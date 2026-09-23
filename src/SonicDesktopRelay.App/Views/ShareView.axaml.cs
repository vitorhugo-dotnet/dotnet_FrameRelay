using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace SonicDesktopRelay.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class ShareView : UserControl
{
    private const double SingleColumnWidth = 940;
    private bool? _isSingleColumn;

    public ShareView()
    {
        InitializeComponent();
    }

    private void OnShareSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var singleColumn = e.NewSize.Width <= SingleColumnWidth;
        if (_isSingleColumn == singleColumn) return;

        _isSingleColumn = singleColumn;
        shareColumns.ColumnDefinitions = new ColumnDefinitions(singleColumn ? "*" : "*,320");
        shareColumns.RowDefinitions = new RowDefinitions(singleColumn ? "Auto,Auto" : "Auto");
        Grid.SetColumn(sessionColumn, singleColumn ? 0 : 1);
        Grid.SetRow(sessionColumn, singleColumn ? 1 : 0);
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
