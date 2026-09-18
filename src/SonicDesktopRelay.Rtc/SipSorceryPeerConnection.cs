using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// A viewer's connection, backed by SIPSorcery. The media tracks are send-only: this side
/// publishes screen video + system audio and receives no media back.
/// </summary>
public sealed class SipSorceryPeerConnection : IPeerConnection
{
    /// <summary>H.264 over WebRTC is a dynamic payload type; 96 is the conventional first one.</summary>
    private const int H264PayloadId = 96;

    /// <summary>The RTP clock for video is 90 kHz, fixed by RFC 3551.</summary>
    private const uint VideoClockRate = 90_000;

    private readonly RTCPeerConnection _connection;
    private readonly object _gate = new();
    private bool _negotiated;
    private bool _closed;

    public SipSorceryPeerConnection(Guid participantId, IceServerSettings ice)
    {
        ParticipantId = participantId;

        var configuration = new RTCConfiguration
        {
            iceServers = ice.Servers
                .Select(x => new RTCIceServer { urls = x.Url, username = x.Username, credential = x.Credential })
                .ToList(),
            iceTransportPolicy = ice.ForceRelay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all
        };
        _connection = new RTCPeerConnection(configuration);

        // SIPSorcery 10.0.16 currently iterates audio before video when building BUNDLE SDP and
        // places ICE candidates on that first media section. Keeping audio first makes the BUNDLE
        // tag and candidate section agree until upstream issue #1763 is fixed.
        var audioTrack = new MediaStreamTrack(
            AudioCommonlyUsedFormats.OpusWebRTC,
            MediaStreamStatusEnum.SendOnly);
        _connection.addTrack(audioTrack);

        // packetization-mode=1 is what every browser and native decoder expects for H.264 over
        // WebRTC; without it a viewer negotiates single-NAL mode and chokes on the first frame
        // larger than an MTU.
        var videoTrack = new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.H264, H264PayloadId, 90_000, "packetization-mode=1"),
            MediaStreamStatusEnum.SendOnly);
        _connection.addTrack(videoTrack);

        _connection.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            IceCandidateGathered?.Invoke(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
        };

        _connection.OnReceiveReport += (_, mediaType, report) =>
        {
            if (mediaType != SDPMediaTypesEnum.video || report is null) return;
            OnRtcpReport(report);
        };

        _connection.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected)
                KeyFrameRequested?.Invoke(KeyFrameRequestReason.InitialConnection);
        };
    }

    public Guid ParticipantId { get; }

    public event Action<string, string?, int?>? IceCandidateGathered;

    public event Action<KeyFrameRequestReason>? KeyFrameRequested;

    public event Action<double>? PacketLossReported;

    public async Task<string> CreateOfferAsync(CancellationToken ct)
    {
        var offer = _connection.createOffer();
        await _connection.setLocalDescription(offer).WaitAsync(ct);
        return offer.sdp;
    }

    public Task ApplyAnswerAsync(string sdp, CancellationToken ct)
    {
        var result = _connection.setRemoteDescription(
            new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"The viewer's answer was rejected: {result}.");

        lock (_gate) _negotiated = true;
        return Task.CompletedTask;
    }

    public Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct)
    {
        _connection.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0)
        });
        return Task.CompletedTask;
    }

    public void SendVideo(EncodedVideoSample sample)
    {
        if (!CanSendMedia()) return;

        try
        {
            _connection.SendVideo(VideoClockRate / 30, sample.Data.ToArray());
        }
        catch (Exception e) when (IsExpectedTransportFailure(e))
        {
            // One viewer's socket dying must never propagate into the fan-out loop.
        }
    }

    public void SendAudio(EncodedAudioSample sample)
    {
        if (sample.SampleCount <= 0 || !CanSendMedia()) return;

        try
        {
            // SIPSorcery expects duration in RTP timestamp units. Opus/WebRTC uses a 48 kHz RTP
            // clock, so a 20 ms frame is 960 units; SampleCount is already exactly that value.
            _connection.SendAudio((uint)sample.SampleCount, sample.Data.ToArray());
        }
        catch (Exception e) when (IsExpectedTransportFailure(e))
        {
            // Audio is independent from video: a dead viewer socket is just a dropped sample.
        }
    }

    private bool CanSendMedia()
    {
        lock (_gate)
        {
            if (_closed || !_negotiated) return false;
        }

        return _connection.connectionState == RTCPeerConnectionState.connected;
    }

    private static bool IsExpectedTransportFailure(Exception e) =>
        e is ObjectDisposedException or InvalidOperationException
            or ApplicationException or System.Net.Sockets.SocketException;

    private void OnRtcpReport(RTCPCompoundPacket report)
    {
        var feedbackType = report.Feedback?.Header?.PayloadFeedbackMessageType;
        if (feedbackType == PSFBFeedbackTypesEnum.PLI)
            KeyFrameRequested?.Invoke(KeyFrameRequestReason.RtcpPli);
        else if (feedbackType == PSFBFeedbackTypesEnum.FIR)
            KeyFrameRequested?.Invoke(KeyFrameRequestReason.RtcpFir);

        var samples = report.ReceiverReport?.ReceptionReports
                      ?? report.SenderReport?.ReceptionReports;
        if (samples is null || samples.Count == 0) return;

        var worst = samples.Max(x => x.FractionLost);
        PacketLossReported?.Invoke(worst / 256.0);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closed) return ValueTask.CompletedTask;
            _closed = true;
        }

        _connection.close();
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class SipSorceryPeerConnectionFactory(IceServerSettings ice) : IPeerConnectionFactory
{
    public IPeerConnection Create(Guid participantId) => new SipSorceryPeerConnection(participantId, ice);
}
