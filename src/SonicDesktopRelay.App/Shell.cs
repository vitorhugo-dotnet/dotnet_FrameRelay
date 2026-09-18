using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using SonicDesktopRelay.Core;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using SonicDesktopRelay.Presentation;
using SonicDesktopRelay.Rtc;

namespace SonicDesktopRelay.App;

/// <summary>
/// What the window binds to: the plan's <see cref="MainWindowViewModel"/> for everything the
/// UI may know about a session, plus the few things only the shell owns — the configured
/// backend, the device name, and the actions the buttons invoke. The composition root is
/// built lazily because the backend address can be wrong until someone fixes it in Settings.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class Shell : INotifyPropertyChanged
{
    private const int DefaultMaxViewers = 3;

    private readonly FileBackendAddressStore _backendAddressStore;
    private AppComposition? _composition;
    private string _backendAddress;
    private string _deviceName = Environment.MachineName;
    private string? _shellError;
    private MonitorInfo? _selectedMonitor;
    private bool _isVideoFullScreen;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// One decoded frame, already on the UI thread. The watch view owns the surface; the
    /// shell only carries the frame across the thread boundary, exactly as it does snapshots.
    /// </summary>
    public event Action<VideoFrame>? FrameDecoded;

    public MainWindowViewModel ViewModel { get; } = new();

    public Shell()
    {
        _backendAddressStore = new FileBackendAddressStore(FileBackendAddressStore.DefaultPath);
        _backendAddress = _backendAddressStore.Read();
        RefreshMonitors();
    }

    /// <summary>The Diagnostics page's session-runtime snapshot history.</summary>
    public ObservableCollection<string> Diagnostics { get; } = [];

    /// <summary>The Diagnostics page's bounded signaling metadata history.</summary>
    public ObservableCollection<string> SignalingDiagnostics { get; } = [];

    /// <summary>The Diagnostics page's metadata-only viewer WebRTC negotiation history.</summary>
    public ObservableCollection<string> WebRtcDiagnostics { get; } = [];

    public string BackendAddress
    {
        get => _backendAddress;
        set
        {
            if (_backendAddress == value) return;
            _backendAddress = value;
            if (BackendSettings.TryParse(value) is not null)
            {
                try
                {
                    _backendAddressStore.Write(value);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    ShellError = $"Could not save backend address: {e.Message}";
                }
            }

            // A changed address invalidates the clients built against the old one.
            _composition = null;
            Raise();
            Raise(nameof(IsBackendAddressValid));
        }
    }

    public bool IsBackendAddressValid => BackendSettings.TryParse(_backendAddress) is not null;

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (_deviceName == value) return;
            _deviceName = value;
            // Same reason as the backend address: the composition captured the old value.
            // It only reaches the backend on a first-ever bootstrap, but a composition built
            // with a stale name would send the stale one if registration happens later.
            _composition = null;
            Raise();
        }
    }

    /// <summary>A failure the runtime never saw, such as an unreachable backend.</summary>
    public string? ShellError
    {
        get => _shellError;
        private set
        {
            if (_shellError == value) return;
            _shellError = value;
            Raise();
        }
    }

    /// <summary>
    /// True while the picture fills the window and the navigation rail is out of the way.
    /// F11 toggles it, Esc leaves it.
    /// </summary>
    public bool IsVideoFullScreen
    {
        get => _isVideoFullScreen;
        set
        {
            if (_isVideoFullScreen == value) return;
            _isVideoFullScreen = value;
            Raise();
        }
    }

    /// <summary>The monitors this machine can share, newest enumeration each time it is read.</summary>
    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    public MonitorInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (Nullable.Equals(_selectedMonitor, value)) return;
            _selectedMonitor = value;
            Raise();
        }
    }

    /// <summary>What the media stack is actually doing, for the Diagnostics page.</summary>
    public string MediaStatusText
    {
        get
        {
            var snapshot = ViewModel.Snapshot;
            return snapshot.Phase == SessionPhase.Watching || _composition?.WatchHost.DecoderName is not null
                ? WatchStatusText(snapshot)
                : PublishStatusText(snapshot);
        }
    }

    private string PublishStatusText(SessionSnapshot snapshot)
    {
        var host = _composition?.PublishHost;
        if (host?.StartFailure is { } failure) return $"Media failed to start: {failure}";
        if (host?.EncoderName is not { } encoder) return "Video: not started";

        var video = host.VideoDiagnostics;
        var transform = video is null
            ? encoder
            : $"{video.TransformName} (clsid={video.TransformClsid:B}, {video.Acceleration}, " +
              $"{video.InputFormat}->{video.OutputFormat}, {video.Width}x{video.Height}@" +
              $"{video.FramesPerSecond}, {video.Bitrate} bps)";

        var audio = host.AudioDegradedReason is { } audioFailure
            ? $"audio degraded: {audioFailure}"
            : $"WASAPI loopback [{host.AudioCaptureEndpoint ?? "default render endpoint"}] -> " +
              $"{host.AudioEncoderName ?? "Opus"} 48 kHz stereo";

        var rejected = host.EncoderRejections.Count == 0
            ? "no rejected MFTs"
            : $"rejected MFTs: {string.Join("; ", host.EncoderRejections)}";

        return $"Video: Windows.Graphics.Capture -> Media Foundation H.264 [{transform}] | " +
               $"Audio: {audio} | viewers={snapshot.ViewerCount} | {rejected}";
    }

    private string WatchStatusText(SessionSnapshot snapshot)
    {
        var host = _composition?.WatchHost;
        if (host?.StartFailure is { } failure) return $"Media failed to start: {failure}";
        if (host?.DecoderName is not { } decoder) return "Video: not started";

        var video = host.VideoDiagnostics;
        var transform = video is null
            ? decoder
            : $"{video.TransformName} (clsid={video.TransformClsid:B}, {video.Acceleration}, " +
              $"{video.InputFormat}->{video.OutputFormat}, " +
              $"{(video.Width > 0 && video.Height > 0 ? $"{video.Width}x{video.Height}" : "geometry pending")})";

        var audio = host.AudioDegradedReason is { } audioFailure
            ? $"audio degraded: {audioFailure}"
            : $"Opus 48 kHz stereo -> {host.AudioSinkName ?? "WASAPI default render endpoint"}";

        var rejected = host.DecoderRejections.Count == 0
            ? "no rejected MFTs"
            : $"rejected MFTs: {string.Join("; ", host.DecoderRejections)}";

        return $"Video: Media Foundation H.264 [{transform}] | Audio: {audio} | " +
               $"watch={snapshot.Watching?.ToString() ?? "not watching"} | {rejected}";
    }

    /// <summary>Refreshes <see cref="Monitors"/> from the OS and keeps a sensible selection.</summary>
    public void RefreshMonitors()
    {
        var monitors = new MonitorEnumerator().List();
        Monitors.Clear();
        foreach (var monitor in monitors) Monitors.Add(monitor);

        if (SelectedMonitor is { } selected && monitors.Any(x => x.Id == selected.Id)) return;
        SelectedMonitor = monitors.FirstOrDefault(x => x.IsPrimary, monitors.FirstOrDefault());
    }

    public async Task ShareAsync(CancellationToken ct)
    {
        var runtime = TryGetRuntime();
        if (runtime is null) return;

        if (SelectedMonitor is not { } monitor)
        {
            ShellError = "No monitor is available to share.";
            return;
        }

        await GuardAsync(() => runtime.StartSharingAsync(monitor, DefaultMaxViewers, ct));
    }

    public async Task WatchAsync(string code, CancellationToken ct)
    {
        var runtime = TryGetRuntime();
        if (runtime is null) return;
        await GuardAsync(() => runtime.StartWatchingAsync(code, ct));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        var runtime = _composition?.Runtime;
        if (runtime is null) return;
        await GuardAsync(() => runtime.StopAsync(ct));
    }

    private SessionRuntime? TryGetRuntime()
    {
        var settings = BackendSettings.TryParse(_backendAddress);
        if (settings is null)
        {
            ShellError = "Set a valid backend address in Settings first.";
            return null;
        }

        if (_composition is null)
        {
            _composition = new AppComposition(settings, _deviceName);
            _composition.Runtime.Changed += OnSnapshot;
            _composition.Runtime.SignalingDiagnosticAdded += OnSignalingDiagnostic;
            _composition.WatchHost.WebRtcDiagnosticAdded += OnWebRtcDiagnostic;
            _composition.WatchHost.FrameDecoded += PublishFrame;
            ViewModel.Apply(_composition.Runtime.Snapshot);
        }

        return _composition.Runtime;
    }

    private async Task GuardAsync(Func<Task> action)
    {
        ShellError = null;
        try
        {
            await action();
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // The runtime already reports refusals the backend explained. What is left is the
            // backend not answering at all, which no session snapshot can describe.
            ShellError = e.Message;
        }
    }

    /// <summary>
    /// Called from the decode thread. Rendering is the UI thread's job and decoding must not
    /// be, so this is the single hand-off point between them.
    /// <para>
    /// Blocking (<c>Invoke</c>, not <c>Post</c>) on purpose. The frame's pixels are the
    /// decoder's own reused buffer — the whole point of not allocating one per frame — so
    /// posting would let the decode thread scribble the next frame over it before the UI
    /// thread had blitted this one, and the picture would tear under exactly the load that
    /// makes it hardest to diagnose. Waiting here costs one memcpy of decode throughput and
    /// applies backpressure to the receive side, which is the right thing to give up.
    /// </para>
    /// </summary>
    internal void PublishFrame(VideoFrame frame)
    {
        if (FrameDecoded is null) return;

        try
        {
            Dispatcher.UIThread.Invoke(() => FrameDecoded?.Invoke(frame));
        }
        catch (Exception e) when (e is InvalidOperationException or TaskCanceledException
                                      or OperationCanceledException)
        {
            // The dispatcher is shutting down: the window is closing and there is nothing left
            // to draw on. A frame in flight at that moment is not a failure.
        }
    }

    // Snapshots arrive on whatever thread the signaling receive loop is running on; bindings
    // and the observable collection are the UI thread's alone.
    private void OnSnapshot(SessionSnapshot snapshot) => Dispatcher.UIThread.Post(() =>
    {
        ViewModel.Apply(snapshot);
        Raise(nameof(MediaStatusText));
        Diagnostics.Insert(0,
            $"{DateTimeOffset.Now:HH:mm:ss}  {snapshot.Phase}  signaling={snapshot.Signaling}  " +
            $"session={snapshot.SessionId?.ToString() ?? "-"}  viewers={snapshot.ViewerCount}");
    });

    private void OnSignalingDiagnostic(SignalingDiagnosticEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        var handled = entry.Handled is { } value ? value.ToString().ToLowerInvariant() : "-";
        SignalingDiagnostics.Insert(0,
            $"{entry.Timestamp:HH:mm:ss.fff} {entry.Direction} {entry.Type} " +
            $"phase={entry.Phase} signaling={entry.Signaling} " +
            $"from={entry.From?.ToString() ?? "-"} to={entry.To?.ToString() ?? "-"} handled={handled}");

        if (SignalingDiagnostics.Count > SignalingDiagnosticBuffer.DefaultCapacity)
            SignalingDiagnostics.RemoveAt(SignalingDiagnostics.Count - 1);
    });

    private void OnWebRtcDiagnostic(ViewerNegotiationDiagnosticEntry entry)
    {
        // Capture phase before crossing to the dispatcher: the session snapshot may advance
        // before the posted UI action runs.
        var phase = _composition?.Runtime.Snapshot.Phase ?? ViewModel.Snapshot.Phase;

        Dispatcher.UIThread.Post(() =>
        {
            var result = entry.SetDescriptionResult is null ? "" : $" result={entry.SetDescriptionResult}";
            var error = entry.ExceptionType is null ? "" : $" exception={entry.ExceptionType}";
            var message = entry.Message is null ? "" : $" message={entry.Message}";

            WebRtcDiagnostics.Insert(0,
                $"{entry.Timestamp:HH:mm:ss.fff} {entry.Event} phase={phase} " +
                $"signaling={entry.SignalingState} iceGathering={entry.IceGatheringState} " +
                $"iceConnection={entry.IceConnectionState} connection={entry.ConnectionState} " +
                $"from={entry.From?.ToString() ?? "-"} to={entry.To?.ToString() ?? "-"}" +
                $"{result}{error}{message}");

            if (WebRtcDiagnostics.Count > SignalingDiagnosticBuffer.DefaultCapacity)
                WebRtcDiagnostics.RemoveAt(WebRtcDiagnostics.Count - 1);
        });
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
