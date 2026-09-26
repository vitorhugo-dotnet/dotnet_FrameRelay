using Microsoft.Extensions.Logging;

namespace SonicDesktopRelay.Media.Windows;

internal static class BorderlessCaptureDiagnostics
{
    internal static void Log(ILogger logger, string targetKind, BorderlessCaptureResult result)
    {
        logger.LogInformation(
            "Windows Graphics Capture borderless policy evaluated. capture_target_kind={capture_target_kind} " +
            "borderless_outcome={borderless_outcome} borderless_enabled={borderless_enabled} " +
            "borderless_fallback_reason={borderless_fallback_reason}",
            targetKind,
            result.Outcome,
            result.IsEnabled,
            result.Reason);
    }
}
