namespace SonicDesktopRelay.Rtc;

/// <summary>
/// Keeps corrupted prediction chains away from the decoder and rate-limits recovery feedback.
/// A recovery episode ends only when an IDR access unit is available for delivery.
/// </summary>
internal sealed class ViewerVideoRecoveryGate(TimeSpan minimumPliInterval)
{
    private DateTimeOffset? _lastPliAt;

    public bool Active { get; private set; }
    public long RecoveryEpisodes { get; private set; }
    public long RecoveryKeyframesRequested { get; private set; }
    public long SuspectAccessUnitsSuppressed { get; private set; }

    public void BeginRecovery()
    {
        if (Active)
            return;

        Active = true;
        RecoveryEpisodes++;
        _lastPliAt = null;
    }

    public bool TryRequestPli(DateTimeOffset now)
    {
        if (!Active)
            BeginRecovery();

        if (_lastPliAt is { } previous && now - previous < minimumPliInterval)
            return false;

        _lastPliAt = now;
        RecoveryKeyframesRequested++;
        return true;
    }

    public bool ShouldDeliver(bool isIdr, bool hasVcl)
    {
        if (!Active)
            return true;

        if (isIdr)
        {
            Active = false;
            _lastPliAt = null;
            return true;
        }

        // Parameter sets / AUD / SEI are safe and may be required before the requested IDR.
        // They do not end recovery because no clean reference picture exists yet.
        if (!hasVcl)
            return true;

        SuspectAccessUnitsSuppressed++;
        return false;
    }
}
