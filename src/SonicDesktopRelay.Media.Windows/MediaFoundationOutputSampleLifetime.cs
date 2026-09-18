using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows;

internal enum OutputSampleAllocationMode
{
    CallerAllocated,
    MftAllocated
}

internal static class MediaFoundationOutputSampleLifetime
{
    internal static bool ShouldSupplyCallerSample(int outputStreamFlags)
    {
        var flags = (OutputStreamInfoFlags)outputStreamFlags;
        var providesSamples =
            (flags & OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        var canProvideSamples =
            (flags & OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0;

        if (providesSamples && canProvideSamples)
        {
            throw new InvalidOperationException(
                "A Media Foundation output stream cannot both provide and optionally provide samples.");
        }

        // FrameRelay keeps its existing caller-buffer policy whenever the MFT allows it.
        // PROVIDES_SAMPLES is the only mode in which the caller must pass pSample = NULL.
        return !providesSamples;
    }

    internal static OutputSampleAllocationMode ResolveAllocationMode(
        int outputStreamFlags,
        bool callerSuppliedSample)
    {
        var flags = (OutputStreamInfoFlags)outputStreamFlags;
        var providesSamples =
            (flags & OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        var canProvideSamples =
            (flags & OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0;

        if (providesSamples && canProvideSamples)
        {
            throw new InvalidOperationException(
                "A Media Foundation output stream cannot both provide and optionally provide samples.");
        }

        if (providesSamples)
        {
            if (callerSuppliedSample)
            {
                throw new InvalidOperationException(
                    "MFT_OUTPUT_STREAM_PROVIDES_SAMPLES requires pSample to be null.");
            }

            return OutputSampleAllocationMode.MftAllocated;
        }

        if (canProvideSamples)
        {
            return callerSuppliedSample
                ? OutputSampleAllocationMode.CallerAllocated
                : OutputSampleAllocationMode.MftAllocated;
        }

        if (!callerSuppliedSample)
        {
            throw new InvalidOperationException(
                "This Media Foundation output stream requires a caller-allocated sample.");
        }

        return OutputSampleAllocationMode.CallerAllocated;
    }

    internal static T? SelectSampleForConversion<T>(
        OutputSampleAllocationMode allocationMode,
        T? callerAllocatedSample,
        T? processOutputSample)
        where T : class
    {
        return allocationMode == OutputSampleAllocationMode.CallerAllocated
            ? callerAllocatedSample
            : processOutputSample;
    }

    internal static void DisposeOwnedResources(
        OutputSampleAllocationMode allocationMode,
        IDisposable? callerAllocatedSample,
        IDisposable? processOutputSample,
        IDisposable? outputEvents)
    {
        try
        {
            outputEvents?.Dispose();
        }
        finally
        {
            if (allocationMode == OutputSampleAllocationMode.CallerAllocated)
                callerAllocatedSample?.Dispose();
            else
                processOutputSample?.Dispose();
        }
    }
}
