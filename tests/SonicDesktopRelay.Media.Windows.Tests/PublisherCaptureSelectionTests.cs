using SonicDesktopRelay.App;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class PublisherCaptureSelectionTests
{
    [Fact]
    public void Selects_monitor_capture_and_system_loopback_only_for_monitor_target()
    {
        var systemAudioCalls = 0;
        var processAudioCalls = 0;
        var selection = new PublisherCaptureSelection(
            () => new FakeVideoSource(), () => new FakeVideoSource(),
            () => { systemAudioCalls++; return new FakeAudioSource(); },
            _ => { processAudioCalls++; return new FakeAudioSource(); }, () => true);

        var capture = selection.CreateVideo(new CaptureTarget.Monitor(new MonitorInfo("D1", "Display", 100, 100, true)));
        var audio = selection.CreateAudio(new CaptureTarget.Monitor(new MonitorInfo("D1", "Display", 100, 100, true)));

        Assert.IsType<FakeVideoSource>(capture);
        Assert.IsType<FakeAudioSource>(audio);
        Assert.Equal(1, systemAudioCalls);
        Assert.Equal(0, processAudioCalls);
    }

    [Fact]
    public void Selects_window_video_and_process_audio_without_constructing_system_loopback()
    {
        var systemAudioCalls = 0;
        var window = new WindowInfo((nint)2, 9, DateTime.UnixEpoch, "Editor", "editor", 800, 600);
        var selection = new PublisherCaptureSelection(
            () => new FakeVideoSource(), () => new FakeVideoSource(),
            () => { systemAudioCalls++; return new FakeAudioSource(); },
            target => new ProcessLoopbackAudioSource(target, new NeverStartingFactory(), isSupported: true), () => true);
        var target = new CaptureTarget.Window(window);

        Assert.IsType<FakeVideoSource>(selection.CreateVideo(target));
        var audio = Assert.IsType<ProcessLoopbackAudioSource>(selection.CreateAudio(target));
        Assert.Equal((uint)9, audio.TargetProcessId);
        Assert.Equal(0, systemAudioCalls);
    }

    [Fact]
    public void Unsupported_process_loopback_returns_no_audio_and_never_uses_system_loopback()
    {
        var systemAudioCalls = 0;
        var processAudioCalls = 0;
        var selection = new PublisherCaptureSelection(
            () => new FakeVideoSource(), () => new FakeVideoSource(),
            () => { systemAudioCalls++; return new FakeAudioSource(); },
            _ => { processAudioCalls++; return new FakeAudioSource(); }, () => false);
        var target = new CaptureTarget.Window(new WindowInfo((nint)2, 9, DateTime.UnixEpoch, "Editor", "editor", 800, 600));

        Assert.Null(selection.CreateAudio(target));
        Assert.Equal(0, systemAudioCalls);
        Assert.Equal(0, processAudioCalls);
    }

    private sealed class NeverStartingFactory : IProcessLoopbackClientFactory
    {
        public IProcessLoopbackClient Create(uint targetPid, bool includeProcessTree, int sampleRate, int channels, int bitsPerSample, int frameSamples)
            => throw new InvalidOperationException("Must not activate in source-selection test.");
    }

    private sealed class FakeVideoSource : IScreenCaptureSource
    {
        public MonitorInfo Monitor => new("fake", "fake", 100, 100, true);
        public event Action<VideoFrame>? FrameCaptured { add { } remove { } }
        public event Action<string>? TargetClosed { add { } remove { } }
        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct) => Task.CompletedTask;
        public void SetFrameRate(int framesPerSecond) { }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioSource : IAudioCaptureSource
    {
        public event Action<AudioFrame>? AudioCaptured { add { } remove { } }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
