using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SonicDesktopRelay.Core;
using AppPage = SonicDesktopRelay.Presentation.Page;

namespace SonicDesktopRelay.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class MainWindow : Window
{
    private readonly Action<LaunchActivation> _activationHandler;
    private readonly ILogger _logger;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new Shell();
        _logger = FrameRelayLogging.Current?.LoggerFactory.CreateLogger("FrameRelay.LaunchActivation")
                  ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _activationHandler = OnLaunchActivation;
        LaunchActivationRouter.Register(_activationHandler);
        Closed += (_, _) => LaunchActivationRouter.Unregister(_activationHandler);
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
