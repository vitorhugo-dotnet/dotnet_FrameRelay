using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonicDesktopRelay.Media.WebSocket;

namespace SonicDesktopRelay.Media.Tests;

public sealed class WebSocketMediaPublisherTests
{
    [Fact]
    public async Task Publisher_keeps_exponential_backoff_when_socket_closes_before_media_transfer()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var waits = new List<TimeSpan>();
        var waitsGate = new object();
        var threeRetries = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
                await AcceptGrantThenCloseAsync(listener, budget.Token);
        });

        await using var publisher = new WebSocketMediaPublisher(
            (_, _) => Task.FromResult(new MediaUploadGrant(
                "test-grant", $"ws://127.0.0.1:{endpoint.Port}/ws/media")),
            () => { },
            false,
            retryWait: (delay, ct) =>
            {
                lock (waitsGate)
                {
                    waits.Add(delay);
                    if (waits.Count >= 3)
                    {
                        threeRetries.TrySetResult();
                        return Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                }

                return Task.CompletedTask;
            });

        await publisher.StartAsync(Guid.NewGuid(), budget.Token);
        await threeRetries.Task.WaitAsync(budget.Token);
        await server.WaitAsync(budget.Token);

        lock (waitsGate)
            Assert.Equal(
                new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) },
                waits.Take(3).ToArray());
    }

    [Fact]
    public async Task Publisher_uploads_config_fresh_keyframe_and_opus_over_real_socket()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyRequests = 0;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(budget.Token); using var stream = tcp.GetStream();
            var header = new StringBuilder(); var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.True(header.Length < 8192); Assert.Equal(1, await stream.ReadAsync(one, budget.Token)); header.Append((char)one[0]);
            }
            Assert.StartsWith("GET /ws/media HTTP/1.1", header.ToString());
            var key = header.ToString().Split("\r\n").Single(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: framerelay-media-v1\r\n\r\n"), budget.Token);
            using var socket = System.Net.WebSockets.WebSocket.CreateFromStream(stream, true, "framerelay-media-v1", TimeSpan.FromSeconds(30));
            var buffer = new byte[8192]; var authentication = await socket.ReceiveAsync(buffer.AsMemory(), budget.Token);
            Assert.Equal(WebSocketMessageType.Text, authentication.MessageType);
            using var grant = JsonDocument.Parse(buffer.AsMemory(0, authentication.Count)); Assert.Equal("test-grant", grant.RootElement.GetProperty("grant").GetString());
            await Task.Delay(1100, budget.Token);
            var request = MediaWireCodec.Encode(new MediaMessage(4, 0, 1, 0, 0, 0, ReadOnlyMemory<byte>.Empty));
            await socket.SendAsync(request.AsMemory(0, 20), WebSocketMessageType.Binary, false, budget.Token);
            await socket.SendAsync(request.AsMemory(20), WebSocketMessageType.Binary, true, budget.Token);
            var messages = new List<MediaMessage>();
            for (var i = 0; i < 3; i++)
            {
                using var complete = new MemoryStream(); ValueWebSocketReceiveResult receive;
                do { receive = await socket.ReceiveAsync(buffer.AsMemory(), budget.Token); complete.Write(buffer, 0, receive.Count); } while (!receive.EndOfMessage);
                messages.Add(MediaWireCodec.Decode(complete.ToArray()));
            }
            return messages;
        });
        await using var publisher = new WebSocketMediaPublisher((_, _) => Task.FromResult(new MediaUploadGrant("test-grant", $"ws://127.0.0.1:{endpoint.Port}/ws/media")), () => { if (Interlocked.Increment(ref keyRequests) >= 2) ready.TrySetResult(); }, true);
        await publisher.StartAsync(Guid.NewGuid(), budget.Token); await ready.Task.WaitAsync(budget.Token);
        publisher.PublishVideo(new EncodedVideoSample(new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0xe0, 0x1f, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1 }, TimeSpan.FromSeconds(1), true, 640, 360, TimeSpan.FromMilliseconds(16.667)));
        publisher.PublishAudio(new EncodedAudioSample(new byte[] { 0xf8, 0xff, 0xfe }, 960, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1)));
        var messages = await server.WaitAsync(budget.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, messages.Select(x => x.Type).ToArray());
        Assert.Equal(new uint[] { 0, 1, 2 }, messages.Select(x => x.Sequence).ToArray());
        using var config = JsonDocument.Parse(messages[0].Payload); Assert.Equal("avc1.42e01f", config.RootElement.GetProperty("video").GetProperty("codec").GetString());
        Assert.Equal(1, messages[1].Flags); Assert.Equal(1000000, messages[1].TimestampUs);
        Assert.Equal(new byte[] { 0xf8, 0xff, 0xfe }, messages[2].Payload.ToArray());
    }
    private static async Task AcceptGrantThenCloseAsync(TcpListener listener, CancellationToken ct)
    {
        using var tcp = await listener.AcceptTcpClientAsync(ct);
        using var stream = tcp.GetStream();
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Assert.True(header.Length < 8192);
            Assert.Equal(1, await stream.ReadAsync(one, ct));
            header.Append((char)one[0]);
        }

        var key = header.ToString().Split("\r\n")
            .Single(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: framerelay-media-v1\r\n\r\n"), ct);

        using var socket = System.Net.WebSockets.WebSocket.CreateFromStream(
            stream, true, "framerelay-media-v1", TimeSpan.FromSeconds(30));
        var buffer = new byte[1024];
        var authentication = await socket.ReceiveAsync(buffer.AsMemory(), ct);
        Assert.Equal(WebSocketMessageType.Text, authentication.MessageType);
        using var grant = JsonDocument.Parse(buffer.AsMemory(0, authentication.Count));
        Assert.Equal("test-grant", grant.RootElement.GetProperty("grant").GetString());

        await socket.CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation, "grant rejected", ct);
    }

}
