using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using Xunit;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class WasapiAudioSinkTests
{
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
