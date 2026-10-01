using SonicDesktopRelay.Media.WebSocket;

namespace SonicDesktopRelay.Media.Tests;

public sealed class WebSocketMediaTests
{
    [Theory]
    [InlineData(null, true)] [InlineData("false", false)] [InlineData("1", false)]
    [InlineData("", false)] [InlineData("true", true)] [InlineData("TRUE", true)]
    public void Publisher_defaults_on_and_respects_environment_override(string? value, bool expected) =>
        Assert.Equal(expected, WebSocketMediaOptions.FromEnvironment(_ => value).Enabled);

    [Fact]
    public async Task Queue_copies_native_sample_memory_and_bounds_backlog()
    {
        var queue = new OwnedMediaQueue(); var data = new byte[] { 7 };
        var message = new MediaMessage(2, 1, 1, 0, 0, 1, data);
        Assert.True(queue.TryEnqueue(message)); data[0] = 99;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Equal(7, (await queue.ReadAsync(budget.Token)).Payload.Span[0]);
        for (var i = 0; i < 64; i++) Assert.True(queue.TryEnqueue(message));
        Assert.False(queue.TryEnqueue(message)); queue.Clear();
        Assert.False(queue.TryEnqueue(message with { Payload = new byte[8 * 1024 * 1024] }));
    }

    private sealed class Capture : IScreenCaptureSource
    {
        public MonitorInfo Monitor => new("test", "test", 2, 2, true);
        public event Action<VideoFrame>? FrameCaptured;
        public void Emit(byte[] bytes) => FrameCaptured?.Invoke(new VideoFrame(2, 2, bytes, TimeSpan.Zero));
        public void SetFrameRate(int framesPerSecond) { }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Encoder : IVideoEncoder
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<byte> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Continue = new(false);
        public string Name => "fake";
        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality)
        {
            Started.TrySetResult(); Continue.Wait(TimeSpan.FromSeconds(3)); Result.TrySetResult(frame.Bgra.Span[0]); return null;
        }
        public void RequestKeyFrame() { }
        public void Dispose() { Continue.Dispose(); }
    }
    [Fact]
    public async Task Independent_encoder_owns_raw_frame_after_capture_returns()
    {
        var capture = new Capture(); var encoder = new Encoder();
        await using var branch = new ActivityH264Output(capture, encoder, new MediaSessionClock(TimeProvider.System), () => VideoQuality.Default, () => true);
        var data = new byte[16]; data[0] = 7; capture.Emit(data);
        await encoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); data[0] = 99; encoder.Continue.Set();
        Assert.Equal(7, await encoder.Result.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
