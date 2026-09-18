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

    // Three 30 fps frames is enough to absorb ordinary packet reordering without turning
    // loss recovery into visible latency. Actual loss is still validated by our H.264 guard.
    private static readonly TimeSpan VideoReorderWindow = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RecoveryPliInterval = TimeSpan.FromSeconds(1);

    private readonly RTCPeerConnection _connection;
    private readonly ReceivedAudioTimeline _audioTimeline = new();
    private readonly H264RtpAccessUnitAssembler _videoAssembler = new();
    private readonly ViewerVideoRecoveryGate _videoRecovery = new(RecoveryPliInterval);
    private readonly Lock _gate = new();

    private RtcTransportDiagnostics? _transportDiagnostics;
    private long _pliSent;
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

        // Use SIPSorcery's supported reorder buffer first. The integrity guard below remains
        // necessary because after the reorder timeout a genuinely missing packet is skipped,
        // while 10.0.16's H264Depacketiser does not validate continuity before FU-A rebuild.
        _connection.VideoStream?.AddBuffer(VideoReorderWindow);
        _videoAssembler.AccessUnitDropped += OnAccessUnitDropped;
        _videoAssembler.RtpGapDetected += OnRtpGapDetected;

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
        _connection.oniceconnectionstatechange += state =>
        {
            EmitDiagnostic("viewer.ice_connection_state.changed");
            if (state == RTCIceConnectionState.connected)
                RefreshTransportDiagnostics();
        };

        _connection.OnAudioFrameReceived += frame =>
        {
            if (frame.EncodedAudio is null || frame.EncodedAudio.Length == 0) return;
            AudioSampleReceived?.Invoke(
                _audioTimeline.Map(frame.EncodedAudio, frame.DurationMilliSeconds));
        };

        // Do not trust SIPSorcery 10.0.16's reconstructed H.264 frame event here. Its
        // depacketizer sorts packets for a timestamp but can concatenate FU-A fragments across
        // a proven RTP sequence gap. Consume the supported low-level RTP event instead, after
        // SIPSorcery's reorder buffer, and only forward integrity-checked access units.
        _connection.OnRtpPacketReceived += (_, mediaType, packet) =>
        {
            if (mediaType != SDPMediaTypesEnum.video || packet is null) return;

            var payload = packet.GetPayloadBytes();
            var accessUnit = _videoAssembler.Push(
                packet.Header.SequenceNumber,
                packet.Header.Timestamp,
                packet.Header.MarkerBit != 0,
                payload);

            if (accessUnit is not { } complete)
                return;

            bool recoveryWasActive;
            bool deliver;
            lock (_gate)
            {
                if (_closed) return;
                recoveryWasActive = _videoRecovery.Active;
                deliver = _videoRecovery.ShouldDeliver(complete.IsIdr, complete.HasVcl);
            }

            if (!deliver)
            {
                EmitDiagnostic(
                    "viewer.video.access_unit.suppressed",
                    message:
                        $"reason=awaiting-idr timestamp={complete.Timestamp} " +
                        $"suppressed={_videoRecovery.SuspectAccessUnitsSuppressed}");
                return;
            }

            if (recoveryWasActive && complete.IsIdr)
            {
                EmitDiagnostic(
                    "viewer.video.recovery.completed",
                    message:
                        $"timestamp={complete.Timestamp} recoveryEpisodes={_videoRecovery.RecoveryEpisodes} " +
                        $"recoveryKeyframesRequested={_videoRecovery.RecoveryKeyframesRequested} pliSent={Interlocked.Read(ref _pliSent)}");
            }

            // The wire carries no picture size: Media Foundation reads it from SPS. Legitimate
            // SPS-driven size changes therefore continue through the normal decoder path.
            VideoSampleReceived?.Invoke(new EncodedVideoSample(
                complete.Data,
                TimeSpan.FromSeconds(complete.Timestamp / (double)VideoClockRate),
                complete.IsIdr,
                Width: 0,
                Height: 0));
        };

        _connection.onconnectionstatechange += state =>
        {
            EmitDiagnostic("viewer.connection_state.changed");

            // The publisher emits keyframes on demand only, so a viewer that has just
            // connected holds no reference frame at all until it asks for one.
            if (state == RTCPeerConnectionState.connected)
            {
                RefreshTransportDiagnostics();
                RequestRecoveryKeyFrame("initial-connection");
            }
        };
    }

    public event Action<string, string?, int?>? IceCandidateGathered;

    public event Action<EncodedVideoSample>? VideoSampleReceived;

    public event Action<EncodedAudioSample>? AudioSampleReceived;

    public event Action<ViewerNegotiationDiagnosticEntry>? Diagnostic;

    public event Action<RtcTransportDiagnostics>? TransportDiagnosticsChanged;

    public RtcTransportDiagnostics? TransportDiagnostics
    {
        get
        {
            lock (_gate) return _transportDiagnostics;
        }
    }

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

    private void RefreshTransportDiagnostics()
    {
        var nominated = _connection.GetRtpChannel()?.NominatedEntry;
        if (nominated?.LocalCandidate is not { } local || nominated.RemoteCandidate is not { } remote)
            return;

        var next = RtcTransportClassifier.Classify(
            local.type,
            remote.type,
            local.protocol,
            remote.protocol);

        lock (_gate)
        {
            if (_closed || _transportDiagnostics == next) return;
            _transportDiagnostics = next;
        }

        TransportDiagnosticsChanged?.Invoke(next);
        EmitDiagnostic(
            "viewer.transport.selected",
            message: $"path={next.Path} protocol={next.Protocol} localType={next.LocalCandidateType} remoteType={next.RemoteCandidateType}");
    }

    public void RequestKeyFrame() => RequestRecoveryKeyFrame("stall");

    private void OnRtpGapDetected(H264RtpGap gap)
    {
        EmitDiagnostic(
            "viewer.rtp_sequence_gap",
            message:
                $"timestamp={gap.Timestamp} previousSequence={gap.PreviousSequence} " +
                $"nextSequence={gap.NextSequence} missingPackets={gap.MissingPackets} " +
                $"rtpPacketsReceived={_videoAssembler.RtpPacketsReceived} " +
                $"rtpPacketsLost={_videoAssembler.RtpPacketsLost} " +
                $"rtpSequenceGaps={_videoAssembler.RtpSequenceGaps}");

        RequestRecoveryKeyFrame("rtp-sequence-gap");
    }

    private void OnAccessUnitDropped(H264AccessUnitDrop drop)
    {
        EmitDiagnostic(
            "viewer.rtp_access_unit.dropped",
            message:
                $"kind={drop.Kind} reason={drop.Reason} timestamp={drop.Timestamp} " +
                $"previousSequence={drop.PreviousSequence?.ToString() ?? "-"} " +
                $"nextSequence={drop.NextSequence?.ToString() ?? "-"} missingPackets={drop.MissingPackets} " +
                $"rtpPacketsReceived={_videoAssembler.RtpPacketsReceived} " +
                $"rtpPacketsLost={_videoAssembler.RtpPacketsLost} rtpSequenceGaps={_videoAssembler.RtpSequenceGaps} " +
                $"rtpPacketsReordered={_videoAssembler.RtpPacketsReordered} " +
                $"incompleteAccessUnitsDropped={_videoAssembler.IncompleteAccessUnitsDropped} " +
                $"corruptAccessUnitsDropped={_videoAssembler.CorruptAccessUnitsDropped}");

        RequestRecoveryKeyFrame($"rtp-{drop.Reason}");
    }

    private void RequestRecoveryKeyFrame(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        bool started;
        bool coalesced;
        bool sent;

        lock (_gate)
        {
            if (_closed) return;

            started = !_videoRecovery.Active;
            _videoRecovery.BeginRecovery();

            if (!_videoRecovery.CanRequestPli(now))
            {
                coalesced = true;
                sent = false;
            }
            else
            {
                coalesced = false;
                sent = TrySendPliUnsafe();
                if (sent)
                    _videoRecovery.MarkPliSent(now);
            }
        }

        if (started)
        {
            EmitDiagnostic(
                "viewer.video.recovery.started",
                message:
                    $"reason={reason} recoveryEpisodes={_videoRecovery.RecoveryEpisodes} " +
                    $"rtpPacketsLost={_videoAssembler.RtpPacketsLost} " +
                    $"incompleteAccessUnitsDropped={_videoAssembler.IncompleteAccessUnitsDropped}");
        }

        if (coalesced)
        {
            EmitDiagnostic(
                "viewer.video.recovery.pli_coalesced",
                message:
                    $"reason={reason} minimumIntervalMs={RecoveryPliInterval.TotalMilliseconds:0} " +
                    $"recoveryKeyframesRequested={_videoRecovery.RecoveryKeyframesRequested}");
            return;
        }

        if (!sent)
        {
            EmitDiagnostic(
                "viewer.video.recovery.pli_deferred",
                message:
                    $"reason={reason} transportReady=false " +
                    $"recoveryKeyframesRequested={_videoRecovery.RecoveryKeyframesRequested}");
            return;
        }

        var totalSent = Interlocked.Increment(ref _pliSent);
        EmitDiagnostic(
            "viewer.video.recovery.pli_sent",
            message:
                $"reason={reason} pliSent={totalSent} " +
                $"recoveryKeyframesRequested={_videoRecovery.RecoveryKeyframesRequested}");
    }

    private bool TrySendPliUnsafe()
    {
        // Before the transport is up there is no RTCP session and no remote SSRC to name.
        var session = _connection.VideoRtcpSession;
        if (session is null) return false;
        if (_connection.VideoRemoteTrack is not { } remote) return false;

        try
        {
            _connection.SendRtcpFeedback(
                SDPMediaTypesEnum.video,
                new RTCPFeedback(session.Ssrc, remote.Ssrc, PSFBFeedbackTypesEnum.PLI));
            return true;
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException
                                      or ApplicationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
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
