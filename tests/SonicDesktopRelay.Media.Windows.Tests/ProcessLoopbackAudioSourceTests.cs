using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using System.Runtime.Versioning;

namespace SonicDesktopRelay.Media.Windows.Tests;

[SupportedOSPlatform("windows10.0.20348.0")]
public sealed class ProcessLoopbackAudioSourceTests
{
    private static readonly WindowInfo Window = new((nint)9, 321, DateTime.UnixEpoch, "Player", "player", 800, 600);

    [Fact]
    public async Task Starts_process_tree_capture_with_normalized_format_and_emits_full_frames()
    {
        var factory = new FakeFactory();
        await using var source = new ProcessLoopbackAudioSource(Window, factory, isSupported: true);
        var frames = new List<AudioFrame>();
        source.AudioCaptured += frames.Add;
        await source.StartAsync(default);
        factory.Client!.Push(new byte[1000]);
        factory.Client.Push(new byte[2840]);

        Assert.Equal((321u, true, 48_000, 2, 16, 960), factory.Parameters);
        var frame = Assert.Single(frames);
        Assert.Equal(960, frame.SampleCount);
        Assert.Equal(3840, frame.Data.Length);
        Assert.Equal((uint)321, source.TargetProcessId);
        Assert.Equal("player", source.TargetProcessName);
        Assert.True(source.IncludesProcessTree);
        Assert.True(source.IsAvailable);
    }

    [Fact]
    public async Task Unsupported_build_degrades_without_constructing_any_loopback_client()
    {
        var factory = new FakeFactory();
        await using var source = new ProcessLoopbackAudioSource(Window, factory, isSupported: false);
        await source.StartAsync(default);
        Assert.Equal(0, factory.CreateCalls);
        Assert.False(source.IsAvailable);
        Assert.Contains("20348", source.DegradedReason);
    }

    [Fact]
    public async Task Activation_failure_degrades_without_system_audio_fallback()
    {
        var factory = new FakeFactory { Failure = new InvalidOperationException("activation rejected") };
        await using var source = new ProcessLoopbackAudioSource(Window, factory, isSupported: true);
        await source.StartAsync(default);
        Assert.Equal(1, factory.CreateCalls);
        Assert.False(source.IsAvailable);
        Assert.Contains("activation rejected", source.DegradedReason);
    }

    [Fact]
    public async Task Stop_detaches_callbacks_and_disposes_client_once()
    {
        var factory = new FakeFactory();
        await using var source = new ProcessLoopbackAudioSource(Window, factory, isSupported: true);
        var count = 0;
        source.AudioCaptured += _ => count++;
        await source.StartAsync(default);
        await source.StopAsync();
        await source.StopAsync();
        factory.Client!.Push(new byte[3840]);
        Assert.Equal(1, factory.Client.StopCalls);
        Assert.Equal(1, factory.Client.DisposeCalls);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Owner_process_exit_degrades_and_suppresses_late_audio()
    {
        var factory = new FakeFactory();
        await using var source = new ProcessLoopbackAudioSource(Window, factory, isSupported: true);
        var count = 0;
        source.AudioCaptured += _ => count++;
        await source.StartAsync(default);
        factory.Client!.Fail(new InvalidOperationException("process exited"));
        factory.Client.Push(new byte[3840]);
        Assert.False(source.IsAvailable);
        Assert.Contains("process exited", source.DegradedReason);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Silent_process_output_is_forwarded_as_a_silent_pcm_frame()
    {
        var factory = new FakeFactory();
        await using var source = new ProcessLoopbackAudioSource(Window, factory, isSupported: true);
        var frames = new List<AudioFrame>();
        source.AudioCaptured += frames.Add;
        await source.StartAsync(default);
        factory.Client!.Push(new byte[3840]);

        var frame = Assert.Single(frames);
        Assert.All(frame.Data.ToArray(), sample => Assert.Equal(0, sample));
    }

    private sealed class FakeFactory : IProcessLoopbackClientFactory
    {
        public int CreateCalls { get; private set; }
        public Exception? Failure { get; init; }
        public FakeClient? Client { get; private set; }
        public (uint, bool, int, int, int, int) Parameters { get; private set; }
        public IProcessLoopbackClient Create(uint targetPid, bool includeProcessTree, int sampleRate, int channels, int bitsPerSample, int frameSamples)
        {
            CreateCalls++;
            Parameters = (targetPid, includeProcessTree, sampleRate, channels, bitsPerSample, frameSamples);
            if (Failure is not null) throw Failure;
            return Client = new FakeClient();
        }
    }

    private sealed class FakeClient : IProcessLoopbackClient
    {
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public event WasapiPcmDataAvailableHandler? DataAvailable;
        public event Action<Exception?>? Stopped;
        public void Start() { }
        public void Stop() => StopCalls++;
        public void Push(byte[] bytes) => DataAvailable?.Invoke(bytes);
        public void Fail(Exception error) => Stopped?.Invoke(error);
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
