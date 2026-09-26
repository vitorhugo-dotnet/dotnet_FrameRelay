using SonicDesktopRelay.Media.Windows;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.App;
using SonicDesktopRelay.ApiClient;
using System.Reflection;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class DiscordExcludingAudioSourceTests
{
    [Fact]
    public async Task Preference_and_Discord_start_exit_restart_switch_only_capture()
    {
        var factory = new Factory();
        uint? pid = null;
        await using var source = new DiscordExcludingAudioSource(factory, () => pid, () => true, monitor: false);
        var frames = 0;
        source.AudioCaptured += frame => { Assert.Equal(48000, frame.SampleRate); Assert.Equal(2, frame.Channels); frames++; };
        await source.StartAsync(default);
        factory.Current!.Push();
        await source.SetIgnoreDiscordAudioAsync(true);
        Assert.Equal(2, factory.NormalCalls);
        pid = 42;
        var old = factory.Current!;
        old.Push(); // Never forward an endpoint packet after Discord appeared.
        Assert.Equal(1, frames);
        await source.RefreshAsync();
        Assert.Equal((uint)42, factory.Excluded.Single());
        Assert.True(old.Disposed);
        factory.Current!.Push();
        Assert.Equal(2, frames);
        pid = 99;
        await source.RefreshAsync();
        Assert.Equal(new uint[] {42, 99}, factory.Excluded);
        pid = null;
        await source.RefreshAsync();
        Assert.Equal(3, factory.NormalCalls);
        pid = 100;
        await source.RefreshAsync();
        await source.SetIgnoreDiscordAudioAsync(false);
        Assert.Equal(4, factory.NormalCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Unsupported_or_failed_exclusion_is_silent_without_endpoint_fallback(bool supported, bool fail)
    {
        var factory = new Factory { FailExclusion = fail };
        await using var source = new DiscordExcludingAudioSource(factory, () => 42, () => supported, monitor: false);
        var diagnostics = 0;
        source.DiagnosticsChanged += () => diagnostics++;
        await source.SetIgnoreDiscordAudioAsync(true);
        await source.StartAsync(default);
        Assert.NotNull(source.DegradedReason);
        Assert.Equal(0, factory.NormalCalls);
        Assert.Equal(1, diagnostics);
    }

    [Fact]
    public async Task Detection_failure_suppresses_existing_unfiltered_capture()
    {
        var factory = new Factory();
        var fail = false;
        await using var source = new DiscordExcludingAudioSource(factory,
            () => fail ? throw new InvalidOperationException("lookup denied") : null, () => true, monitor: false);
        await source.SetIgnoreDiscordAudioAsync(true);
        await source.StartAsync(default);
        var frames = 0;
        source.AudioCaptured += _ => frames++;
        fail = true;
        factory.Current!.Push();
        await source.RefreshAsync();
        Assert.Equal(0, frames);
        Assert.True(factory.Current.Disposed);
        Assert.Contains("lookup denied", source.DegradedReason);
    }

    [Fact]
    public async Task Active_host_switch_keeps_video_capture_and_audio_encoder_alive()
    {
        var factory = new Factory();
        var source = new DiscordExcludingAudioSource(factory, () => 42, () => true, monitor: false);
        var audioEncoder = new AudioEncoder();
        var audio = new AudioPublishPipeline(source, audioEncoder, new MediaSessionClock(TimeProvider.System));
        var capture = new VideoCapture();
        var video = new ScreenPublishPipeline(capture, new VideoEncoder());
        await video.StartAsync(capture.Monitor, default);
        await audio.StartAsync(default);
        using var http = new HttpClient();
        await using var host = new RtcVideoPublishHost(new IceApiClient(http), () => null);
        // Install already-running fake media without activating D3D/MF or connecting signaling.
        SetField(host, "_pipeline", video);
        SetField(host, "_audioPipeline", audio);
        SetField(host, "_audioSource", source);
        factory.Current!.Push();
        await host.SetIgnoreDiscordAudioAsync(true);
        factory.Current!.Push();
        await host.SetIgnoreDiscordAudioAsync(false);
        factory.Current!.Push();
        Assert.Equal(3, audioEncoder.Calls);
        Assert.False(audioEncoder.Disposed);
        Assert.Equal(1, capture.Starts);
        Assert.Equal(0, capture.Stops);
        Assert.Null(host.AudioDegradedReason);
    }

    private static void SetField(RtcVideoPublishHost host, string name, object value)
        => typeof(RtcVideoPublishHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);

    [Fact]
    public async Task Partial_endpoint_frame_spanning_Discord_start_exit_is_discarded()
    {
        var factory = new Factory();
        uint? pid = null;
        await using var source = new DiscordExcludingAudioSource(factory, () => pid, () => true, monitor: false);
        await source.SetIgnoreDiscordAudioAsync(true);
        await source.StartAsync(default);
        var frames = new List<AudioFrame>();
        source.AudioCaptured += frames.Add;
        factory.Current!.Push(new byte[1000]);
        pid = 42;
        factory.Current.Push(Enumerable.Repeat((byte)123, 1000).ToArray());
        pid = null;
        // The PID returned to null before the timer or a completed frame observed it.
        factory.Current.Push(new byte[1840]);
        Assert.Empty(frames);
        await source.RefreshAsync();
        factory.Current!.Push();
        Assert.All(Assert.Single(frames).Data.ToArray(), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task Enable_during_start_gate_closes_audio_before_waiting_for_startup()
    {
        var factory = new Factory();
        var source = new DiscordExcludingAudioSource(factory, () => 42, () => true, monitor: false);
        var encoder = new AudioEncoder();
        var audio = new AudioPublishPipeline(source, encoder, new MediaSessionClock(TimeProvider.System));
        await audio.StartAsync(default);
        using var http = new HttpClient();
        await using var host = new RtcVideoPublishHost(new IceApiClient(http), () => null);
        SetField(host, "_audioPipeline", audio);
        SetField(host, "_audioSource", source);
        var gate = (SemaphoreSlim)typeof(RtcVideoPublishHost)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        factory.Current!.Push();
        Task? update = null;
        await gate.WaitAsync(); // Models StartAsync while startup still owns the lifecycle gate.
        try
        {
            update = host.SetIgnoreDiscordAudioAsync(true);
            Assert.False(update.IsCompleted);
            factory.Current.Push();
            Assert.Equal(1, encoder.Calls);
        }
        finally
        {
            gate.Release();
            if (update is not null) await update;
        }
        factory.Current!.Push();
        Assert.Equal(2, encoder.Calls);
        Assert.False(encoder.Disposed);
        Assert.Equal((uint)42, factory.Excluded.Single());
    }

    [Fact]
    public async Task Cancellation_after_recorder_creation_disposes_native_resources()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new Factory { OnCreated = cancellation.Cancel };
        await using var source = new DiscordExcludingAudioSource(factory, () => null, () => true, monitor: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.StartAsync(cancellation.Token));
        Assert.True(factory.Current!.Disposed);
    }

    [Fact]
    public void Desktop_detection_selects_root_and_ignores_browsers()
    {
        var processes = new Dictionary<uint, (uint Parent, string Name)>
        {
            [10] = (1, "chrome.exe"),
            [11] = (10, "chrome.exe"),
            [42] = (1, "Discord.exe"),
            [43] = (42, "Discord.exe"),
            [44] = (43, "Discord.exe")
        };
        Assert.Equal((uint)42, DiscordDesktopProcess.SelectRoot(processes));
        processes.Remove(42);
        processes.Remove(43);
        processes.Remove(44);
        Assert.Null(DiscordDesktopProcess.SelectRoot(processes));
        processes[99] = (1, "DiscordCanary.exe");
        Assert.Equal((uint)99, DiscordDesktopProcess.SelectRoot(processes));
        processes[100] = (1, "DiscordPTB.exe");
        Assert.Throws<InvalidOperationException>(() => DiscordDesktopProcess.SelectRoot(processes));
    }

    [Fact]
    public async Task Monitor_detects_Discord_while_endpoint_is_silent()
    {
        var factory = new Factory();
        var process = 0;
        await using var source = new DiscordExcludingAudioSource(factory,
            () => Volatile.Read(ref process) == 0 ? null : 42u, () => true);
        await source.SetIgnoreDiscordAudioAsync(true);
        await source.StartAsync(default);
        Volatile.Write(ref process, 1);
        await factory.ExclusionCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync();
        Assert.Equal((uint)42, factory.Excluded.Single());
        Assert.True(factory.Current!.Disposed);
    }

    [Fact]
    public async Task Native_capture_failure_notifies_diagnostics_and_stops_delivering_audio()
    {
        var factory = new Factory();
        await using var source = new DiscordExcludingAudioSource(factory, () => 42, () => true, monitor: false);
        await source.SetIgnoreDiscordAudioAsync(true);
        var diagnostics = 0;
        var frames = 0;
        source.DiagnosticsChanged += () => diagnostics++;
        source.AudioCaptured += _ => frames++;
        await source.StartAsync(default);
        factory.Current!.Fail(new InvalidOperationException("device lost"));
        factory.Current.Push();
        Assert.Equal(2, diagnostics);
        Assert.Equal(0, frames);
        Assert.Contains("device lost", source.DegradedReason);
    }

    private sealed class AudioEncoder : IAudioEncoder
    {
        public string Name => "fake";
        public int Calls;
        public bool Disposed;
        public EncodedAudioSample? Encode(AudioFrame frame)
        {
            Calls++;
            Assert.Equal(960, frame.SampleCount);
            return new EncodedAudioSample(new byte[] { 1 }, 960, frame.Duration, frame.Timestamp);
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class VideoEncoder : IVideoEncoder
    {
        public string Name => "fake";
        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality) => null;
        public void RequestKeyFrame() { }
        public void Dispose() { }
    }

    private sealed class VideoCapture : IScreenCaptureSource
    {
        public MonitorInfo Monitor => new("fake", "fake", 1920, 1080, true);
        public int Starts, Stops;
        public event Action<VideoFrame>? FrameCaptured { add { } remove { } }
        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct)
        { Starts++; return Task.CompletedTask; }
        public void SetFrameRate(int rate) { }
        public Task StopAsync() { Stops++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Factory : IWasapiLoopbackRecorderFactory
    {
        public int NormalCalls;
        public List<uint> Excluded = [];
        public Recorder? Current;
        public bool FailExclusion;
        public Action? OnCreated;
        public TaskCompletionSource ExclusionCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IWasapiLoopbackRecorder Create(int rate, int channels, int bits, int buffer)
        { NormalCalls++; Current = new Recorder(); OnCreated?.Invoke(); return Current; }
        public Task<IWasapiLoopbackRecorder> CreateExcludingAsync(uint pid, int rate, int channels, int bits, int buffer)
        {
            Excluded.Add(pid);
            if (FailExclusion) throw new InvalidOperationException("activation failed");
            ExclusionCreated.TrySetResult();
            return Task.FromResult<IWasapiLoopbackRecorder>(Current = new Recorder());
        }
    }

    private sealed class Recorder : IWasapiLoopbackRecorder
    {
        public string EndpointId => "fake";
        public string EndpointName => "fake";
        public bool Disposed;
        public event WasapiPcmDataAvailableHandler? DataAvailable;
        public event Action<Exception?>? Stopped;
        public void Start() { }
        public void Stop() { }
        public void Push() => DataAvailable?.Invoke(new byte[3840]);
        public void Push(byte[] bytes) => DataAvailable?.Invoke(bytes);
        public void Fail(Exception error) => Stopped?.Invoke(error);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
