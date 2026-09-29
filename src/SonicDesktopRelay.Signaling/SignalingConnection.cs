using System.Net.WebSockets;
using SonicDesktopRelay.Core;

namespace SonicDesktopRelay.Signaling;

public sealed class SignalingConnection(
    IWebSocketAdapter socket,
    BackendSettings settings,
    Func<CancellationToken, Task<string>> tokenProvider,
    Action<string, Exception?>? diagnostic = null) : ISignalingConnection
{
    private readonly CancellationTokenSource _stopping = new();
    private SignalingState _state = SignalingState.Disconnected;
    private Task? _receiveLoop;
    private Guid _sessionId;

    /// <summary>Kept settable so tests do not have to wait out a real backoff.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    public SignalingState State => _state;

    public event Action<SignalingEnvelope>? FrameReceived;

    public event Action<SignalingState>? StateChanged;

    public async Task StartAsync(Guid sessionId, CancellationToken ct)
    {
        _sessionId = sessionId;
        SetState(SignalingState.Connecting);
        await ConnectAsync(ct);
        SetState(SignalingState.Connected);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stopping.Token), CancellationToken.None);
    }

    public Task SendAsync(string type, Guid? to, object? payload, CancellationToken ct) =>
        socket.SendAsync(SignalingEnvelope.Serializer.ToJson(type, to, payload), ct);

    private async Task ConnectAsync(CancellationToken ct)
    {
        diagnostic?.Invoke("signaling.token.request", null);
        var token = await tokenProvider(ct);
        diagnostic?.Invoke("signaling.token.ready", null);
        diagnostic?.Invoke("signaling.websocket.connecting", null);
        await socket.ConnectAsync(settings.SignalingUri(_sessionId), token, ct);
        diagnostic?.Invoke("signaling.websocket.connected", null);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _state != SignalingState.Terminated)
        {
            string? frame;
            try
            {
                frame = await socket.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (frame is null)
            {
                diagnostic?.Invoke("signaling.websocket.closed", null);
                if (!await TryReconnectAsync(ct)) return;
                continue;
            }

            var envelope = SignalingEnvelope.Serializer.TryParse(frame);
            if (envelope is null) continue;

            if (envelope.Type == SignalingMessageTypes.SessionEnded)
            {
                FrameReceived?.Invoke(envelope);
                SetState(SignalingState.Terminated);
                return;
            }

            FrameReceived?.Invoke(envelope);
        }
    }

    private async Task<bool> TryReconnectAsync(CancellationToken ct)
    {
        // The session outlives a dropped socket: the server holds the participant for its
        // grace period and reports a reconnect as participant.reconnected rather than a new
        // join. Terminal conditions - session.ended, or the app stopping - never get here.
        if (_state == SignalingState.Terminated || ct.IsCancellationRequested) return false;

        SetState(SignalingState.Reconnecting);
        diagnostic?.Invoke("signaling.websocket.reconnecting", null);
        try
        {
            if (ReconnectDelay > TimeSpan.Zero) await Task.Delay(ReconnectDelay, ct);
            await ConnectAsync(ct);
            SetState(SignalingState.Connected);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or WebSocketException or IOException)
        {
            diagnostic?.Invoke("signaling.websocket.reconnect_failed", e);
            SetState(SignalingState.Disconnected);
            return false;
        }
    }

    private void SetState(SignalingState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        await socket.DisposeAsync();
        _stopping.Dispose();
    }
}
