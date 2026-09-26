using System.Runtime.Versioning;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class ShareView : UserControl
{
    private Shell? _shell;
    public ShareView()
    {
        InitializeComponent();
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is not Shell shell) return;
        _shell = shell;
        shell.PreviewFrameCaptured += OnPreviewFrame;
        shell.PropertyChanged += OnShellPropertyChanged;
        await shell.SetShareViewAttachedAsync(true);
    }

    protected override async void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        var shell = _shell;
        if (shell is null) return;
        _shell = null;
        shell.PreviewFrameCaptured -= OnPreviewFrame;
        shell.PropertyChanged -= OnShellPropertyChanged;
        PreviewSurface.Clear();
        PreviewEmptyMarker.IsVisible = true;
        await shell.SetShareViewAttachedAsync(false);
    }

    private void OnPreviewFrame(VideoFrame frame)
    {
        PreviewSurface.Present(frame);
        PreviewEmptyMarker.IsVisible = false;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Shell.SelectedCaptureTarget)
            || e.PropertyName == nameof(Shell.PreviewStatus)
            && sender is Shell { PreviewStatus: not "Live preview" })
        {
            PreviewSurface.Clear();
            PreviewEmptyMarker.IsVisible = true;
        }
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

    private void OnRefreshWindows(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Shell shell) shell.RefreshWindows();
    }
}
