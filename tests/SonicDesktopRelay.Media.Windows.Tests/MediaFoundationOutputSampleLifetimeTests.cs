using SonicDesktopRelay.Media.Windows;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationOutputSampleLifetimeTests
{
    [Fact]
    public void Caller_allocated_output_disposes_exactly_one_owned_sample()
    {
        var plan = MediaFoundationOutputSampleLifetime.PlanCleanup(
            OutputSampleAllocationMode.CallerRequired,
            callerSamplePresent: true,
            returnedSamplePresent: true,
            eventsPresent: false);

        Assert.True(plan.DisposeCallerSample);
        Assert.False(plan.DisposeReturnedSample);
        Assert.True(plan.DetachReturnedWrapper);
        Assert.Equal(1, plan.OwnedSampleDisposeCount);
    }

    [Fact]
    public void Mft_allocated_output_disposes_exactly_one_returned_sample()
    {
        var plan = MediaFoundationOutputSampleLifetime.PlanCleanup(
            OutputSampleAllocationMode.MftProvided,
            callerSamplePresent: false,
            returnedSamplePresent: true,
            eventsPresent: false);

        Assert.False(plan.DisposeCallerSample);
        Assert.True(plan.DisposeReturnedSample);
        Assert.False(plan.DetachReturnedWrapper);
        Assert.Equal(1, plan.OwnedSampleDisposeCount);
    }

    [Fact]
    public void Distinct_managed_wrapper_on_caller_owned_path_is_detached_not_disposed()
    {
        var plan = MediaFoundationOutputSampleLifetime.PlanCleanup(
            OutputSampleAllocationMode.CallerOptional,
            callerSamplePresent: true,
            returnedSamplePresent: true,
            eventsPresent: false);

        Assert.True(plan.DisposeCallerSample);
        Assert.False(plan.DisposeReturnedSample);
        Assert.True(plan.DetachReturnedWrapper);
        Assert.Equal(1, plan.OwnedSampleDisposeCount);
    }

    [Fact]
    public void Provides_samples_selects_mft_owned_path()
    {
        var mode = MediaFoundationOutputSampleLifetime.SelectAllocationMode(
            (int)OutputStreamInfoFlags.OutputStreamProvidesSamples);

        Assert.Equal(OutputSampleAllocationMode.MftProvided, mode);
        Assert.False(MediaFoundationOutputSampleLifetime.CallerSuppliesSample(mode));
    }

    [Fact]
    public void Can_provide_samples_selects_caller_optional_path()
    {
        var mode = MediaFoundationOutputSampleLifetime.SelectAllocationMode(
            (int)OutputStreamInfoFlags.OutputStreamCanProvideSamples);

        Assert.Equal(OutputSampleAllocationMode.CallerOptional, mode);
        Assert.True(MediaFoundationOutputSampleLifetime.CallerSuppliesSample(mode));
    }

    [Fact]
    public void No_allocation_flags_selects_caller_required_path()
    {
        var mode = MediaFoundationOutputSampleLifetime.SelectAllocationMode(0);

        Assert.Equal(OutputSampleAllocationMode.CallerRequired, mode);
        Assert.True(MediaFoundationOutputSampleLifetime.CallerSuppliesSample(mode));
    }

    [Fact]
    public void Output_events_are_disposed_independently_from_sample_ownership()
    {
        var callerPlan = MediaFoundationOutputSampleLifetime.PlanCleanup(
            OutputSampleAllocationMode.CallerRequired,
            callerSamplePresent: true,
            returnedSamplePresent: true,
            eventsPresent: true);
        var mftPlan = MediaFoundationOutputSampleLifetime.PlanCleanup(
            OutputSampleAllocationMode.MftProvided,
            callerSamplePresent: false,
            returnedSamplePresent: true,
            eventsPresent: true);

        Assert.True(callerPlan.DisposeEvents);
        Assert.True(mftPlan.DisposeEvents);
        Assert.Equal(1, callerPlan.OwnedSampleDisposeCount);
        Assert.Equal(1, mftPlan.OwnedSampleDisposeCount);
    }

    [Fact]
    public void Need_more_input_cleanup_does_not_add_a_second_sample_disposal()
    {
        var plan = MediaFoundationOutputSampleLifetime.PlanCleanup(
            OutputSampleAllocationMode.CallerRequired,
            callerSamplePresent: true,
            returnedSamplePresent: true,
            eventsPresent: false);

        Assert.Equal(1, plan.OwnedSampleDisposeCount);
        Assert.True(plan.DisposeCallerSample);
        Assert.False(plan.DisposeReturnedSample);
    }

    [Fact]
    public void Stream_change_cleanup_does_not_add_a_second_sample_disposal()
    {
        var plan = MediaFoundationOutputSampleLifetime.PlanCleanup(
            OutputSampleAllocationMode.CallerOptional,
            callerSamplePresent: true,
            returnedSamplePresent: true,
            eventsPresent: true);

        Assert.Equal(1, plan.OwnedSampleDisposeCount);
        Assert.True(plan.DetachReturnedWrapper);
        Assert.True(plan.DisposeEvents);
    }

    [Fact]
    public void Missing_mft_output_sample_has_nothing_to_release()
    {
        var plan = MediaFoundationOutputSampleLifetime.PlanCleanup(
            OutputSampleAllocationMode.MftProvided,
            callerSamplePresent: false,
            returnedSamplePresent: false,
            eventsPresent: false);

        Assert.Equal(0, plan.OwnedSampleDisposeCount);
        Assert.False(plan.DisposeReturnedSample);
        Assert.False(plan.DetachReturnedWrapper);
    }
}
