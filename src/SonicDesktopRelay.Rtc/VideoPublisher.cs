using System.Collections.Concurrent;
using System.Text.Json;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.Rtc;

/// <summary>Privacy-safe codec negotiation summary; viewer IDs and signaling payloads are omitted.</summary>
public sealed record VideoCodecSessionDiagnostics(
    VideoCodec? ActiveCodec,
    string? FallbackReason,
    string LocalCodecs,
    string ViewerCodecs,
    string CommonCodecs,
    string? ProfileLevel);

/// <summary>
/// Owns one peer connection per viewer and feeds all of them from a single video encode and,
/// when available, a single audio encode. Everything that scales with viewer count lives here;
/// capture and encoding remain session-scoped in the media pipelines.
/// </summary>
public sealed class VideoPublisher(
    ScreenPublishPipeline pipeline,
    IPeerConnectionFactory peers,
    ISignalingConnection signaling,
    AudioPublishPipeline? audioPipeline = null,
    TimeProvider? time = null,
    Func<CancellationToken, Task>? downgradeEncoderToH264 = null,
    VideoCodec initialSessionCodec = VideoCodec.H264,
    VideoCodecCapabilities? publisherVideoCapabilities = null,
    VideoCodecConstraints? requiredAv1 = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, IPeerConnection> _peers = new();
    private readonly ConcurrentDictionary<Guid, VideoSampleSendQueue> _videoQueues = new();
    private readonly ConcurrentDictionary<Guid, RtcTransportDiagnostics> _transportDiagnostics = new();
    private readonly ConcurrentDictionary<Guid, Guid> _negotiationIds = new();
    private readonly ConcurrentDictionary<Guid, Guid> _fallbackIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _fallbackUsed = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _peerGates = new();
    private readonly ConcurrentDictionary<Guid, VideoCodecCapabilities> _viewerCapabilities = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private VideoCodec? _sessionCodec = initialSessionCodec;
    private string? _codecFallbackReason;
    private VideoCodecCapabilities? _publisherVideoCapabilities = publisherVideoCapabilities;
    private VideoCodecConstraints _requiredAv1 = requiredAv1 ?? new VideoCodecConstraints("0", 4);
    private readonly object _receiverStatsGate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _lastVideoSendDurationTicks;
    private bool _subscribed;

    public int PeerCount => _peers.Count;

    public VideoCodecSessionDiagnostics CodecDiagnostics
    {
        get
        {
            var selection = SelectSharedCodec();
            var localCodecs = CodecNames(_publisherVideoCapabilities?.Encoders ?? H264OnlyCapabilities.Encoders);
            var viewerIds = _peers.Keys.Order().ToArray();
            var viewerCodecs = string.Join("; ", viewerIds.Select((id, index) =>
                _viewerCapabilities.TryGetValue(id, out var capabilities)
                    ? $"viewer{index + 1}={CodecNames(capabilities.Decoders)}"
                    : $"viewer{index + 1}=pending"));
            var common = new HashSet<VideoCodec> { VideoCodec.H264 };
            if (_publisherVideoCapabilities?.Encoders.Contains(VideoCodec.Av1) == true
                && viewerIds.Length > 0
                && viewerIds.All(id => _viewerCapabilities.TryGetValue(id, out var capabilities)
                    && capabilities.Decoders.Contains(VideoCodec.Av1)))
                common.Add(VideoCodec.Av1);
            var profileLevel = _sessionCodec == VideoCodec.Av1
                && _publisherVideoCapabilities?.EncoderConstraints.ContainsKey(VideoCodec.Av1) == true
                    ? $"profile={_requiredAv1.Profile}, level-idx={_requiredAv1.MaxLevel}"
                    : null;
            return new VideoCodecSessionDiagnostics(
                _sessionCodec,
                _sessionCodec == VideoCodec.H264
                    ? selection.FallbackReason ?? _codecFallbackReason
                    : null,
                localCodecs,
                viewerCodecs,
                CodecNames(common),
                profileLevel);
        }
    }

    public IReadOnlyDictionary<Guid, RtcTransportDiagnostics> TransportDiagnostics => _transportDiagnostics;

    public TimeSpan? LastVideoSendDuration
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastVideoSendDurationTicks);
            return ticks <= 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    public long DroppedVideoSamples => _videoQueues.Values.Sum(x => x.DroppedSamples);

    public long VideoSendFailures => _videoQueues.Values.Sum(x => x.SendFailures);

    public int PendingVideoSamples => _videoQueues.Values.Sum(x => x.PendingSamples);

    public int ViewersAwaitingKeyFrame => _videoQueues.Values.Count(x => x.AwaitingKeyFrame);

    public event Action<Guid, RtcTransportDiagnostics>? TransportDiagnosticsChanged;

    /// <summary>Audio-track RTCP reception reports for audio diagnostics; never sent to video quality policy.</summary>
    public event Action<Guid, RtcpReceptionReport>? AudioRtcpReportReceived;

    public async Task AddViewerAsync(Guid participantId, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
        var peerGate = _peerGates.GetOrAdd(participantId, _ => new SemaphoreSlim(1, 1));
        await peerGate.WaitAsync(ct);
        try
        {
            if (_peers.ContainsKey(participantId)) return;

            var peer = peers.Create(participantId);
            if (!_peers.TryAdd(participantId, peer))
            {
                await peer.DisposeAsync();
                return;
            }

            var negotiationId = Guid.NewGuid();
            _negotiationIds[participantId] = negotiationId;
            AttachPeer(participantId, peer, negotiationId);

            EnsureSubscribed();

            // publisher.ready first, then the offer — the order dotnet_SonicRelay/docs/protocol.md
            // specifies. It is how a viewer learns which participant is the publisher, from the
            // server-authenticated `from` rather than from anything a peer claims about itself.
            // Skipping it happens to work with our own viewer, which also accepts the first offer,
            // but it would silently break any client written against the documented contract.
            await signaling.SendAsync(SignalingMessageTypes.PublisherReady, participantId, new { }, ct);

            var offer = _sessionCodec == VideoCodec.H264
                ? await peer.CreateH264OfferAsync(ct)
                : await peer.CreateOfferAsync(ct);
            await signaling.SendAsync(SignalingMessageTypes.WebRtcOffer, participantId,
                new { type = "offer", sdp = offer, negotiationId }, ct);
        }
        finally { peerGate.Release(); }
        }
        finally { _sessionGate.Release(); }
    }

    public async Task RemoveViewerAsync(Guid participantId)
    {
        await _sessionGate.WaitAsync();
        try
        {
        var peerGate = _peerGates.GetOrAdd(participantId, _ => new SemaphoreSlim(1, 1));
        await peerGate.WaitAsync();
        try
        {
            IPeerConnection? peer;
            lock (_receiverStatsGate)
            {
                _peers.TryRemove(participantId, out peer);
                pipeline.RemoveReceptionSource(participantId);
            }
            _transportDiagnostics.TryRemove(participantId, out _);
            _negotiationIds.TryRemove(participantId, out _);
            _viewerCapabilities.TryRemove(participantId, out _);
            _fallbackIds.TryRemove(participantId, out _);
            if (_videoQueues.TryRemove(participantId, out var queue))
                await queue.DisposeAsync();
            if (peer is not null) await peer.DisposeAsync();
        }
        finally { peerGate.Release(); }
        }
        finally { _sessionGate.Release(); }
    }

    public async Task HandleAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        if (envelope.From is not { } from) return;
        if (!_peers.TryGetValue(from, out var peer)) return;
        if (envelope.Payload is not { } payload) return;

        switch (envelope.Type)
        {
            case SignalingMessageTypes.WebRtcAnswer:
                await HandleAnswerAsync(from, payload, ct);
                break;

            case SignalingMessageTypes.WebRtcIceCandidate:
                if (MatchesGeneration(from, payload)
                    && payload.TryGetProperty("candidate", out var candidate)
                    && candidate.GetString() is { } candidateText)
                {
                    await peer.AddIceCandidateAsync(
                        candidateText,
                        payload.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null,
                        payload.TryGetProperty("sdpMLineIndex", out var index)
                            && index.ValueKind == JsonValueKind.Number
                                ? index.GetInt32()
                                : null,
                        ct);
                }

                break;

            case SignalingMessageTypes.WebRtcRenegotiate:
                await HandleRenegotiationRequestAsync(from, payload, ct);
                break;

            case SignalingMessageTypes.VideoReceiverStats:
                if (!TryReadReceiverStats(payload, out var stats)) break;
                lock (_receiverStatsGate)
                {
                    if (_peers.ContainsKey(from))
                        pipeline.ReportReceiverStats(from, stats);
                }
                break;
        }
    }

    private async Task HandleAnswerAsync(Guid participantId, JsonElement payload, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            var peerGate = _peerGates.GetOrAdd(participantId, _ => new SemaphoreSlim(1, 1));
            await peerGate.WaitAsync(ct);
            try
            {
                if (!_peers.TryGetValue(participantId, out var peer)
                    || !MatchesGeneration(participantId, payload)
                    || !payload.TryGetProperty("sdp", out var sdp)
                    || sdp.ValueKind != JsonValueKind.String
                    || sdp.GetString() is not { } sdpText) return;

                await peer.ApplyAnswerAsync(sdpText, ct);
                if (peer.NegotiatedVideoCodec is { } negotiated)
                    _viewerCapabilities[participantId] = BuildViewerCapabilities(negotiated, peer.NegotiatedVideoConstraints);
            }
            finally { peerGate.Release(); }

            var selection = SelectSharedCodec();
            _codecFallbackReason = selection.Codec == VideoCodec.H264 ? selection.FallbackReason : null;
            if (_sessionCodec == VideoCodec.Av1 && selection.Codec == VideoCodec.H264)
                await SwitchSessionToH264Async(ct);

            if (_sessionCodec == VideoCodec.H264
                && _peers.TryGetValue(participantId, out var answeredPeer)
                && answeredPeer.NegotiatedVideoCodec == VideoCodec.H264)
            {
                if (_videoQueues.TryGetValue(participantId, out var videoQueue))
                    videoQueue.ResetForCodecTransition();
                else
                    pipeline.RequestKeyFrame(KeyFrameRequestReason.QualityChange);
            }
        }
        finally { _sessionGate.Release(); }
    }

    public async Task UpdateRequiredAv1ConstraintsAsync(VideoCodecConstraints requiredAv1, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requiredAv1);
        await _sessionGate.WaitAsync(ct);
        try
        {
            _requiredAv1 = requiredAv1;
            var selection = SelectSharedCodec();
            _codecFallbackReason = selection.Codec == VideoCodec.H264 ? selection.FallbackReason : null;
            if (_sessionCodec == VideoCodec.Av1 && selection.Codec == VideoCodec.H264)
                await SwitchSessionToH264Async(ct);
        }
        finally { _sessionGate.Release(); }
    }

    /// <summary>Falls back the shared session after a runtime AV1 encoder failure.</summary>
    public async Task HandleRuntimeEncoderFailureAsync(Exception error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(error);
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_sessionCodec != VideoCodec.Av1) return;

            _codecFallbackReason = "av1-runtime-encoder-failure";
            if (_publisherVideoCapabilities is { } capabilities)
            {
                var rejectionReasons = capabilities.RejectionReasons.ToDictionary();
                rejectionReasons[VideoCodec.Av1] = "av1-runtime-encoder-failure";
                _publisherVideoCapabilities = capabilities with
                {
                    Encoders = capabilities.Encoders.Where(codec => codec != VideoCodec.Av1).ToHashSet(),
                    EncoderConstraints = capabilities.EncoderConstraints
                        .Where(pair => pair.Key != VideoCodec.Av1)
                        .ToDictionary(pair => pair.Key, pair => pair.Value),
                    RejectionReasons = rejectionReasons
                };
            }

            await SwitchSessionToH264Async(ct);
            pipeline.ResumeAfterEncoderReplacement();
        }
        finally { _sessionGate.Release(); }
    }

    private bool MatchesGeneration(Guid participantId, JsonElement payload)
    {
        if (!_negotiationIds.TryGetValue(participantId, out var current)) return false;
        if (payload.TryGetProperty("negotiationId", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
            && Guid.TryParse(idElement.GetString(), out var id))
            return id == current;
        return !_fallbackIds.ContainsKey(participantId);
    }

    private void AttachPeer(Guid participantId, IPeerConnection peer, Guid negotiationId)
    {
        var queue = new VideoSampleSendQueue(
            peer,
            duration => Interlocked.Exchange(ref _lastVideoSendDurationTicks, duration.Ticks),
            () => pipeline.RequestKeyFrame(KeyFrameRequestReason.PacketLoss),
            _time);
        _videoQueues[participantId] = queue;
        peer.IceCandidateGathered += (candidate, mid, index) =>
            _ = signaling.SendAsync(SignalingMessageTypes.WebRtcIceCandidate, participantId,
                new { candidate, sdpMid = mid, sdpMLineIndex = index,
                    negotiationId = _negotiationIds.TryGetValue(participantId, out var current) ? current : negotiationId }, CancellationToken.None);
        peer.KeyFrameRequested += pipeline.RequestKeyFrame;
        peer.ReceptionReportReceived += report =>
        {
            if (report.MediaKind == RtcMediaKind.Video)
                pipeline.ReportReception(participantId, report.FractionLost);
            else if (report.MediaKind == RtcMediaKind.Audio)
                AudioRtcpReportReceived?.Invoke(participantId, report);
        };
        peer.TransportDiagnosticsChanged += diagnostics =>
        {
            if (!_peers.TryGetValue(participantId, out var current) || !ReferenceEquals(current, peer)) return;
            _transportDiagnostics[participantId] = diagnostics;
            TransportDiagnosticsChanged?.Invoke(participantId, diagnostics);
        };
    }

    private async Task RetryViewerThroughRelayAsync(Guid participantId, JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String || reason.GetString() != "direct_connection_failed"
            || !payload.TryGetProperty("iceTransportPolicy", out var policy) || policy.ValueKind != JsonValueKind.String || policy.GetString() != "relay"
            || !payload.TryGetProperty("negotiationId", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var requestedId)) return;

        await _sessionGate.WaitAsync(ct);
        try
        {
            var gate = _peerGates.GetOrAdd(participantId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (!_peers.TryGetValue(participantId, out var oldPeer)) return;
                if (!_fallbackUsed.TryAdd(participantId, 0)) return;
                _fallbackIds[participantId] = requestedId;

                var relayPeer = peers.Create(participantId, forceRelay: true);
                _negotiationIds[participantId] = requestedId;
                _peers[participantId] = relayPeer;
                if (_videoQueues.TryRemove(participantId, out var oldQueue)) await oldQueue.DisposeAsync();
                AttachPeer(participantId, relayPeer, requestedId);
                await oldPeer.DisposeAsync();
                var offer = _sessionCodec == VideoCodec.H264
                    ? await relayPeer.CreateH264OfferAsync(ct)
                    : await relayPeer.CreateOfferAsync(ct);
                await signaling.SendAsync(SignalingMessageTypes.WebRtcOffer, participantId,
                    new { type = "offer", sdp = offer, negotiationId = requestedId }, ct);
            }
            finally { gate.Release(); }
        }
        finally { _sessionGate.Release(); }
    }

    private async Task HandleRenegotiationRequestAsync(Guid participantId, JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("reason", out var reasonElement)
            || reasonElement.ValueKind != JsonValueKind.String) return;

        var reason = reasonElement.GetString();
        if (reason == "direct_connection_failed")
        {
            await RetryViewerThroughRelayAsync(participantId, payload, ct);
            return;
        }
        if (reason != "av1_decoder_init_failed"
            || !payload.TryGetProperty("negotiationId", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var requestedId)
            || !_negotiationIds.TryGetValue(participantId, out var currentId)
            || requestedId != currentId) return;

        await _sessionGate.WaitAsync(ct);
        try
        {
            if (!_negotiationIds.TryGetValue(participantId, out currentId) || requestedId != currentId) return;
            _viewerCapabilities[participantId] = BuildViewerCapabilities(VideoCodec.H264, null);
            var selection = SelectSharedCodec();
            _codecFallbackReason = selection.Codec == VideoCodec.H264 ? selection.FallbackReason : null;
            if (selection.Codec == VideoCodec.H264)
                await SwitchSessionToH264Async(ct);
        }
        finally { _sessionGate.Release(); }
    }

    private VideoCodecSelection SelectSharedCodec() =>
        VideoCodecNegotiator.Select(
            _publisherVideoCapabilities ?? H264OnlyCapabilities,
            _viewerCapabilities.Values.ToArray(),
            _requiredAv1);

    private static string CodecNames(IEnumerable<VideoCodec> codecs) =>
        string.Join(", ", new[] { VideoCodec.H264 }.Concat(codecs).Distinct().Order()
            .Select(x => x == VideoCodec.Av1 ? "AV1" : "H264"));

    private static VideoCodecCapabilities BuildViewerCapabilities(
        VideoCodec negotiatedCodec,
        VideoCodecConstraints? negotiatedConstraints)
    {
        var decoderCodecs = negotiatedCodec == VideoCodec.Av1
            ? new HashSet<VideoCodec> { VideoCodec.Av1 }
            : new HashSet<VideoCodec>();
        var decoderConstraints = negotiatedCodec == VideoCodec.Av1 && negotiatedConstraints is not null
            ? new Dictionary<VideoCodec, VideoCodecConstraints> { [VideoCodec.Av1] = negotiatedConstraints }
            : new Dictionary<VideoCodec, VideoCodecConstraints>();
        return new VideoCodecCapabilities(
            new HashSet<VideoCodec>(), decoderCodecs,
            new Dictionary<VideoCodec, VideoCodecConstraints>(), decoderConstraints,
            new Dictionary<VideoCodec, string>());
    }

    private static readonly VideoCodecCapabilities H264OnlyCapabilities = new(
        new HashSet<VideoCodec>(), new HashSet<VideoCodec>(),
        new Dictionary<VideoCodec, VideoCodecConstraints>(),
        new Dictionary<VideoCodec, VideoCodecConstraints>(),
        new Dictionary<VideoCodec, string>());

    private async Task SwitchSessionToH264Async(CancellationToken ct)
    {
        if (_sessionCodec == VideoCodec.H264) return;
        if (downgradeEncoderToH264 is not null)
            await downgradeEncoderToH264(ct);

        _sessionCodec = VideoCodec.H264;
        foreach (var queue in _videoQueues.Values) queue.ResetForCodecTransition();

        foreach (var participantId in _peers.Keys.Order())
        {
            var gate = _peerGates.GetOrAdd(participantId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (!_peers.TryGetValue(participantId, out var peer)) continue;
                var negotiationId = Guid.NewGuid();
                _negotiationIds[participantId] = negotiationId;
                var offer = await peer.CreateH264OfferAsync(ct);
                await signaling.SendAsync(SignalingMessageTypes.WebRtcOffer, participantId,
                    new { type = "offer", sdp = offer, negotiationId }, ct);
            }
            finally { gate.Release(); }
        }
        pipeline.RequestKeyFrame(KeyFrameRequestReason.Manual);
    }

    private static bool TryReadReceiverStats(JsonElement payload, out VideoReceiverStats stats)
    {
        stats = null!;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryInt(payload, "version", out var version) || version != 1
            || !TryLong(payload, "intervalMilliseconds", out var interval) || interval is < 1000 or > 5000
            || !TryLong(payload, "rtpPacketsReceived", out var received) || received is < 0 or > 10_000_000
            || !TryLong(payload, "rtpPacketsLost", out var lost) || lost is < 0 or > 10_000_000
            || received + lost > 10_000_000
            || !TryLong(payload, "accessUnitsReceived", out var units) || units is < 0 or > 10_000_000
            || !TryLong(payload, "incompleteAccessUnits", out var incomplete)
            || incomplete < 0 || incomplete > 10_000_000 || incomplete > units
            || !TryLong(payload, "decodedFrames", out var decoded) || decoded is < 0 or > 10_000_000
            || !payload.TryGetProperty("targetFramesPerSecond", out var fpsElement)
            || fpsElement.ValueKind != JsonValueKind.Number || !fpsElement.TryGetDouble(out var fps)
            || !double.IsFinite(fps) || fps is < 1 or > 60)
            return false;

        stats = new VideoReceiverStats(version, interval, received, lost, units, incomplete, decoded, fps);
        return true;
    }

    private static bool TryLong(JsonElement payload, string name, out long value) =>
        payload.TryGetProperty(name, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt64(out value) || SetDefault(out value);

    private static bool TryInt(JsonElement payload, string name, out int value) =>
        payload.TryGetProperty(name, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out value) || SetDefault(out value);

    private static bool SetDefault(out long value) { value = 0; return false; }
    private static bool SetDefault(out int value) { value = 0; return false; }

    // Subscribed on the first viewer rather than at construction: with nobody watching there
    // is nothing to send, and unsubscribed media pipelines are the cheap idle state.
    private void EnsureSubscribed()
    {
        if (_subscribed) return;
        pipeline.SampleEncoded += BroadcastVideo;
        if (audioPipeline is not null) audioPipeline.SampleEncoded += BroadcastAudio;
        _subscribed = true;
    }

    private void BroadcastVideo(EncodedVideoSample sample)
    {
        foreach (var queue in _videoQueues.Values) queue.Enqueue(sample);
    }

    private void BroadcastAudio(EncodedAudioSample sample)
    {
        foreach (var peer in _peers.Values) peer.SendAudio(sample);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribed)
        {
            pipeline.SampleEncoded -= BroadcastVideo;
            if (audioPipeline is not null) audioPipeline.SampleEncoded -= BroadcastAudio;
            _subscribed = false;
        }

        foreach (var participantId in _peers.Keys) await RemoveViewerAsync(participantId);
    }
}
