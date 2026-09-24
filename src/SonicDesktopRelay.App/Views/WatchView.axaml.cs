using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class WatchView : UserControl
{
    private const int CodeLength = 6;

    private Shell? _shell;
    private Window? _window;
    private WindowState _restoreState = WindowState.Normal;

    public WatchView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.KeyDown += OnWindowKeyDown;

        if (DataContext is not Shell shell) return;
        _shell = shell;
        shell.FrameDecoded += OnFrame;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_window is not null) _window.KeyDown -= OnWindowKeyDown;
        _window = null;

        if (_shell is null) return;
        _shell.FrameDecoded -= OnFrame;
        _shell = null;
    }

    // Frames are already marshalled onto the UI thread by the shell; the surface only blits.
    private void OnFrame(VideoFrame frame) => Surface.Present(frame);

    /// <summary>
    /// F11 in, Esc out. Handled on the window rather than the control because a video surface
    /// is not focusable and nobody expects to have to click the picture first.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || _shell is null || _window is null) return;

        switch (e.Key)
        {
            case Key.F11:
                SetFullScreen(!_shell.IsVideoFullScreen);
                e.Handled = true;
                break;

            case Key.Escape when _shell.IsVideoFullScreen:
                SetFullScreen(false);
                e.Handled = true;
                break;
        }
    }

    private void SetFullScreen(bool fullScreen)
    {
        if (_shell is null || _window is null) return;

        if (fullScreen)
        {
            _restoreState = _window.WindowState;
            _window.WindowState = WindowState.FullScreen;
        }
        else
        {
            _window.WindowState = _restoreState == WindowState.FullScreen
                ? WindowState.Normal
                : _restoreState;
        }

        _shell.IsVideoFullScreen = fullScreen;
    }

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
        SetFullScreen(false);
        Surface.Clear();
        await shell.StopAsync(CancellationToken.None);
    }

    private void OnTogglePlaybackMute(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Shell shell) shell.TogglePlaybackMute();
    }
}
