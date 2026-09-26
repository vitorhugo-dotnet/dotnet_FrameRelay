using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace SonicDesktopRelay.App;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (FrameRelayLogging.Current is { } logging)
        {
            var logger = logging.LoggerFactory.CreateLogger("FrameRelay.Avalonia");
            Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
            {
                // Keep Avalonia's normal terminal behavior. We only record the exception here;
                // setting Handled=true after an arbitrary UI exception could continue with a
                // corrupted application state.
                logger.LogCritical(
                    eventArgs.Exception,
                    "Unhandled Avalonia UI-thread exception. hresult=0x{HResult:X8}",
                    eventArgs.Exception.HResult);
            };
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
