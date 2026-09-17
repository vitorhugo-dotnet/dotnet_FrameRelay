using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// The viewer's connection, backed by SIPSorcery. The video track is <c>recvonly</c>: this
/// side answers, receives and never sends media. Frames are taken still encoded from
/// <c>OnVideoFrameReceived</c> — SIPSorcery reassembles the RTP packets into whole access
/// units, and decoding them is this project's job, not the transport's.
/// </summary>
public sealed class SipSorceryViewerPeerConnection : IViewerPeerConnection
{
    /// <summary>H.264 over WebRTC is a dynamic payload type; 96 is the conventional first one.</summary>
    private const int H264PayloadId = 96;

    /// <summary>The RTP clock for video is 90 kHz, fixed by RFC 3551.</summary>
    private const uint VideoClockRate = 90_000;

    private readonly RTCPeerConnection _connection;
    private readonly Lock _gate = new();
    private bool _closed;

    public SipSorceryViewerPeerConnection(IceServerSettings ice)
    {
        var configuration = new RTCConfiguration
        {
            iceServers = ice.Servers
                .Select(x => new RTCIceServer { urls = x.Url, username = x.Username, credential = x.Credential })
                .ToList(),
            iceTransportPolicy = ice.ForceRelay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all
        };
        _connection = new RTCPeerConnection(configuration);

        // The same format the publisher offers. packetization-mode=1 is not optional: without
        // it the answer negotiates single-NAL mode and the first frame over an MTU is lost.
        var videoTrack = new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.H264, H264PayloadId, (int)VideoClockRate, "packetization-mode=1"),
            MediaStreamStatusEnum.RecvOnly);
        _connection.addTrack(videoTrack);

        _connection.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            IceCandidateGathered?.Invoke(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
        };

        _connection.OnVideoFrameReceived += (_, timestamp, frame, _) =>
        {
            if (frame is null || frame.Length == 0) return;

            // The wire carries no picture size: the decoder reads it from the SPS, and a
            // resolution change mid-session arrives as a new SPS rather than as metadata.
            VideoSampleReceived?.Invoke(new EncodedVideoSample(
                frame,
                TimeSpan.FromSeconds(timestamp / (double)VideoClockRate),
                LooksLikeKeyFrame(frame),
                Width: 0,
                Height: 0));
        };

        _connection.onconnectionstatechange += state =>
        {
            // The publisher emits keyframes on demand only, so a viewer that has just
            // connected holds no reference frame at all until it asks for one.
            if (state == RTCPeerConnectionState.connected) RequestKeyFrame();
        };
    }

    public event Action<string, string?, int?>? IceCandidateGathered;

    public event Action<EncodedVideoSample>? VideoSampleReceived;

    // The contract exists before the SIPSorcery receive wiring on purpose. The next TDD step
    // makes this event observable from OnAudioFrameReceived instead of implementing both
    // contract and behavior in one unverified jump.
    public event Action<EncodedAudioSample>? AudioSampleReceived
    {
        add { }
        remove { }
    }

    public async Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct)
    {
        SetDescriptionResultEnum result;
        try
        {
            result = _connection.setRemoteDescription(
                new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("The publisher's offer could not be parsed.", e);
        }

        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"The publisher's offer was rejected: {result}.");

        var answer = _connection.createAnswer(null);
        await _connection.setLocalDescription(answer).WaitAsync(ct);
        return answer.sdp;
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

    public void RequestKeyFrame()
    {
        lock (_gate)
        {
            if (_closed) return;
        }

        // Before the transport is up there is no RTCP session and no remote SSRC to name, so
        // there is nothing to send. Asking early is normal — the pipeline's watchdog does not
        // know how far negotiation has got — and must be a no-op, not a throw.
        var session = _connection.VideoRtcpSession;
        if (session is null) return;
        if (_connection.VideoRemoteTrack is not { } remote) return;

        try
        {
            _connection.SendRtcpFeedback(SDPMediaTypesEnum.video,
                new RTCPFeedback(session.Ssrc, remote.Ssrc, PSFBFeedbackTypesEnum.PLI));
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException
                                      or ApplicationException or System.Net.Sockets.SocketException)
        {
            // A PLI that cannot leave is not worth ending the session over; the next stall
            // check will try again.
        }
    }

    /// <summary>
    /// True when the access unit carries an IDR or a parameter set. Purely informational —
    /// the decoder does not need to be told — so it stops at the first NAL that answers.
    /// </summary>
    private static bool LooksLikeKeyFrame(byte[] frame)
    {
        for (var i = 0; i + 3 < frame.Length; i++)
        {
            if (frame[i] != 0x00 || frame[i + 1] != 0x00) continue;

            int header;
            if (frame[i + 2] == 0x01) header = frame[i + 3];
            else if (frame[i + 2] == 0x00 && i + 4 < frame.Length && frame[i + 3] == 0x01) header = frame[i + 4];
            else continue;

            var nalType = header & 0x1F;
            // 5 = IDR slice, 7 = SPS, 8 = PPS.
            if (nalType is 5 or 7 or 8) return true;
        }

        return false;
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

public sealed class SipSorceryViewerPeerConnectionFactory(IceServerSettings ice) : IViewerPeerConnectionFactory
{
    public IViewerPeerConnection Create() => new SipSorceryViewerPeerConnection(ice);
}
