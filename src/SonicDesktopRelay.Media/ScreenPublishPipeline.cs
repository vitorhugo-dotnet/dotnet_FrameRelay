using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SonicDesktopRelay.Media;

/// <summary>
/// capture → encode → one event, for the whole session. Every viewer subscribes to the same
/// <see cref="SampleEncoded"/>, so adding the fourth viewer costs a subscription rather than
/// a fourth encoder.
/// </summary>
public sealed class ScreenPublishPipeline : IAsyncDisposable
{
    private readonly IScreenCaptureSource _capture;
    private readonly IVideoEncoder _encoder;
    private readonly MediaSessionClock? _clock;
    private readonly TimeProvider _time;
    private readonly ILogger<ScreenPublishPipeline> _logger;
    private readonly VideoPublishProfile _profile;

    // RTCP reports normally arrive periodically. Requiring both multiple reports and elapsed
    // time makes a burst insufficient on its own, while the cooldown prevents staircase drops.
    private const double PoorReceptionLossRatio = 0.05;
    private const double StableReceptionLossRatio = 0.01;
    private const int ConsecutivePoorReportsRequired = 3;
    private const int ConsecutiveStableReportsRequired = 3;
    private static readonly TimeSpan PoorReceptionMinimumDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QualityChangeCooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StableRecoveryDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReceiverStatsExpiry = TimeSpan.FromSeconds(10);
    private static readonly Guid AnonymousReceptionSource = Guid.Empty;

    private readonly Lock _adaptationGate = new();
    private readonly Dictionary<Guid, ReceptionEvidence> _receptionBySource = [];
    private readonly VideoFrameEncodeQueue _encodeQueue;

    private bool _running;
    private long _framesCaptured;
    private long _encodedAccessUnits;
    private long _keyframesProduced;
    private long _keyFrameRequests;
    private long _keyFrameRequestSignals;
    private long _coalescedKeyFrameRequests;
    private long _pliReceived;
    private long _maximumAccessUnitBytes;
    private long _lastCapturedUtcTicks;
    private long _lastEncodedUtcTicks;
    private long _lastEncodeDurationTicks;
    private long _lastKeyFrameRecoveryLatencyTicks;

    private bool _keyFramePending;
    private KeyFrameRequestReason? _pendingKeyFrameReason;
    private DateTimeOffset? _pendingKeyFrameRequestedAt;
    private DateTimeOffset? _lastQualityChangeAt;

    public ScreenPublishPipeline(
        IScreenCaptureSource capture,
        IVideoEncoder encoder,
        MediaSessionClock? clock = null,
        TimeProvider? time = null,
        ILogger<ScreenPublishPipeline>? logger = null,
        VideoPublishProfile? profile = null)
    {
        _capture = capture;
        _encoder = encoder;
        _clock = clock;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ScreenPublishPipeline>.Instance;
        _profile = profile ?? VideoPublishProfile.Default;
        Quality = VideoQuality.InitialFor(_profile);
        _encodeQueue = new(EncodeFrame);
    }

    public event Action<EncodedVideoSample>? SampleEncoded;

    public event Action<Exception>? Failed;

    public VideoQuality Quality { get; private set; }

