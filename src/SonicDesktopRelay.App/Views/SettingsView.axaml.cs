using Avalonia.Controls;
using Avalonia.Interactivity;
using SonicDesktopRelay.App;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace SonicDesktopRelay.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Shell shell) await shell.CheckForUpdatesAsync();
    }

    private void OnOpenUpdate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Shell { UpdateDownloadUrl: { } url }
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
