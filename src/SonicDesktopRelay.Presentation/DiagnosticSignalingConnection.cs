using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.Presentation;

/// <summary>
/// Adds metadata-only TX diagnostics at the signaling abstraction boundary. The payload is
/// deliberately passed straight through without inspection, serialization or retention.
/// </summary>
public sealed class DiagnosticSignalingConnection(
    ISignalingConnection inner,
    SignalingDiagnosticBuffer diagnostics,
    Func<SessionPhase> phaseProvider) : ISignalingConnection
{
    public SignalingState State => inner.State;

    public event Action<SignalingEnvelope>? FrameReceived
    {
        add => inner.FrameReceived += value;
        remove => inner.FrameReceived -= value;
    }

    public event Action<SignalingState>? StateChanged
    {
        add => inner.StateChanged += value;
        remove => inner.StateChanged -= value;
    }

    public Task StartAsync(Guid sessionId, CancellationToken ct) => inner.StartAsync(sessionId, ct);

    public async Task SendAsync(string type, Guid? to, object? payload, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.Now;
        var phase = phaseProvider();
        var signaling = inner.State;

        await inner.SendAsync(type, to, payload, ct);

        diagnostics.Add(new SignalingDiagnosticEntry(
            timestamp,
            SignalingDirection.TX,
            type,
            phase,
            signaling,
            null,
            to,
            null));
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