    public string EncoderName => _encoder.Name;

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);

    public long DroppedEncodeFrames => _encodeQueue.DroppedFrames;

    public long EncodedAccessUnits => Interlocked.Read(ref _encodedAccessUnits);

    public long KeyframesProduced => Interlocked.Read(ref _keyframesProduced);

    public long KeyFrameRequests => Interlocked.Read(ref _keyFrameRequests);

    public long KeyFrameRequestSignals => Interlocked.Read(ref _keyFrameRequestSignals);

    public long CoalescedKeyFrameRequests => Interlocked.Read(ref _coalescedKeyFrameRequests);

    public long PliReceived => Interlocked.Read(ref _pliReceived);

    public long MaximumAccessUnitBytes => Interlocked.Read(ref _maximumAccessUnitBytes);

    public DateTimeOffset? LastCapturedFrameAt => ReadTimestamp(ref _lastCapturedUtcTicks);

    public DateTimeOffset? LastEncodedAccessUnitAt => ReadTimestamp(ref _lastEncodedUtcTicks);

    public TimeSpan? LastEncodeDuration => ReadDuration(ref _lastEncodeDurationTicks);

    public TimeSpan? LastKeyFrameRecoveryLatency => ReadDuration(ref _lastKeyFrameRecoveryLatencyTicks);

    public string? LastFailure { get; private set; }

    public async Task StartAsync(MonitorInfo monitor, CancellationToken ct)
    {
        if (_running) return;
        _capture.FrameCaptured += OnFrame;
        await _capture.StartAsync(monitor, Quality, ct);
        _running = true;
    }

    public async Task StopAsync()
    {
        var wasRunning = _running;
        _running = false;
        if (wasRunning)
        {
            _capture.FrameCaptured -= OnFrame;
            await _capture.StopAsync();
        }

        await _encodeQueue.DisposeAsync();
    }

    public void RequestKeyFrame(KeyFrameRequestReason reason = KeyFrameRequestReason.Manual)
    {
        Interlocked.Increment(ref _keyFrameRequestSignals);
        if (reason == KeyFrameRequestReason.RtcpPli)
            Interlocked.Increment(ref _pliReceived);

        lock (_adaptationGate)
        {
            if (_keyFramePending)
            {
                Interlocked.Increment(ref _coalescedKeyFrameRequests);
                _logger.LogInformation(
                    "Publisher keyframe request coalesced. reason={Reason} signals={KeyFrameRequestSignals} " +
                    "coalesced={CoalescedKeyFrameRequests} encodedAccessUnits={EncodedAccessUnits}",
                    reason,
                    KeyFrameRequestSignals,
                    CoalescedKeyFrameRequests,
                    EncodedAccessUnits);
                return;
            }

            _keyFramePending = true;
            _pendingKeyFrameReason = reason;
            _pendingKeyFrameRequestedAt = _time.GetUtcNow();
        }

        try
        {
            Interlocked.Increment(ref _keyFrameRequests);
            _logger.LogInformation(
                "Publisher keyframe requested. reason={Reason} requests={KeyFrameRequests} " +
                "signals={KeyFrameRequestSignals} pliReceived={PliReceived} encodedAccessUnits={EncodedAccessUnits}",
                reason,
                KeyFrameRequests,
                KeyFrameRequestSignals,
                PliReceived,
                EncodedAccessUnits);
            _encoder.RequestKeyFrame();
        }
        catch
        {
            lock (_adaptationGate)
            {
                _keyFramePending = false;
                _pendingKeyFrameReason = null;
                _pendingKeyFrameRequestedAt = null;
            }
            throw;
        }
    }

    /// <summary>
    /// Feeds one viewer RTCP reception sample into the shared quality policy. Recovery feedback
    /// is intentionally separate: a PLI asks for a clean picture, it does not mean congestion.
    /// </summary>
    public void ReportReception(double reportedLoss) =>
        ReportReception(AnonymousReceptionSource, reportedLoss);

    /// <summary>
    /// Feeds one viewer's RTCP reception sample into the shared quality policy. Evidence is
    /// tracked per viewer: one healthy peer cannot erase another peer's sustained loss, and a
    /// single healthy peer cannot prove that all degraded peers recovered. The selected quality
    /// remains session-global because capture and encoding are intentionally shared.
    /// </summary>
    public void ReportReception(Guid sourceId, double reportedLoss) =>
        ApplyReception(sourceId, reportedLoss, "video-rtcp", "video-rtcp-loss");

    private void ApplyReception(Guid sourceId, double reportedLoss, string evidenceSource, string lossReason)
    {
        var loss = Math.Clamp(reportedLoss, 0, 1);
        var now = _time.GetUtcNow();
        VideoQuality? oldQuality = null;
        VideoQuality? newQuality = null;
        string? changeEvent = null;
        string? reason = null;
        var poorReports = 0;
        var stableReports = 0;
        TimeSpan cooldownRemaining;

        lock (_adaptationGate)
        {
            cooldownRemaining = CooldownRemaining(now);
            var evidence = GetReceptionEvidence(sourceId);
            var telemetryFresh = evidence.TelemetrySeen
                && evidence.TelemetryAt is { } telemetryAt
                && now - telemetryAt <= ReceiverStatsExpiry;
            if (evidence.TelemetrySeen && !telemetryFresh)
                evidence.ResetStable();

            if (telemetryFresh)
            {
                if (evidence.TelemetryLoss > loss)
                {
                    loss = evidence.TelemetryLoss;
                    evidenceSource = "video.receiver_stats";
                    lossReason = evidence.TelemetryLossReason;
                }
                if (evidence.LowFpsSustained && loss <= PoorReceptionLossRatio)
                {
                    loss = PoorReceptionLossRatio;
                    evidenceSource = "video.receiver_stats";
                    lossReason = "low-decoded-fps";
                }
            }

            if (loss >= PoorReceptionLossRatio)
            {
                evidence.ResetStable();
                evidence.PoorSince ??= now;
                evidence.ConsecutivePoorReports++;
                poorReports = evidence.ConsecutivePoorReports;

                _logger.LogInformation(
                    "video.quality.degradation.considered reason={Reason} evidenceSource={EvidenceSource} receptionSource={ReceptionSource} " +
                    "reportedLoss={ReportedLoss:F4} consecutivePoorReports={ConsecutivePoorReports} " +
                    "poorDurationMs={PoorDurationMs:F0} cooldownRemainingMs={CooldownRemainingMs:F0} " +
                    "maxHeight={MaxHeight} bitrate={Bitrate}",
                    lossReason,
                    evidenceSource,
                    sourceId,
                    loss,
                    poorReports,
                    (now - evidence.PoorSince.Value).TotalMilliseconds,
                    cooldownRemaining.TotalMilliseconds,
                    Quality.MaxHeight,
                    Quality.TargetBitsPerSecond);

                if ((poorReports >= ConsecutivePoorReportsRequired || evidence.LowFpsSustained)
                    && now - evidence.PoorSince.Value >= PoorReceptionMinimumDuration
                    && cooldownRemaining == TimeSpan.Zero)
                {
                    var reduced = Quality.Reduced(_profile);
                    if (reduced != Quality)
                    {
                        oldQuality = Quality;
                        newQuality = reduced;
                        Quality = reduced;
                        _lastQualityChangeAt = now;
                        changeEvent = "video.quality.changed";
                        reason = lossReason;
                        ResetAllReceptionEvidence();
                    }
                    else
                    {
                        evidence.ResetPoor();
                    }
                }
            }
            else if (loss <= StableReceptionLossRatio)
            {
                evidence.ResetPoor();

                var telemetryRecoveryEligible = !evidence.TelemetrySeen
                    || telemetryFresh
                        && evidence.TelemetryLoss <= StableReceptionLossRatio
                        && evidence.DecodedFramesPerSecond >= evidence.TargetFramesPerSecond * 0.95;
                if (!telemetryRecoveryEligible)
                    evidence.ResetStable();

                if (!telemetryRecoveryEligible || Quality == VideoQuality.InitialFor(_profile))
                {
                    evidence.ResetStable();
                }
                else
                {
                    evidence.StableSince ??= now;
                    evidence.ConsecutiveStableReports++;
                    stableReports = evidence.ConsecutiveStableReports;

                    _logger.LogInformation(
                        "video.quality.recovery.considered reason={Reason} evidenceSource={EvidenceSource} receptionSource={ReceptionSource} " +
                        "reportedLoss={ReportedLoss:F4} consecutiveStableReports={ConsecutiveStableReports} " +
                        "stableDurationMs={StableDurationMs:F0} cooldownRemainingMs={CooldownRemainingMs:F0} " +
                        "maxHeight={MaxHeight} bitrate={Bitrate}",
                        "stable-video-reception",
                        evidenceSource,
                        sourceId,
                        loss,
                        stableReports,
                        (now - evidence.StableSince.Value).TotalMilliseconds,
                        cooldownRemaining.TotalMilliseconds,
                        Quality.MaxHeight,
                        Quality.TargetBitsPerSecond);

                    if (AllReceptionSourcesStable(now)
                        && cooldownRemaining == TimeSpan.Zero)
                    {
                        var improved = Quality.Improved(_profile);
                        if (improved != Quality)
                        {
                            oldQuality = Quality;
                            newQuality = improved;
                            Quality = improved;
                            _lastQualityChangeAt = now;
                            changeEvent = "video.quality.recovered";
                            reason = "stable-video-reception";
                            ResetAllReceptionEvidence();
                        }
                    }
                }
            }
            else
            {
                // Neither genuinely poor nor genuinely stable. Do not let stale evidence from
                // this viewer count toward a later shared quality transition.
                evidence.Reset();
            }
        }

        if (oldQuality is null || newQuality is null || changeEvent is null)
            return;

        var oldResolution = ResolutionFor(oldQuality);
        var newResolution = ResolutionFor(newQuality);

        if (oldQuality.FramesPerSecond != newQuality.FramesPerSecond)
            _capture.SetFrameRate(newQuality.FramesPerSecond);

        _logger.LogWarning(
            "{QualityEvent} reason={Reason} evidenceSource={EvidenceSource} receptionSource={ReceptionSource} reportedLoss={ReportedLoss:F4} " +
            "oldResolution={OldResolution} newResolution={NewResolution} " +
            "oldFps={OldFps} newFps={NewFps} oldBitrate={OldBitrate} newBitrate={NewBitrate} " +
            "consecutivePoorReports={ConsecutivePoorReports} consecutiveStableReports={ConsecutiveStableReports} " +
            "cooldownMs={CooldownMs:F0}",
            changeEvent,
            reason,
            evidenceSource,
            sourceId,
            loss,
            oldResolution,
            newResolution,
            oldQuality.FramesPerSecond,
            newQuality.FramesPerSecond,
            oldQuality.TargetBitsPerSecond,
            newQuality.TargetBitsPerSecond,
            poorReports,
            stableReports,
            QualityChangeCooldown.TotalMilliseconds);

        // Reconfiguring bitrate or geometry starts a new encoder configuration. Make the
        // transition a clean random-access point for every viewer, independently of RTCP PLI.
        RequestKeyFrame(KeyFrameRequestReason.QualityChange);
    }

    public void ReportReceiverStats(Guid sourceId, VideoReceiverStats stats)
    {
        var now = _time.GetUtcNow();
        var packetTotal = stats.RtpPacketsReceived + stats.RtpPacketsLost;
        var unitTotal = stats.AccessUnitsReceived;
        var packetLoss = packetTotal == 0 ? 0 : stats.RtpPacketsLost / (double)packetTotal;
        var incompleteRatio = unitTotal == 0 ? 0 : stats.IncompleteAccessUnits / (double)unitTotal;
        var loss = Math.Max(packetLoss, incompleteRatio);
        var lossReason = incompleteRatio > packetLoss
            ? "receiver-video-access-unit-loss" : "receiver-video-packet-loss";
        var decodedFps = stats.IntervalMilliseconds <= 0 ? 0
            : stats.DecodedFrames * 1000d / stats.IntervalMilliseconds;
        var lowFps = decodedFps < stats.TargetFramesPerSecond * 0.85;
        var sustainedLowFps = false;
        lock (_adaptationGate)
        {
            var evidence = GetReceptionEvidence(sourceId);
            if (evidence.TelemetrySeen && evidence.TelemetryAt is { } previousTelemetryAt
                && now - previousTelemetryAt > ReceiverStatsExpiry)
            {
                evidence.ResetStable();
                evidence.LowFpsSince = null;
                evidence.LowFpsSustained = false;
            }

            if (lowFps)
            {
                evidence.LowFpsSince ??= now;
                evidence.LowFpsSustained = now - evidence.LowFpsSince.Value >= PoorReceptionMinimumDuration;
                sustainedLowFps = evidence.LowFpsSustained;
                if (evidence.LowFpsSustained)
                    evidence.PoorSince ??= evidence.LowFpsSince;
            }
            else
            {
                evidence.LowFpsSince = null;
                evidence.LowFpsSustained = false;
            }

            evidence.TelemetrySeen = true;
            evidence.TelemetryAt = now;
            evidence.TelemetryLoss = loss;
            evidence.TelemetryLossReason = lossReason;
            evidence.DecodedFramesPerSecond = decodedFps;
            evidence.TargetFramesPerSecond = stats.TargetFramesPerSecond;
        }
        var evidenceLoss = sustainedLowFps
            ? Math.Max(loss, PoorReceptionLossRatio)
            : loss;
        ApplyReception(sourceId, evidenceLoss, "video.receiver_stats",
            sustainedLowFps && loss < PoorReceptionLossRatio ? "low-decoded-fps" : lossReason);
    }

    public void RemoveReceptionSource(Guid sourceId)
    {
        lock (_adaptationGate)
            _receptionBySource.Remove(sourceId);
    }

    private ReceptionEvidence GetReceptionEvidence(Guid sourceId)
    {
        if (_receptionBySource.TryGetValue(sourceId, out var evidence))
            return evidence;

        evidence = new ReceptionEvidence();
        _receptionBySource.Add(sourceId, evidence);
        return evidence;
    }

    private bool AllReceptionSourcesStable(DateTimeOffset now) =>
        _receptionBySource.Count > 0
        && _receptionBySource.Values.All(evidence =>
            evidence.ConsecutiveStableReports >= ConsecutiveStableReportsRequired
            && evidence.StableSince is { } stableSince
            && now - stableSince >= StableRecoveryDuration
            && (!evidence.TelemetrySeen
                || evidence.TelemetryAt is { } telemetryAt
                    && now - telemetryAt <= ReceiverStatsExpiry
                    && evidence.TelemetryLoss <= StableReceptionLossRatio
                    && evidence.DecodedFramesPerSecond >= evidence.TargetFramesPerSecond * 0.95));

    private void ResetAllReceptionEvidence()
    {
        foreach (var evidence in _receptionBySource.Values)
            evidence.Reset();
    }

    private TimeSpan CooldownRemaining(DateTimeOffset now)
    {
        if (_lastQualityChangeAt is not { } changedAt)
            return TimeSpan.Zero;

        var remaining = QualityChangeCooldown - (now - changedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private string ResolutionFor(VideoQuality quality)
    {
        var monitor = _capture.Monitor;
        if (monitor.Width <= 0 || monitor.Height <= 0)
            return $"max-height-{quality.MaxHeight}";

        var (width, height) = quality.ScaleFor(monitor.Width, monitor.Height);
        return $"{width}x{height}";
    }

    private void OnFrame(VideoFrame frame)
    {
        if (!_running) return;

        Interlocked.Increment(ref _framesCaptured);
        Interlocked.Exchange(ref _lastCapturedUtcTicks, _time.GetUtcNow().UtcTicks);

        var droppedBefore = _encodeQueue.DroppedFrames;
        _encodeQueue.Enqueue(frame);
        if (_encodeQueue.DroppedFrames != droppedBefore)
            RequestKeyFrame(KeyFrameRequestReason.PacketLoss);
    }

    private void EncodeFrame(VideoFrame frame)
    {
        if (!_running) return;

        EncodedVideoSample? sample;
        var encodeStarted = _time.GetTimestamp();
        try
        {
            var stampedFrame = _clock is null
                ? frame
                : new VideoFrame(frame.Width, frame.Height, frame.Bgra, _clock.Now);
            sample = _encoder.Encode(stampedFrame, Quality);
        }
        catch (Exception e)
        {
            LastFailure = $"{e.GetType().Name} (0x{e.HResult:X8}): {e.Message}";
            _logger.LogError(
                e,
                "Encoder exception stopped ScreenPublishPipeline. hresult=0x{HResult:X8} " +
                "framesCaptured={FramesCaptured} encodedAccessUnits={EncodedAccessUnits}",
                e.HResult,
                FramesCaptured,
                EncodedAccessUnits);

            // Stop before reporting: a failing encoder called once per frame at 30 Hz turns
            // one fault into a flood, and the session is over either way.
            _running = false;
            _capture.FrameCaptured -= OnFrame;
            Failed?.Invoke(e);
            return;
        }
        finally
        {
            var encodeDuration = _time.GetElapsedTime(encodeStarted);
            Interlocked.Exchange(ref _lastEncodeDurationTicks, encodeDuration.Ticks);
            _logger.LogTrace(
                "video.encode.completed encodeMs={EncodeMs:F3} maxHeight={MaxHeight} fps={Fps} bitrate={Bitrate}",
                encodeDuration.TotalMilliseconds,
                Quality.MaxHeight,
                Quality.FramesPerSecond,
                Quality.TargetBitsPerSecond);
        }

        if (sample is not { } encoded) return;

        Interlocked.Increment(ref _encodedAccessUnits);
        Interlocked.Exchange(ref _lastEncodedUtcTicks, _time.GetUtcNow().UtcTicks);
        UpdateMaximum(ref _maximumAccessUnitBytes, encoded.Data.Length);
        if (encoded.IsKeyFrame)
        {
            Interlocked.Increment(ref _keyframesProduced);

            KeyFrameRequestReason? fulfilledReason;
            DateTimeOffset? requestedAt;
            lock (_adaptationGate)
            {
                fulfilledReason = _keyFramePending ? _pendingKeyFrameReason : null;
                requestedAt = _keyFramePending ? _pendingKeyFrameRequestedAt : null;
                _keyFramePending = false;
                _pendingKeyFrameReason = null;
                _pendingKeyFrameRequestedAt = null;
            }

            if (fulfilledReason is { } reason)
            {
                var recoveryLatency = requestedAt is { } requestTime
                    ? _time.GetUtcNow() - requestTime
                    : TimeSpan.Zero;
                if (recoveryLatency > TimeSpan.Zero)
                    Interlocked.Exchange(ref _lastKeyFrameRecoveryLatencyTicks, recoveryLatency.Ticks);

                _logger.LogInformation(
                    "Publisher recovery keyframe produced. reason={Reason} recoveryLatencyMs={RecoveryLatencyMs:F1} " +
                    "keyframesProduced={KeyframesProduced} encodedAccessUnits={EncodedAccessUnits} " +
                    "requests={KeyFrameRequests} coalesced={CoalescedKeyFrameRequests}",
                    reason,
                    recoveryLatency.TotalMilliseconds,
                    KeyframesProduced,
                    EncodedAccessUnits,
                    KeyFrameRequests,
                    CoalescedKeyFrameRequests);
            }
        }

        SampleEncoded?.Invoke(encoded);
    }

    private static DateTimeOffset? ReadTimestamp(ref long source)
    {
        var ticks = Interlocked.Read(ref source);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static TimeSpan? ReadDuration(ref long source)
    {
        var ticks = Interlocked.Read(ref source);
        return ticks <= 0 ? null : TimeSpan.FromTicks(ticks);
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Interlocked.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private sealed class ReceptionEvidence
    {
        public bool TelemetrySeen { get; set; }
        public DateTimeOffset? TelemetryAt { get; set; }
        public double TelemetryLoss { get; set; }
        public string TelemetryLossReason { get; set; } = "receiver-video-packet-loss";
        public double DecodedFramesPerSecond { get; set; }
        public double TargetFramesPerSecond { get; set; }
        public DateTimeOffset? LowFpsSince { get; set; }
        public bool LowFpsSustained { get; set; }
        public int ConsecutivePoorReports { get; set; }
        public int ConsecutiveStableReports { get; set; }
        public DateTimeOffset? PoorSince { get; set; }
        public DateTimeOffset? StableSince { get; set; }

        public void ResetPoor()
        {
            ConsecutivePoorReports = 0;
            PoorSince = null;
        }

        public void ResetStable()
        {
            ConsecutiveStableReports = 0;
            StableSince = null;
        }

        public void Reset()
        {
            ResetPoor();
            ResetStable();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _capture.DisposeAsync();
        _encoder.Dispose();
    }
}
