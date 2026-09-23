namespace SonicDesktopRelay.Media.Windows;

internal sealed record MediaFoundationTransformCandidate(Guid Clsid, bool IsHardware);

/// <summary>Tracks transforms that failed at runtime and bounds one operation to one fallback.</summary>
internal sealed class MediaFoundationTransformRetryPolicy
{
    internal const int NeedMoreInputHResult = unchecked((int)0xC00D6D72);
    internal const int StreamChangeHResult = unchecked((int)0xC00D6D61);

    private readonly HashSet<Guid> _failedClsids = [];

    internal static void EnsureCandidateSelected(
        Func<bool> hasSelection,
        Action selectCandidate)
    {
        if (!hasSelection())
            selectCandidate();
    }

    public IReadOnlyList<MediaFoundationTransformCandidate> OrderCandidates(
        IEnumerable<MediaFoundationTransformCandidate> candidates) =>
        candidates
            .Where(candidate => !_failedClsids.Contains(candidate.Clsid))
            .OrderByDescending(candidate => candidate.IsHardware)
            .ToArray();

    public void ExcludeFailed(Guid clsid)
    {
        if (clsid != Guid.Empty)
            _failedClsids.Add(clsid);
    }

    internal static bool IsHardDecoderOutputFailure(int hresult) =>
        hresult < 0
        && hresult != NeedMoreInputHResult
        && hresult != StreamChangeHResult;

    internal static TResult? ReadAsyncOutputIfReady<TResult>(
        bool outputReady,
        Func<TResult?> readOutput)
        where TResult : struct =>
        outputReady ? readOutput() : null;

    public static TResult ExecuteWithSingleFallback<TResult>(
        Func<TResult> current,
        Func<Exception, bool> isHardFailure,
        Func<TResult> fallback)
    {
        try
        {
            return current();
        }
        catch (Exception exception) when (isHardFailure(exception))
        {
            return fallback();
        }
    }
}
