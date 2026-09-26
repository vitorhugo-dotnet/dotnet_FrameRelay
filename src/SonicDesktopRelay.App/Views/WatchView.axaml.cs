using System.Runtime.Versioning;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Presentation;

namespace SonicDesktopRelay.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class WatchView : UserControl
{
    private const int CodeLength = 6;

    private Shell? _shell;
    private Window? _window;
    private readonly ViewerDisplayState _display = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _overControls;

    public WatchView()
    {
        InitializeComponent();
        _controlsTimer.Tick += (_, _) => UpdateControls();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.KeyDown += OnWindowKeyDown;

        if (DataContext is not Shell shell) return;
        _shell = shell;
        shell.FrameDecoded += OnFrame;
        shell.PropertyChanged += OnShellChanged;
        shell.ViewModel.PropertyChanged += OnViewModelChanged;
        _controlsTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        SetDisplayMode(ViewerDisplayMode.Normal);
        _controlsTimer.Stop();
        if (_window is not null) _window.KeyDown -= OnWindowKeyDown;
        _window = null;

        if (_shell is null) return;
        _shell.FrameDecoded -= OnFrame;
        _shell.PropertyChanged -= OnShellChanged;
        _shell.ViewModel.PropertyChanged -= OnViewModelChanged;
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
                ApplyWindowState(_display.ToggleFullScreen(_window.WindowState));
                e.Handled = true;
                break;

            case Key.Escape when _shell.IsVideoExpanded:
                SetDisplayMode(ViewerDisplayMode.Normal);
                e.Handled = true;
                break;
        }
    }

    private void SetDisplayMode(ViewerDisplayMode mode)
    {
        if (_shell is null || _window is null) return;
        ApplyWindowState(_display.SetMode(mode, _window.WindowState));
    }

    private void ApplyWindowState(WindowState windowState)
    {
        if (_shell is null || _window is null) return;
        _window.WindowState = windowState;
        _shell.VideoDisplayMode = _display.Mode;
        _display.RevealControls(_clock.Elapsed);
        UpdateControls();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Shell.VideoDisplayMode) && _shell is not null
            && _shell.VideoDisplayMode != _display.Mode)
            SetDisplayMode(_shell.VideoDisplayMode);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_shell is null) return;
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage)
            && _shell.ViewModel.CurrentPage != SonicDesktopRelay.Presentation.Page.Watch)
            SetDisplayMode(ViewerDisplayMode.Normal);
        if (e.PropertyName == nameof(MainWindowViewModel.Snapshot)
            && _shell.ViewModel.Snapshot.Phase is not (SessionPhase.Watching or SessionPhase.Joining))
        {
            SetDisplayMode(ViewerDisplayMode.Normal);
            Surface.Clear();
        }
    }

    private void UpdateControls() => ViewerControls.IsVisible =
        _display.ControlsVisible(_clock.Elapsed, _overControls);

    private void OnVideoPointerMoved(object? sender, PointerEventArgs e)
    {
        _display.RevealControls(_clock.Elapsed);
        UpdateControls();
    }

    private void OnControlsEntered(object? sender, PointerEventArgs e) { _overControls = true; UpdateControls(); }
    private void OnControlsExited(object? sender, PointerEventArgs e) { _overControls = false; OnVideoPointerMoved(sender, e); }
    private void OnNormal(object? sender, RoutedEventArgs e) => SetDisplayMode(ViewerDisplayMode.Normal);
    private void OnFit(object? sender, RoutedEventArgs e) => SetDisplayMode(ViewerDisplayMode.Fit);
    private void OnFullScreen(object? sender, RoutedEventArgs e)
    {
        if (_window is not null) ApplyWindowState(_display.ToggleFullScreen(_window.WindowState));
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
        SetDisplayMode(ViewerDisplayMode.Normal);
        Surface.Clear();
        await shell.StopAsync(CancellationToken.None);
    }
}
