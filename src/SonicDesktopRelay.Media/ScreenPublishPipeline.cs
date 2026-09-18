using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SonicDesktopRelay.Media;

/// <summary>
/// capture → encode → one event, for the whole session. Every viewer subscribes to the same
/// <see cref="SampleEncoded"/>, so adding the fourth viewer costs a subscription rather than
/// a fourth encoder.
/// </summary>
public sealed class ScreenPublishPipeline(
    IScreenCaptureSource capture,
    IVideoEncoder encoder,
    MediaSessionClock? clock = null,
    TimeProvider? time = null,
    ILogger<ScreenPublishPipeline>? logger = null,
    VideoPublishProfile? profile = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger<ScreenPublishPipeline> _logger =
        logger ?? NullLogger<ScreenPublishPipeline>.Instance;
    private readonly VideoPublishProfile _profile = profile ?? VideoPublishProfile.Default;

    // RTCP reports normally arrive periodically. Requiring both multiple reports and elapsed
    // time makes a burst insufficient on its own, while the cooldown prevents staircase drops.
    private const double PoorReceptionLossRatio = 0.05;
    private const double StableReceptionLossRatio = 0.01;
    private const int ConsecutivePoorReportsRequired = 3;
    private const int ConsecutiveStableReportsRequired = 3;
    private static readonly TimeSpan PoorReceptionMinimumDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QualityChangeCooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StableRecoveryDuration = TimeSpan.FromSeconds(30);
    private static readonly Guid AnonymousReceptionSource = Guid.Empty;

    private readonly Lock _adaptationGate = new();
    private readonly Dictionary<Guid, ReceptionEvidence> _receptionBySource = [];

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

    private bool _keyFramePending;
    private KeyFrameRequestReason? _pendingKeyFrameReason;
    private DateTimeOffset? _lastQualityChangeAt;

    public event Action<EncodedVideoSample>? SampleEncoded;

    public event Action<Exception>? Failed;

    public VideoQuality Quality { get; private set; } =
        VideoQuality.InitialFor(profile ?? VideoPublishProfile.Default);

    public string EncoderName => encoder.Name;

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);

    public long EncodedAccessUnits => Interlocked.Read(ref _encodedAccessUnits);

    public long KeyframesProduced => Interlocked.Read(ref _keyframesProduced);

    public long KeyFrameRequests => Interlocked.Read(ref _keyFrameRequests);

    public long KeyFrameRequestSignals => Interlocked.Read(ref _keyFrameRequestSignals);

    public long CoalescedKeyFrameRequests => Interlocked.Read(ref _coalescedKeyFrameRequests);

    public long PliReceived => Interlocked.Read(ref _pliReceived);

    public long MaximumAccessUnitBytes => Interlocked.Read(ref _maximumAccessUnitBytes);

    public DateTimeOffset? LastCapturedFrameAt => ReadTimestamp(ref _lastCapturedUtcTicks);

    public DateTimeOffset? LastEncodedAccessUnitAt => ReadTimestamp(ref _lastEncodedUtcTicks);

    public string? LastFailure { get; private set; }

    public async Task StartAsync(MonitorInfo monitor, CancellationToken ct)
    {
        if (_running) return;
        capture.FrameCaptured += OnFrame;
        await capture.StartAsync(monitor, Quality, ct);
        _running = true;
    }

    public async Task StopAsync()
    {
        if (!_running) return;
        _running = false;
        capture.FrameCaptured -= OnFrame;
        await capture.StopAsync();
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
            encoder.RequestKeyFrame();
        }
        catch
        {
            lock (_adaptationGate)
            {
                _keyFramePending = false;
                _pendingKeyFrameReason = null;
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
    public void ReportReception(Guid sourceId, double reportedLoss)
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

            if (loss >= PoorReceptionLossRatio)
            {
                evidence.ResetStable();
                evidence.PoorSince ??= now;
                evidence.ConsecutivePoorReports++;
                poorReports = evidence.ConsecutivePoorReports;

                _logger.LogInformation(
                    "video.quality.degradation.considered reason={Reason} receptionSource={ReceptionSource} " +
                    "reportedLoss={ReportedLoss:F4} consecutivePoorReports={ConsecutivePoorReports} " +
                    "poorDurationMs={PoorDurationMs:F0} cooldownRemainingMs={CooldownRemainingMs:F0} " +
                    "maxHeight={MaxHeight} bitrate={Bitrate}",
                    "rtcp-loss",
                    sourceId,
                    loss,
                    poorReports,
                    (now - evidence.PoorSince.Value).TotalMilliseconds,
                    cooldownRemaining.TotalMilliseconds,
                    Quality.MaxHeight,
                    Quality.TargetBitsPerSecond);

                if (poorReports >= ConsecutivePoorReportsRequired
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
                        reason = "sustained-rtcp-loss";
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

                if (Quality == VideoQuality.InitialFor(_profile))
                {
                    evidence.ResetStable();
                }
                else
                {
                    evidence.StableSince ??= now;
                    evidence.ConsecutiveStableReports++;
                    stableReports = evidence.ConsecutiveStableReports;

                    _logger.LogInformation(
                        "video.quality.recovery.considered reason={Reason} receptionSource={ReceptionSource} " +
                        "reportedLoss={ReportedLoss:F4} consecutiveStableReports={ConsecutiveStableReports} " +
                        "stableDurationMs={StableDurationMs:F0} cooldownRemainingMs={CooldownRemainingMs:F0} " +
                        "maxHeight={MaxHeight} bitrate={Bitrate}",
                        "stable-rtcp-reception",
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
                            reason = "stable-rtcp-reception";
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

        _logger.LogWarning(
            "{QualityEvent} reason={Reason} receptionSource={ReceptionSource} reportedLoss={ReportedLoss:F4} " +
            "oldResolution={OldResolution} newResolution={NewResolution} " +
            "oldBitrate={OldBitrate} newBitrate={NewBitrate} " +
            "consecutivePoorReports={ConsecutivePoorReports} consecutiveStableReports={ConsecutiveStableReports} " +
            "cooldownMs={CooldownMs:F0}",
            changeEvent,
            reason,
            sourceId,
            loss,
            oldResolution,
            newResolution,
            oldQuality.TargetBitsPerSecond,
            newQuality.TargetBitsPerSecond,
            poorReports,
            stableReports,
            QualityChangeCooldown.TotalMilliseconds);

        // Reconfiguring bitrate or geometry starts a new encoder configuration. Make the
        // transition a clean random-access point for every viewer, independently of RTCP PLI.
        RequestKeyFrame(KeyFrameRequestReason.QualityChange);
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
            && now - stableSince >= StableRecoveryDuration);

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
        var monitor = capture.Monitor;
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

        EncodedVideoSample? sample;
        try
        {
            var stampedFrame = clock is null
                ? frame
                : new VideoFrame(frame.Width, frame.Height, frame.Bgra, clock.Now);
            sample = encoder.Encode(stampedFrame, Quality);
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
            capture.FrameCaptured -= OnFrame;
            Failed?.Invoke(e);
            return;
        }

        if (sample is not { } encoded) return;

        Interlocked.Increment(ref _encodedAccessUnits);
        Interlocked.Exchange(ref _lastEncodedUtcTicks, _time.GetUtcNow().UtcTicks);
        UpdateMaximum(ref _maximumAccessUnitBytes, encoded.Data.Length);
        if (encoded.IsKeyFrame)
        {
            Interlocked.Increment(ref _keyframesProduced);

            KeyFrameRequestReason? fulfilledReason;
            lock (_adaptationGate)
            {
                fulfilledReason = _keyFramePending ? _pendingKeyFrameReason : null;
                _keyFramePending = false;
                _pendingKeyFrameReason = null;
            }

            if (fulfilledReason is { } reason)
            {
                _logger.LogInformation(
                    "Publisher recovery keyframe produced. reason={Reason} keyframesProduced={KeyframesProduced} " +
                    "encodedAccessUnits={EncodedAccessUnits} requests={KeyFrameRequests} coalesced={CoalescedKeyFrameRequests}",
                    reason,
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
        await capture.DisposeAsync();
        encoder.Dispose();
    }
}
