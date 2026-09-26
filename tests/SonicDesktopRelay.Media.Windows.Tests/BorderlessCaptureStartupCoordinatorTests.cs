using Microsoft.Extensions.Logging;
using SonicDesktopRelay.Media.Windows;
using Windows.Graphics.Capture;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class BorderlessCaptureStartupCoordinatorTests
{
    [Fact]
    public async Task Bordered_fallback_still_starts_capture()
    {
        var policy = new StubPolicy(new BorderlessCaptureResult(
            IsEnabled: false, Outcome: "denied", Reason: "user denied access"));
        var starts = 0;

        var result = await BorderlessCaptureStartupCoordinator.ConfigureAndStartAsync(
            policy, null!, () => starts++, CancellationToken.None);

        Assert.Equal("denied", result.Outcome);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Cancelled_permission_request_does_not_start_capture()
    {
        var pending = new TaskCompletionSource<BorderlessCaptureResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new StubPolicy(pending.Task);
        using var cancellation = new CancellationTokenSource();
        var starts = 0;

        var start = BorderlessCaptureStartupCoordinator.ConfigureAndStartAsync(
            policy, null!, () => starts++, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, starts);
    }

    private sealed class StubPolicy(Task<BorderlessCaptureResult> result) : IBorderlessCapturePolicy
    {
        public StubPolicy(BorderlessCaptureResult result)
            : this(Task.FromResult(result)) { }

        public Task<BorderlessCaptureResult> TryEnableAsync(GraphicsCaptureSession session, CancellationToken ct) =>
            result.WaitAsync(ct);
    }
}
