using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using Xunit;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class WasapiLoopbackAudioSourceTests
{
    [Fact]
    public async Task Start_and_stop_are_idempotent_and_expose_endpoint_diagnostics()
    {
        var factory = new FakeRecorderFactory();
        await using var source = new WasapiLoopbackAudioSource(factory);

        await source.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);

        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(1, factory.Recorder!.StartCalls);
        Assert.Equal("render-1", source.ActiveEndpointId);
        Assert.Equal("Fake Speakers", source.ActiveEndpointName);
        Assert.Equal(48_000, source.NormalizedSampleRate);
        Assert.Equal(2, source.NormalizedChannels);
        Assert.Null(source.DegradedReason);

        await source.StopAsync();
        await source.StopAsync();

        Assert.Equal(1, factory.Recorder.StopCalls);
    }

    [Fact]
    public async Task Arbitrary_callbacks_emit_one_audio_frame_per_full_20ms_packet()
    {
        var factory = new FakeRecorderFactory();
        await using var source = new WasapiLoopbackAudioSource(factory);
        var frames = new List<AudioFrame>();
        source.AudioCaptured += frames.Add;
        await source.StartAsync(CancellationToken.None);

        factory.Recorder!.Push(new byte[1_000]);
        factory.Recorder.Push(new byte[2_840]);

        var frame = Assert.Single(frames);
        Assert.Equal(48_000, frame.SampleRate);
        Assert.Equal(2, frame.Channels);
        Assert.Equal(960, frame.SampleCount);
        Assert.Equal(960 * 2 * sizeof(short), frame.Data.Length);
    }

    [Fact]
    public async Task Recorder_failure_degrades_once_and_ignores_late_audio()
    {
        var factory = new FakeRecorderFactory();
        await using var source = new WasapiLoopbackAudioSource(factory);
        var frames = 0;
        source.AudioCaptured += _ => frames++;
        await source.StartAsync(CancellationToken.None);

        factory.Recorder!.Fail(new InvalidOperationException("endpoint removed"));
        factory.Recorder.Push(new byte[960 * 2 * sizeof(short)]);

        Assert.NotNull(source.DegradedReason);
        Assert.Contains("endpoint removed", source.DegradedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, frames);
    }

    private sealed class FakeRecorderFactory : IWasapiLoopbackRecorderFactory
    {
        public int CreateCalls { get; private set; }
        public FakeRecorder? Recorder { get; private set; }

        public IWasapiLoopbackRecorder Create(int sampleRate, int channels, int bitsPerSample, int bufferMilliseconds)
        {
            CreateCalls++;
            Assert.Equal(48_000, sampleRate);
            Assert.Equal(2, channels);
            Assert.Equal(16, bitsPerSample);
            Assert.Equal(20, bufferMilliseconds);
            Recorder = new FakeRecorder();
            return Recorder;
        }
    }

    private sealed class FakeRecorder : IWasapiLoopbackRecorder
    {
        public string EndpointId => "render-1";
        public string EndpointName => "Fake Speakers";
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public event WasapiPcmDataAvailableHandler? DataAvailable;
        public event Action<Exception?>? Stopped;

        public void Start() => StartCalls++;

        public void Stop() => StopCalls++;

        public void Push(byte[] data) => DataAvailable?.Invoke(data);

        public void Fail(Exception error) => Stopped?.Invoke(error);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
