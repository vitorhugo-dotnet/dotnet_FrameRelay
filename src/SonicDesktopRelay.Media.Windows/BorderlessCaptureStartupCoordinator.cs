using Windows.Graphics.Capture;

namespace SonicDesktopRelay.Media.Windows;

internal static class BorderlessCaptureStartupCoordinator
{
    internal static async Task<BorderlessCaptureResult> ConfigureAndStartAsync(
        IBorderlessCapturePolicy policy,
        GraphicsCaptureSession session,
        Action startCapture,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(startCapture);

        var result = await policy.TryEnableAsync(session, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        startCapture();
        return result;
    }
}
