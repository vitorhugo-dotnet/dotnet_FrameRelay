using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace SonicDesktopRelay.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class ShareView : UserControl
{
    public ShareView()
    {
        InitializeComponent();
    }

    private async void OnShare(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Shell shell) await shell.ShareAsync(CancellationToken.None);
    }

    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Shell shell) await shell.StopAsync(CancellationToken.None);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Shell shell || shell.ViewModel.Code is not { } code) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(code);
    }
}
