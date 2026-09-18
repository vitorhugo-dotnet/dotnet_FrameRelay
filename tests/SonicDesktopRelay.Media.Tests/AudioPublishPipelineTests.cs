using Microsoft.Extensions.Time.Testing;

namespace SonicDesktopRelay.Media.Tests;

public sealed class AudioPublishPipelineTests
{
    [Fact]
    public async Task One_captured_frame_is_encoded_once_and_stamped_from_session_clock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var clock = new MediaSessionClock(time);
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new AudioPublishPipeline(capture, encoder, clock);
        var samples = new List<EncodedAudioSample>();
        pipeline.SampleEncoded += samples.Add;

        await pipeline.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(100));
        capture.Push(Pcm20Ms(TimeSpan.FromHours(7)));

        Assert.Equal(1, encoder.EncodeCalls);
        Assert.Single(samples);
        Assert.Equal(TimeSpan.FromMilliseconds(100), samples[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(20), samples[0].Duration);
    }

    [Fact]
    public async Task Terminal_encoder_failure_is_reported_once_and_stops_future_encoding()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder { Failure = new InvalidOperationException("codec failed") };
        await using var pipeline = new AudioPublishPipeline(
            capture,
            encoder,
            new MediaSessionClock(new FakeTimeProvider(DateTimeOffset.UnixEpoch)));
        var failures = new List<Exception>();
        pipeline.Failed += failures.Add;

        await pipeline.StartAsync(CancellationToken.None);
        capture.Push(Pcm20Ms(TimeSpan.Zero));
        capture.Push(Pcm20Ms(TimeSpan.FromMilliseconds(20)));

        Assert.Equal(1, encoder.EncodeCalls);
        Assert.Single(failures);
        Assert.Equal("codec failed", failures[0].Message);
    }

    private static AudioFrame Pcm20Ms(TimeSpan timestamp) =>
        new(new byte[960 * 2 * 2], 48_000, 2, 960, timestamp);

    private sealed class FakeCapture : IAudioCaptureSource
    {
        public event Action<AudioFrame>? AudioCaptured;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public Task StartAsync(CancellationToken ct) { StartCalls++; return Task.CompletedTask; }
        public Task StopAsync() { StopCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Push(AudioFrame frame) => AudioCaptured?.Invoke(frame);
    }

    private sealed class FakeEncoder : IAudioEncoder
    {
        public string Name => "fake-opus";
        public int EncodeCalls { get; private set; }
        public Exception? Failure { get; init; }

        public EncodedAudioSample? Encode(AudioFrame frame)
        {
            EncodeCalls++;
            if (Failure is not null) throw Failure;
            return new EncodedAudioSample(new byte[] { 1, 2, 3 }, frame.SampleCount, frame.Duration, frame.Timestamp);
        }

        public void Dispose() { }
    }
}
