using System.ComponentModel;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SonicDesktopRelay.Core;
using SonicDesktopRelay.Presentation;
using AppPage = SonicDesktopRelay.Presentation.Page;

namespace SonicDesktopRelay.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class MainWindow : Window
{
    private WindowState _restoreState = WindowState.Normal;
    private readonly Action<LaunchActivation> _activationHandler;
    private readonly ILogger _logger;

    public MainWindow()
    {
        InitializeComponent();
        var shell = new Shell();
        DataContext = shell;
        shell.PropertyChanged += OnShellPropertyChanged;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        _logger = FrameRelayLogging.Current?.LoggerFactory.CreateLogger("FrameRelay.LaunchActivation")
                  ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _activationHandler = OnLaunchActivation;
        LaunchActivationRouter.Register(_activationHandler);
        Closed += async (_, _) =>
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
            LaunchActivationRouter.Unregister(_activationHandler);
            await shell.DisposeAsync();
        };
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Shell.IsVideoFullScreen) || sender is not Shell shell) return;

        if (shell.IsVideoFullScreen)
        {
            _restoreState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (WindowState == WindowState.FullScreen)
        {
            WindowState = _restoreState;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not Shell shell) return;

        if (e.Key == Key.Escape && shell.IsVideoExpanded)
        {
            shell.ExitVideoFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && shell.ViewModel.CurrentPage == AppPage.Watch
                 && (shell.IsVideoFullScreen || shell.ViewModel.Snapshot.Phase == SessionPhase.Watching))
        {
            shell.ToggleVideoFullScreen();
            e.Handled = true;
        }
    }

    private void OnLaunchActivation(LaunchActivation activation) =>
        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            try
            {
                if (DataContext is Shell shell)
                    await shell.ActivateLaunchAsync(activation, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogError("Could not process FrameRelay launch activation. kind={Kind} type={ExceptionType}",
                    activation.Kind, exception.GetType().Name);
            }
        });

    private void OnNavigate(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }
            && Enum.TryParse<AppPage>(tag, out var page)
            && DataContext is Shell shell)
        {
            shell.ViewModel.CurrentPage = page;
        }
    }
}
