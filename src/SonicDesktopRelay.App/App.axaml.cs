using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace SonicDesktopRelay.App;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;
    private TrayIcon? _trayIcon;

    internal bool IsExitRequested { get; private set; }

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
            _desktopLifetime = desktop;
            _trayIcon = TrayIcon.GetIcons(this)?.FirstOrDefault();
            var minimizeToTray = new SonicDesktopRelay.Core.FileUserPreferencesStore(
                SonicDesktopRelay.Core.FileUserPreferencesStore.DefaultPath).ReadMinimizeToTray();
            var window = new Views.MainWindow();
            desktop.MainWindow = window;
            if (LaunchActivationRouter.StartupOptions.ShouldStartHidden(minimizeToTray))
                desktop.Startup += (_, _) => Dispatcher.UIThread.Post(window.Hide);
            desktop.ShutdownRequested += (_, _) => IsExitRequested = true;
            desktop.Exit += (_, _) =>
            {
                if (_trayIcon is not null) _trayIcon.IsVisible = false;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayIconClicked(object? sender, EventArgs eventArgs) => RestoreMainWindow();

    private void OnTrayOpenClicked(object? sender, EventArgs eventArgs) => RestoreMainWindow();

    private void OnTraySettingsClicked(object? sender, EventArgs eventArgs)
    {
        if (_desktopLifetime?.MainWindow is Views.MainWindow window)
            window.OpenSettingsFromTray();
    }

    private void OnTrayExitClicked(object? sender, EventArgs eventArgs)
    {
        IsExitRequested = true;
        _desktopLifetime?.Shutdown();
    }

    private void RestoreMainWindow()
    {
        if (_desktopLifetime?.MainWindow is Views.MainWindow window)
            window.RestoreFromTray();
    }
}
