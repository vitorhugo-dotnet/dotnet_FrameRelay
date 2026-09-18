using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows;

internal enum OutputSampleAllocationMode
{
    MftProvided,
    CallerOptional,
    CallerRequired
}

internal readonly record struct OutputSampleCleanupPlan(
    bool DisposeCallerSample,
    bool DisposeReturnedSample,
    bool DetachReturnedWrapper,
    bool DisposeEvents)
{
    internal int OwnedSampleDisposeCount =>
        (DisposeCallerSample ? 1 : 0) +
        (DisposeReturnedSample ? 1 : 0);
}

internal static class MediaFoundationOutputSampleLifetime
{
    internal static OutputSampleAllocationMode SelectAllocationMode(int outputStreamFlags)
    {
        var flags = (OutputStreamInfoFlags)outputStreamFlags;

        // PROVIDES_SAMPLES forbids a caller-provided pSample, so it takes precedence
        // if an MFT reports both allocation-capability flags.
        if ((flags & OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0)
            return OutputSampleAllocationMode.MftProvided;

        // CAN_PROVIDE_SAMPLES supports both models. FrameRelay deliberately keeps
        // supplying its own correctly-sized sample to preserve the existing path.
        if ((flags & OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0)
            return OutputSampleAllocationMode.CallerOptional;

        return OutputSampleAllocationMode.CallerRequired;
    }

    internal static bool CallerSuppliesSample(OutputSampleAllocationMode allocationMode) =>
        allocationMode is OutputSampleAllocationMode.CallerOptional
            or OutputSampleAllocationMode.CallerRequired;

    internal static OutputSampleCleanupPlan PlanCleanup(
        OutputSampleAllocationMode allocationMode,
        bool callerSamplePresent,
        bool returnedSamplePresent,
        bool eventsPresent)
    {
        var callerOwnsSample = CallerSuppliesSample(allocationMode);

        return new OutputSampleCleanupPlan(
            DisposeCallerSample: callerOwnsSample && callerSamplePresent,
            DisposeReturnedSample: !callerOwnsSample && returnedSamplePresent,
            DetachReturnedWrapper: callerOwnsSample && returnedSamplePresent,
            DisposeEvents: eventsPresent);
    }
}
