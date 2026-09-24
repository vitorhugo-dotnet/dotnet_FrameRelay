using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using Xunit;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class WasapiAudioSinkTests
{
    [Fact]
    public async Task Volume_is_clamped_and_applied_to_playback()
    {
        var factory = new FakePlaybackFactory();
        await using var sink = new WasapiAudioSink(factory);
        sink.Volume = 2;
        await sink.StartAsync(CancellationToken.None);
        Assert.Equal(1f, sink.Volume);
        Assert.Equal(1f, factory.Session!.Gain);

        sink.Volume = -1;
        Assert.Equal(0f, sink.Volume);
        Assert.Equal(0f, factory.Session.Gain);
    }

    [Fact]
    public async Task Muting_silences_playback_and_unmuting_restores_last_nonzero_volume()
    {
        var factory = new FakePlaybackFactory();
        await using var sink = new WasapiAudioSink(factory);
        sink.Volume = 0.4f;
        await sink.StartAsync(CancellationToken.None);

        sink.IsMuted = true;
        Assert.Equal(0f, factory.Session!.Gain);
        Assert.Equal(0.4f, sink.Volume);

        sink.IsMuted = false;
        Assert.Equal(0.4f, factory.Session.Gain);

        sink.Volume = 0;
        Assert.True(sink.IsMuted);
        sink.IsMuted = false;
        Assert.Equal(0.4f, sink.Volume);
        Assert.Equal(0.4f, factory.Session.Gain);
        Assert.Equal(1, factory.Session.PlayCalls);
    }

    [Fact]
    public async Task Applying_a_muted_control_snapshot_never_briefly_restores_gain()
    {
        var factory = new FakePlaybackFactory();
        await using var sink = new WasapiAudioSink(factory);
        await sink.StartAsync(CancellationToken.None);
        sink.IsMuted = true;
        var changesBefore = factory.Session!.GainChanges.Count;

        sink.SetPlaybackControls(0.25f, true);

        Assert.All(factory.Session.GainChanges.Skip(changesBefore), gain => Assert.Equal(0f, gain));
        Assert.Equal(0.25f, sink.Volume);
        Assert.True(sink.IsMuted);
    }

    [Fact]
    public void Playback_provider_scales_pcm_without_changing_the_queued_samples()
    {
        var queue = new BoundedPcmBuffer(16);
        var source = new BoundedPcmWaveProvider(queue, 48_000, 2) { Gain = 0.5f };
        var samples = new short[] { 1000, -1000, 2000, -2000 };
        queue.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
        Span<byte> output = stackalloc byte[8];

        source.Read(output);

        Assert.Equal(new short[] { 500, -500, 1000, -1000 },
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(output).ToArray());
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public void Muted_playback_provider_outputs_silence_and_consumes_queued_audio()
    {
        var queue = new BoundedPcmBuffer(16);
        var source = new BoundedPcmWaveProvider(queue, 48_000, 2) { Gain = 0f };
        queue.Write([0xE8, 0x03, 0x18, 0xFC]);
        Span<byte> output = stackalloc byte[4];

        source.Read(output);

        Assert.Equal(new byte[4], output.ToArray());
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public async Task Start_and_stop_are_idempotent_and_use_48khz_stereo()
    {
        var factory = new FakePlaybackFactory();
        await using var sink = new WasapiAudioSink(factory, maxBufferedMilliseconds: 200);

        await sink.StartAsync(CancellationToken.None);
        await sink.StartAsync(CancellationToken.None);

        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(48_000, factory.SampleRate);
        Assert.Equal(2, factory.Channels);
        Assert.Equal(1, factory.Session!.PlayCalls);
        Assert.Equal("Fake Speakers", sink.ActiveEndpointName);
        Assert.Null(sink.DegradedReason);

        await sink.StopAsync();
        await sink.StopAsync();

        Assert.Equal(1, factory.Session.StopCalls);
    }

    [Fact]
    public async Task Write_queues_pcm_for_the_playback_session()
    {
        var factory = new FakePlaybackFactory();
        await using var sink = new WasapiAudioSink(factory, maxBufferedMilliseconds: 200);
        await sink.StartAsync(CancellationToken.None);
        var pcm = Enumerable.Range(0, 960 * 2 * sizeof(short)).Select(i => (byte)(i % 251)).ToArray();

        sink.Write(new AudioFrame(pcm, 48_000, 2, 960, TimeSpan.FromMilliseconds(20)));

        Assert.Equal(pcm, factory.Buffer!.Snapshot());
    }

    [Fact]
    public async Task Terminal_player_failure_degrades_audio_without_exception_storms()
    {
        var factory = new FakePlaybackFactory { ThrowOnPlay = true };
        await using var sink = new WasapiAudioSink(factory, maxBufferedMilliseconds: 200);

        var startError = await Record.ExceptionAsync(() => sink.StartAsync(CancellationToken.None));
        var writeError = Record.Exception(() => sink.Write(
            new AudioFrame(new byte[960 * 2 * sizeof(short)], 48_000, 2, 960, TimeSpan.Zero)));

        Assert.Null(startError);
        Assert.Null(writeError);
        Assert.NotNull(sink.DegradedReason);
        Assert.Empty(factory.Buffer!.Snapshot());
    }

    private sealed class FakePlaybackFactory : IWasapiPlaybackFactory
    {
        public int CreateCalls { get; private set; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public BoundedPcmBuffer? Buffer { get; private set; }
        public FakePlaybackSession? Session { get; private set; }
        public bool ThrowOnPlay { get; init; }

        public IWasapiPlaybackSession Create(BoundedPcmBuffer buffer, int sampleRate, int channels)
        {
            CreateCalls++;
            Buffer = buffer;
            SampleRate = sampleRate;
            Channels = channels;
            Session = new FakePlaybackSession(ThrowOnPlay);
            return Session;
        }
    }

    private sealed class FakePlaybackSession(bool throwOnPlay) : IWasapiPlaybackSession
    {
        public string EndpointName => "Fake Speakers";
        private float _gain = 1;
        public List<float> GainChanges { get; } = [];
        public float Gain
        {
            get => _gain;
            set
            {
                _gain = value;
                GainChanges.Add(value);
            }
        }
        public int PlayCalls { get; private set; }
        public int StopCalls { get; private set; }

        public void Play()
        {
            PlayCalls++;
            if (throwOnPlay) throw new InvalidOperationException("device vanished");
        }

        public void Stop() => StopCalls++;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
