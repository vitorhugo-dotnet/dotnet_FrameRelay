using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// The viewer's connection, backed by SIPSorcery. Audio and video tracks are <c>recvonly</c>:
/// this side answers, receives and never sends media. Both stay encoded at the transport
/// boundary; decoding belongs to the independent media pipelines.
/// </summary>
public sealed class SipSorceryViewerPeerConnection : IViewerPeerConnection
{
    /// <summary>H.264 over WebRTC is a dynamic payload type; 96 is the conventional first one.</summary>
    private const int H264PayloadId = 96;

    /// <summary>The RTP clock for video is 90 kHz, fixed by RFC 3551.</summary>
    private const uint VideoClockRate = 90_000;

    private readonly RTCPeerConnection _connection;
    private readonly ReceivedAudioTimeline _audioTimeline = new();
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

        // Keep audio first. SIPSorcery 10.0.16 currently places BUNDLE ICE candidates on the
        // audio stream first, so matching the publisher's audio-first offer avoids issue #1763
        // until upstream fixes candidate placement for video-first bundles.
        var audioTrack = new MediaStreamTrack(
            AudioCommonlyUsedFormats.OpusWebRTC,
            MediaStreamStatusEnum.RecvOnly);
        _connection.addTrack(audioTrack);

        // The same format the publisher offers. packetization-mode=1 is not optional: without
        // it the answer negotiates single-NAL mode and the first frame over an MTU is lost.
        var videoTrack = new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.H264, H264PayloadId, (int)VideoClockRate, "packetization-mode=1"),
            MediaStreamStatusEnum.RecvOnly);
        _connection.addTrack(videoTrack);

        _connection.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            EmitDiagnostic("viewer.ice_candidate.gathered");
            IceCandidateGathered?.Invoke(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
        };

        _connection.onsignalingstatechange += () =>
            EmitDiagnostic("viewer.signaling_state.changed");
        _connection.onicegatheringstatechange += _ =>
            EmitDiagnostic("viewer.ice_gathering_state.changed");
        _connection.oniceconnectionstatechange += _ =>
            EmitDiagnostic("viewer.ice_connection_state.changed");

        _connection.OnAudioFrameReceived += frame =>
        {
            if (frame.EncodedAudio is null || frame.EncodedAudio.Length == 0) return;
            AudioSampleReceived?.Invoke(
                _audioTimeline.Map(frame.EncodedAudio, frame.DurationMilliSeconds));
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
            EmitDiagnostic("viewer.connection_state.changed");

            // The publisher emits keyframes on demand only, so a viewer that has just
            // connected holds no reference frame at all until it asks for one.
            if (state == RTCPeerConnectionState.connected) RequestKeyFrame();
        };
    }

    public event Action<string, string?, int?>? IceCandidateGathered;

    public event Action<EncodedVideoSample>? VideoSampleReceived;

    public event Action<EncodedAudioSample>? AudioSampleReceived;

    public event Action<ViewerNegotiationDiagnosticEntry>? Diagnostic;

    public async Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct)
    {
        EmitDiagnostic("viewer.remote_description.begin");

        SetDescriptionResultEnum result;
        try
        {
            result = _connection.setRemoteDescription(
                new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        }
        catch (Exception e)
        {
            const string reason = "The publisher's offer could not be parsed.";
            EmitDiagnostic(
                "viewer.remote_description.failed",
                exceptionType: e.GetType().Name,
                message: reason);
            throw new ViewerNegotiationException("setRemoteDescription", reason, e);
        }

        if (result != SetDescriptionResultEnum.OK)
        {
            var reason = $"The publisher's offer was rejected: {result}.";
            EmitDiagnostic(
                "viewer.remote_description.failed",
                setDescriptionResult: result.ToString(),
                message: reason);
            throw new ViewerNegotiationException("setRemoteDescription", reason);
        }

        EmitDiagnostic(
            "viewer.remote_description.ok",
            setDescriptionResult: result.ToString());

        RTCSessionDescriptionInit answer;
        EmitDiagnostic("viewer.answer.create.begin");
        try
        {
            answer = _connection.createAnswer(null);
            EmitDiagnostic("viewer.answer.create.ok");
        }
        catch (Exception e)
        {
            var reason = $"SIPSorcery could not create the viewer answer: {e.Message}";
            EmitDiagnostic(
                "viewer.answer.create.failed",
                exceptionType: e.GetType().Name,
                message: reason);
            throw new ViewerNegotiationException("createAnswer", reason, e);
        }

        EmitDiagnostic("viewer.local_description.begin");
        try
        {
            await _connection.setLocalDescription(answer).WaitAsync(ct);
            EmitDiagnostic("viewer.local_description.ok");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var reason = $"SIPSorcery could not set the viewer local answer: {e.Message}";
            EmitDiagnostic(
                "viewer.local_description.failed",
                exceptionType: e.GetType().Name,
                message: reason);
            throw new ViewerNegotiationException("setLocalDescription", reason, e);
        }

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

    private void EmitDiagnostic(
        string eventName,
        string? setDescriptionResult = null,
        string? exceptionType = null,
        string? message = null)
    {
        Diagnostic?.Invoke(new ViewerNegotiationDiagnosticEntry(
            DateTimeOffset.UtcNow,
            eventName,
            _connection.signalingState.ToString(),
            _connection.iceGatheringState.ToString(),
            _connection.iceConnectionState.ToString(),
            _connection.connectionState.ToString(),
            setDescriptionResult,
            exceptionType,
            message));
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
