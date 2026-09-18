using Avalonia;
using Microsoft.Extensions.Logging;

namespace SonicDesktopRelay.App;

[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var logging = FrameRelayLogging.InitializeDefault();
        var logger = logging.LoggerFactory.CreateLogger("FrameRelay.Program");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                logger.LogCritical(
                    exception,
                    "Unhandled process exception. terminating={IsTerminating} hresult=0x{HResult:X8}",
                    eventArgs.IsTerminating,
                    exception.HResult);
            }
            else
            {
                logger.LogCritical(
                    "Unhandled process exception object of type {ExceptionObjectType}. terminating={IsTerminating}",
                    eventArgs.ExceptionObject?.GetType().FullName ?? "unknown",
                    eventArgs.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            // Observe for diagnostics only. Do not change the runtime's configured escalation
            // behavior here; this handler is not a blanket exception-swallowing policy.
            logger.LogError(
                eventArgs.Exception,
                "Unobserved Task exception reached TaskScheduler. hresult=0x{HResult:X8}",
                eventArgs.Exception.HResult);
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                exception,
                "Application terminated by an unhandled exception. hresult=0x{HResult:X8}",
                exception.HResult);
            throw;
        }
    }

    // Avalonia configuration, also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
