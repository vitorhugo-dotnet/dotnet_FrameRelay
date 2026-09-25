using System.Runtime.Versioning;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace SonicDesktopRelay.Media.Windows;

internal sealed record BorderlessCaptureResult(bool IsEnabled, string Outcome, string? Reason);

internal interface IBorderlessCapturePlatform
{
    bool IsAvailable { get; }

    Task<AppCapabilityAccessStatus> RequestAccessAsync();

    void SetBorderRequired(GraphicsCaptureSession session, bool required);
}

internal interface IBorderlessCapturePolicy
{
    Task<BorderlessCaptureResult> TryEnableAsync(GraphicsCaptureSession session, CancellationToken ct);
}

internal sealed class BorderlessCapturePolicy(IBorderlessCapturePlatform platform) : IBorderlessCapturePolicy
{
    public async Task<BorderlessCaptureResult> TryEnableAsync(GraphicsCaptureSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ct.ThrowIfCancellationRequested();

        bool isAvailable;
        try
        {
            isAvailable = platform.IsAvailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new BorderlessCaptureResult(false, "unsupported", e.Message);
        }

        if (!isAvailable)
            return new BorderlessCaptureResult(false, "unsupported", "Borderless capture APIs are unavailable.");

        AppCapabilityAccessStatus accessStatus;
        try
        {
            accessStatus = await platform.RequestAccessAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new BorderlessCaptureResult(false, "request_failed", e.Message);
        }

        ct.ThrowIfCancellationRequested();

        if (accessStatus != AppCapabilityAccessStatus.Allowed)
            return new BorderlessCaptureResult(false, "denied", DescribeAccessStatus(accessStatus));

        try
        {
            platform.SetBorderRequired(session, required: false);
            return new BorderlessCaptureResult(true, "granted", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new BorderlessCaptureResult(false, "apply_failed", e.Message);
        }
    }

    private static string DescribeAccessStatus(AppCapabilityAccessStatus status) => status switch
    {
        AppCapabilityAccessStatus.DeniedByUser => "Borderless capture access was denied by the user.",
        AppCapabilityAccessStatus.DeniedBySystem => "Borderless capture access was denied by system policy.",
        AppCapabilityAccessStatus.NotDeclaredByApp =>
            "The package manifest does not declare graphicsCaptureWithoutBorder.",
        _ => $"Borderless capture access was not granted (status: {status})."
    };
}

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WinRtBorderlessCapturePlatform : IBorderlessCapturePlatform
{
    private const int BorderlessApiBuild = 20348;
    private const string CaptureAccessType = "Windows.Graphics.Capture.GraphicsCaptureAccess";
    private const string CaptureSessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";

    public bool IsAvailable
    {
        get
        {
            try
            {
                return OperatingSystem.IsWindowsVersionAtLeast(10, 0, BorderlessApiBuild)
                       && ApiInformation.IsTypePresent(CaptureAccessType)
                       && ApiInformation.IsMethodPresent(CaptureAccessType, "RequestAccessAsync")
                       && ApiInformation.IsPropertyPresent(CaptureSessionType, "IsBorderRequired");
            }
            catch (Exception e) when (e is TypeLoadException or DllNotFoundException
                                          or EntryPointNotFoundException or MissingMethodException)
            {
                return false;
            }
        }
    }

    public async Task<AppCapabilityAccessStatus> RequestAccessAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, BorderlessApiBuild))
            throw new PlatformNotSupportedException("Borderless capture requires Windows build 20348 or later.");

        var status = await GraphicsCaptureAccess
            .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
            .AsTask()
            .ConfigureAwait(false);
        return status;
    }

    public void SetBorderRequired(GraphicsCaptureSession session, bool required)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, BorderlessApiBuild))
            throw new PlatformNotSupportedException("Borderless capture requires Windows build 20348 or later.");

        session.IsBorderRequired = required;
    }
}
