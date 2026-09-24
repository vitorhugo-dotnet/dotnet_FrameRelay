namespace SonicDesktopRelay.Media.Tests;

public sealed class AudioWatchPipelineTests
{
    [Fact]
    public async Task Encoded_audio_is_decoded_and_written_to_sink()
    {
        var decoder = new FakeDecoder();
        var sink = new FakeSink();
        await using var pipeline = new AudioWatchPipeline(decoder, sink);
        await pipeline.StartAsync(CancellationToken.None);

        pipeline.Push(Sample());

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Single(sink.Frames);
        Assert.Equal(TimeSpan.FromMilliseconds(120), sink.Frames[0].Timestamp);
    }

    [Fact]
    public async Task Decoder_failure_is_reported_once_and_stops_future_packets()
    {
        var decoder = new FakeDecoder { Failure = new InvalidOperationException("decode failed") };
        var sink = new FakeSink();
        await using var pipeline = new AudioWatchPipeline(decoder, sink);
        var failures = new List<Exception>();
        pipeline.Failed += failures.Add;
        await pipeline.StartAsync(CancellationToken.None);

        pipeline.Push(Sample());
        pipeline.Push(Sample());

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Empty(sink.Frames);
        Assert.Single(failures);
    }

    [Fact]
    public async Task Sink_failure_is_reported_once_and_stops_future_writes()
    {
        var decoder = new FakeDecoder();
        var sink = new FakeSink { Failure = new InvalidOperationException("playback failed") };
        await using var pipeline = new AudioWatchPipeline(decoder, sink);
        var failures = new List<Exception>();
        pipeline.Failed += failures.Add;
        await pipeline.StartAsync(CancellationToken.None);

        pipeline.Push(Sample());
        pipeline.Push(Sample());

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Equal(1, sink.WriteCalls);
        Assert.Single(failures);
    }

    private static EncodedAudioSample Sample() =>
        new(new byte[] { 1, 2, 3 }, 960, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(120));

    private sealed class FakeDecoder : IAudioDecoder
    {
        public string Name => "fake-opus";
        public int DecodeCalls { get; private set; }
        public Exception? Failure { get; init; }

        public AudioFrame? Decode(EncodedAudioSample sample)
        {
            DecodeCalls++;
            if (Failure is not null) throw Failure;
            return new AudioFrame(new byte[960 * 2 * 2], 48_000, 2, sample.SampleCount, sample.Timestamp);
        }

        public void Dispose() { }
    }

    private sealed class FakeSink : IAudioSink
    {
        public string Name => "fake-sink";
        public float Volume { get; set; } = 1f;
        public bool IsMuted { get; set; }
        public List<AudioFrame> Frames { get; } = [];
        public int WriteCalls { get; private set; }
        public Exception? Failure { get; init; }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public void Write(AudioFrame frame)
        {
            WriteCalls++;
            if (Failure is not null) throw Failure;
            Frames.Add(frame);
        }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
