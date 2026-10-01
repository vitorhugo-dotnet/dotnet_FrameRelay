using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SonicDesktopRelay.Media.WebSocket;

public sealed record MediaUploadGrant(string Grant, string MediaUrl);

public sealed class WebSocketMediaPublisher(Func<Guid, CancellationToken, Task<MediaUploadGrant>> grants,
    Action requestKeyFrame, bool hasAudio, ILogger<WebSocketMediaPublisher>? logger = null,
    Func<TimeSpan, CancellationToken, Task>? retryWait = null) : IAsyncDisposable
{
    private readonly ILogger log = logger ?? NullLogger<WebSocketMediaPublisher>.Instance;
    private readonly Func<TimeSpan, CancellationToken, Task> waitBeforeRetry = retryWait ?? Task.Delay;
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private OwnedMediaQueue? queue;
    private uint generation, sequence;
    private bool recovery = true;
    private int width, height;
    private byte[]? sps, pps;
    private string? codec;
    private DateTimeOffset lastKeyRequest;

    public Task StartAsync(Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        worker ??= Task.Run(() => RunAsync(sessionId, stop.Token));
        return Task.CompletedTask; // Optional transport never delays RTC startup.
    }
    public void PublishVideo(EncodedVideoSample sample)
    {
        if (sample.Codec != VideoCodec.H264) return;
        try
        {
            lock (gate)
            {
                if (queue is null) return;
                if (width != sample.Width || height != sample.Height) { width = sample.Width; height = sample.Height; sps = pps = null; codec = null; Recover(); }
                foreach (var nal in Nals(sample.Data.Span))
                {
                    if ((nal[0] & 31) == 7 && nal.Length >= 4)
                    {
                        var next = $"avc1.{nal[1]:x2}{nal[2]:x2}{nal[3]:x2}";
                        if (codec != next || (sps is not null && !sps.AsSpan().SequenceEqual(nal))) Recover();
                        codec = next; sps = nal;
                    }
                    if ((nal[0] & 31) == 8) pps = nal;
                }
                if (recovery)
                {
                    if (!sample.IsKeyFrame || sps is null || pps is null || codec is null) { RequestKey(); return; }
                    if (generation == uint.MaxValue) throw new InvalidDataException("Generation exhausted.");
                    generation++; sequence = 0; queue.Clear();
                    var config = JsonSerializer.SerializeToUtf8Bytes(new { video = new { codec, width, height, format = "annexb" },
                        audio = hasAudio ? new { codec = "opus", sampleRate = 48000, channels = 2 } : null });
                    queue.TryEnqueue(new MediaMessage(1, 0, generation, sequence++, 0, 0, config)); recovery = false;
                }
                var data = sample.Data;
                if (sample.IsKeyFrame && sps is not null && pps is not null)
                {
                    var complete = new byte[8 + sps.Length + pps.Length + data.Length];
                    new byte[] { 0, 0, 0, 1 }.CopyTo(complete, 0); sps.CopyTo(complete, 4);
                    new byte[] { 0, 0, 0, 1 }.CopyTo(complete, 4 + sps.Length); pps.CopyTo(complete, 8 + sps.Length);
                    data.Span.CopyTo(complete.AsSpan(8 + sps.Length + pps.Length)); data = complete;
                }
                Enqueue(2, sample.IsKeyFrame ? (ushort)1 : (ushort)0, sample.Timestamp, sample.Duration, data);
            }
        }
        catch (Exception ex) { log.LogWarning("Discord media sample rejected: {ErrorType}", ex.GetType().Name); lock (gate) Recover(); }
    }
    public void PublishAudio(EncodedAudioSample sample)
    {
        try { lock (gate) { if (queue is not null && !recovery && hasAudio) Enqueue(3, 0, sample.Timestamp, sample.Duration, sample.Data); } }
        catch (Exception ex) { log.LogWarning("Discord audio sample rejected: {ErrorType}", ex.GetType().Name); lock (gate) Recover(); }
    }
    private void Enqueue(byte type, ushort flags, TimeSpan timestamp, TimeSpan duration, ReadOnlyMemory<byte> data)
    {
        if (sequence == uint.MaxValue || !queue!.TryEnqueue(new MediaMessage(type, flags, generation, sequence++, timestamp.Ticks / 10, duration.Ticks / 10, data))) Recover();
    }
    private void Recover() { recovery = true; queue?.Clear(); RequestKey(); }
    private void RequestKey()
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow - lastKeyRequest < TimeSpan.FromSeconds(1)) return;
            lastKeyRequest = DateTimeOffset.UtcNow;
        }
        try { requestKeyFrame(); } catch (Exception ex) { log.LogWarning("Discord keyframe request failed: {ErrorType}", ex.GetType().Name); }
    }

    private async Task RunAsync(Guid sessionId, CancellationToken ct)
    {
        var delay = 1;
        while (!ct.IsCancellationRequested)
        {
            var transferredMedia = 0;
            using var socket = new ClientWebSocket(); using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct); connect.CancelAfter(TimeSpan.FromSeconds(12));
                var grant = await grants(sessionId, connect.Token);
                var url = new UriBuilder(grant.MediaUrl);
                if (url.Scheme == "https") url.Scheme = "wss"; else if (url.Scheme == "http") url.Scheme = "ws";
                socket.Options.AddSubProtocol("framerelay-media-v1");
                await socket.ConnectAsync(url.Uri, connect.Token);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { grant = grant.Grant }).AsMemory(), WebSocketMessageType.Text, true, connect.Token);
                var owned = new OwnedMediaQueue(); lock (gate) { queue = owned; Recover(); }
                var sending = SendAsync(socket, owned, connection.Token,
                    () => Interlocked.Exchange(ref transferredMedia, 1));
                var receiving = ReceiveAsync(socket, connection.Token);
                await Task.WhenAny(sending, receiving); connection.Cancel(); socket.Abort();
                try { await Task.WhenAll(sending, receiving); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException) { }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("Discord media upload unavailable: {ErrorType}; retrying", ex.GetType().Name); }
            finally { connection.Cancel(); socket.Abort(); lock (gate) { queue?.Clear(); queue = null; recovery = true; } }
            if (Volatile.Read(ref transferredMedia) != 0) delay = 1;
            try { await waitBeforeRetry(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; }
            delay = Math.Min(30, delay * 2);
        }
    }
    private static async Task SendAsync(
        ClientWebSocket socket,
        OwnedMediaQueue owned,
        CancellationToken ct,
        Action mediaTransferred)
    {
        while (true)
        {
            var sample = await owned.ReadAsync(ct);
            using var send = CancellationTokenSource.CreateLinkedTokenSource(ct); send.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.SendAsync(MediaWireCodec.Encode(sample).AsMemory(), WebSocketMessageType.Binary, true, send.Token);
            if (sample.Type is 2 or 3) mediaTransferred();
        }
    }
    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var data = new byte[41]; var last = DateTimeOffset.MinValue;
        while (true)
        {
            var length = 0; ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(data.AsMemory(length), ct);
                if (received.MessageType == WebSocketMessageType.Close) return;
                length += received.Count;
                if (received.MessageType != WebSocketMessageType.Binary || length > 40)
                    throw new InvalidDataException("Invalid media control.");
            } while (!received.EndOfMessage);
            if (length != 40 || MediaWireCodec.Decode(data.AsMemory(0, length)).Type != 4)
                throw new InvalidDataException("Invalid media control.");
            if (DateTimeOffset.UtcNow - last >= TimeSpan.FromSeconds(1)) { last = DateTimeOffset.UtcNow; RequestKey(); }
        }
    }
    private static List<byte[]> Nals(ReadOnlySpan<byte> data)
    {
        var output = new List<byte[]>(); int start = -1;
        for (var i = 0; i + 3 <= data.Length; i++)
        {
            var prefix = data[i] == 0 && data[i + 1] == 0 ? (data[i + 2] == 1 ? 3 : i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0) : 0;
            if (prefix == 0) continue;
            if (start >= 0 && i > start && (data[start] & 31) is 7 or 8) output.Add(data[start..i].ToArray());
            start = i + prefix; i += prefix - 1;
        }
        if (start >= 0 && start < data.Length && (data[start] & 31) is 7 or 8) output.Add(data[start..].ToArray());
        return output;
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (worker is not null) { try { await worker; } catch (OperationCanceledException) { } }
        stop.Dispose();
    }
}
