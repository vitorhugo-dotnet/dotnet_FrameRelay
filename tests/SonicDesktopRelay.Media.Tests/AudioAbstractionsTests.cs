namespace SonicDesktopRelay.Media.Tests;

public sealed class AudioAbstractionsTests
{
    [Fact]
    public async Task Audio_abstractions_use_only_project_media_contracts()
    {
        IAudioCaptureSource capture = new FakeCapture();
        IAudioEncoder encoder = new FakeEncoder();
        IAudioDecoder decoder = new FakeDecoder();
        IAudioSink sink = new FakeSink();

        await capture.StartAsync(CancellationToken.None);
        var frame = new AudioFrame(new byte[3840], 48_000, 2, 960, TimeSpan.Zero);
        var encoded = encoder.Encode(frame);
        Assert.NotNull(encoded);
        var decoded = decoder.Decode(encoded!.Value);
        Assert.NotNull(decoded);
        await sink.StartAsync(CancellationToken.None);
        sink.Write(decoded!.Value);

        await capture.StopAsync();
        await sink.StopAsync();
        encoder.Dispose();
        decoder.Dispose();
        await capture.DisposeAsync();
        await sink.DisposeAsync();
    }

    private sealed class FakeCapture : IAudioCaptureSource
    {
        public event Action<AudioFrame>? AudioCaptured;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Push(AudioFrame frame) => AudioCaptured?.Invoke(frame);
    }

    private sealed class FakeEncoder : IAudioEncoder
    {
        public string Name => "fake-encoder";
        public EncodedAudioSample? Encode(AudioFrame frame) =>
            new(new byte[] { 1 }, frame.SampleCount, frame.Duration, frame.Timestamp);
        public void Dispose() { }
    }

    private sealed class FakeDecoder : IAudioDecoder
    {
        public string Name => "fake-decoder";
        public AudioFrame? Decode(EncodedAudioSample sample) =>
            new(new byte[3840], 48_000, 2, sample.SampleCount, sample.Timestamp);
        public void Dispose() { }
    }

    private sealed class FakeSink : IAudioSink
    {
        public string Name => "fake-sink";
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public void Write(AudioFrame frame) { }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
