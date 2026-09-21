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

        var expected = new EncodedAudioSample(
            new byte[] { 1, 2, 3 },
            SampleCount: 960,
            Duration: TimeSpan.FromMilliseconds(20),
            Timestamp: TimeSpan.Zero);
        fake.ReceiveAudio(expected);

        Assert.Equal(expected, received);
    }

    private sealed class FakeViewerPeer : IViewerPeerConnection
    {
        private event Action<EncodedAudioSample>? AudioReceived;

        public event Action<string, string?, int?>? IceCandidateGathered
        {
            add { }
            remove { }
        }

        public event Action<EncodedVideoSample>? VideoSampleReceived
        {
            add { }
            remove { }
        }

        public event Action<EncodedAudioSample>? AudioSampleReceived
        {
            add => AudioReceived += value;
            remove => AudioReceived -= value;
        }

        public event Action<ViewerNegotiationDiagnosticEntry>? Diagnostic
        {
            add { }
            remove { }
        }

        public event Action<RtcTransportDiagnostics>? TransportDiagnosticsChanged
        {
            add { }
            remove { }
        }

        public RtcTransportDiagnostics? TransportDiagnostics => null;

        public Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct) => Task.FromResult("answer");

        public Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct) =>
            Task.CompletedTask;

        public void RequestKeyFrame()
        {
        }

        public void ReceiveAudio(EncodedAudioSample sample) => AudioReceived?.Invoke(sample);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
