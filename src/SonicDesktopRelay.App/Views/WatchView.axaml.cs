using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class WatchView : UserControl
{
    private const int CodeLength = 6;

    private Shell? _shell;

    public WatchView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is not Shell shell) return;
        _shell = shell;
        shell.FrameDecoded += OnFrame;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_shell is null) return;
        _shell.FrameDecoded -= OnFrame;
        _shell = null;
    }

    // Frames are already marshalled onto the UI thread by the shell; the surface only blits.
    private void OnFrame(VideoFrame frame) => Surface.Present(frame);

    /// <summary>
    /// Codes are issued uppercase and matched uppercase, so the box shows what will actually
    /// be sent rather than letting the user believe their lowercase entry is something else.
    /// </summary>
    private void OnCodeChanged(object? sender, TextChangedEventArgs e)
    {
        var text = CodeBox.Text ?? string.Empty;
        var cleaned = new string([.. text.Where(char.IsLetterOrDigit)]).ToUpperInvariant();
        if (cleaned != text)
        {
            var caret = CodeBox.CaretIndex;
            CodeBox.Text = cleaned;
            CodeBox.CaretIndex = Math.Min(caret, cleaned.Length);
            return;
        }

        WatchButton.IsEnabled = cleaned.Length == CodeLength
            && DataContext is Shell { ViewModel.CanWatch: true };
    }

    private async void OnWatch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Shell shell) return;
        var code = CodeBox.Text ?? string.Empty;
        if (code.Length != CodeLength) return;
        await shell.WatchAsync(code, CancellationToken.None);
    }

    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Shell shell) return;
        Surface.Clear();
        await shell.StopAsync(CancellationToken.None);
    }

    private void OnEnterFullScreen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Shell shell) shell.EnterVideoFullScreen();
    }

    private void OnExitFullScreen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Shell shell) shell.ExitVideoFullScreen();
    }

    private void OnTogglePlaybackMute(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Shell shell) shell.TogglePlaybackMute();
    }
}
