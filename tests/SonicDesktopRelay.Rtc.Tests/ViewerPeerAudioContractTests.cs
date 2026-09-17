using SonicDesktopRelay.Media;
using SonicDesktopRelay.Rtc;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class ViewerPeerAudioContractTests
{
    [Fact]
    public void Encoded_audio_can_cross_the_viewer_peer_contract_without_sipsorcery_types()
    {
        var fake = new FakeViewerPeer();
        IViewerPeerConnection peer = fake;
        EncodedAudioSample? received = null;
        peer.AudioSampleReceived += sample => received = sample;

        var expected = new EncodedAudioSample(new byte[] { 1, 2, 3 }, TimeSpan.FromMilliseconds(20), 48_000, 2, 960);
        fake.ReceiveAudio(expected);

        Assert.Equal(expected, received);
    }

    private sealed class FakeViewerPeer : IViewerPeerConnection
    {
        public event Action<string, string?, int?>? IceCandidateGathered;
        public event Action<EncodedVideoSample>? VideoSampleReceived;
        public event Action<EncodedAudioSample>? AudioSampleReceived;

        public Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct) => Task.FromResult("answer");

        public Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct) =>
            Task.CompletedTask;

        public void RequestKeyFrame()
        {
        }

        public void ReceiveAudio(EncodedAudioSample sample) => AudioSampleReceived?.Invoke(sample);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
