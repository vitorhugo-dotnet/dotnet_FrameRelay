using SonicDesktopRelay.Media;
using SonicDesktopRelay.Signaling;
using Xunit;

namespace SonicDesktopRelay.Presentation.Tests;

public sealed class SignalingDiagnosticsTests
{
    private static readonly Guid SessionId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

    [Fact]
    public async Task Offer_received_during_signaling_start_is_recorded_while_still_joining()
    {
        var from = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var to = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var connection = new EmittingConnection(
            new SignalingEnvelope(
                SignalingMessageTypes.WebRtcOffer,
                null,
                SessionId,
                from,
                to,
                null,
                null));
        var runtime = new SessionRuntime(
            new FakeSessionApi(),
            () => connection,
            watchHost: new FakeVideoWatchHost());

        await runtime.StartWatchingAsync("AB12CD", CancellationToken.None);

        var entry = Assert.Single(runtime.SignalingDiagnostics);

        Assert.Equal(SignalingDirection.RX, entry.Direction);
        Assert.Equal(SignalingMessageTypes.WebRtcOffer, entry.Type);
        Assert.Equal(SessionPhase.Joining, entry.Phase);
        Assert.Equal(SignalingState.Connected, entry.Signaling);
        Assert.Equal(from, entry.From);
        Assert.Equal(to, entry.To);
        Assert.False(entry.Handled);
    }

    [Fact]
    public void Signaling_diagnostics_keep_only_the_newest_200_entries()
    {
        var buffer = new SignalingDiagnosticBuffer();

        for (var i = 0; i < 200; i++)
            buffer.Add(Diagnostic(i));

        Assert.Equal(200, buffer.Entries.Count);

        buffer.Add(Diagnostic(200));

        Assert.Equal(200, buffer.Entries.Count);
        Assert.Equal("type.1", buffer.Entries[0].Type);
        Assert.Equal("type.200", buffer.Entries[^1].Type);
    }

    private static SignalingDiagnosticEntry Diagnostic(int index) =>
        new(
            DateTimeOffset.UnixEpoch.AddMilliseconds(index),
            SignalingDirection.RX,
            $"type.{index}",
            SessionPhase.Watching,
            SignalingState.Connected,
            null,
            null,
            true);

    private sealed class FakeSessionApi : ISessionApi
    {
        public Task<CreatedSession> CreateScreenShareAsync(int maxViewers, CancellationToken ct) =>
            Task.FromResult(new CreatedSession(SessionId, "AB12CD"));

        public Task<Guid> JoinAsync(string code, CancellationToken ct) => Task.FromResult(SessionId);

        public Task EndAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EmittingConnection(SignalingEnvelope frame) : ISignalingConnection
    {
        public SignalingState State { get; private set; } = SignalingState.Disconnected;

        public event Action<SignalingEnvelope>? FrameReceived;

        public event Action<SignalingState>? StateChanged;

        public Task StartAsync(Guid sessionId, CancellationToken ct)
        {
            State = SignalingState.Connected;
            StateChanged?.Invoke(State);
            FrameReceived?.Invoke(frame);
            return Task.CompletedTask;
        }

        public Task SendAsync(string type, Guid? to, object? payload, CancellationToken ct) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVideoWatchHost : IVideoWatchHost
    {
        public string? DecoderName => "test";

        public event Action<WatchState>? WatchStateChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public Task HandleSignalingAsync(SignalingEnvelope envelope, CancellationToken ct) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
