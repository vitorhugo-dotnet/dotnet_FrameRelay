using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SonicDesktopRelay.Core;
using SonicDesktopRelay.ApiClient;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using SonicDesktopRelay.Presentation;
using SonicDesktopRelay.Rtc;

namespace SonicDesktopRelay.App;

public sealed record ShareQualityOption(string Label, int MaxHeight);

public sealed record ShareFrameRateOption(string Label, int FramesPerSecond);

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
    private readonly FileUserPreferencesStore _userPreferencesStore;
    private readonly ILogger<Shell> _logger;
    private AppComposition? _composition;
    private string _backendAddress;
    private bool _ignoreDiscordAudio;
    private string _deviceName = Environment.MachineName;
    private string? _shellError;
    private MonitorInfo? _selectedMonitor;
    private ShareQualityOption? _selectedShareQuality;
    private ShareFrameRateOption? _selectedShareFrameRate;
    private ViewerDisplayMode _videoDisplayMode;
    private double _viewerVolume = 100;
    private bool _viewerMuted;
    private long _uiFramesDelivered;
    private long _lastUiFrameUtcTicks;
    private Guid? _pendingShareIntent;
    private bool _activatingLaunch;

    public string? LaunchNotice => _pendingShareIntent is not null
        ? "Discord requested a share. Choose your monitor and quality, then click Start sharing to confirm."
        : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// One decoded frame, already on the UI thread. The watch view owns the surface; the
    /// shell only carries the frame across the thread boundary, exactly as it does snapshots.
    /// </summary>
    public event Action<VideoFrame>? FrameDecoded;

    /// <summary>Raised when the local Discord audio exclusion preference changes.</summary>
    public event Action<bool>? IgnoreDiscordAudioChanged;

    public MainWindowViewModel ViewModel { get; } = new();

    public Shell()
    {
        _logger = FrameRelayLogging.Current?.LoggerFactory.CreateLogger<Shell>()
                  ?? NullLogger<Shell>.Instance;
        _backendAddressStore = new FileBackendAddressStore(FileBackendAddressStore.DefaultPath);
        _backendAddress = _backendAddressStore.Read();
        _userPreferencesStore = new FileUserPreferencesStore(FileUserPreferencesStore.DefaultPath);
        _ignoreDiscordAudio = _userPreferencesStore.ReadIgnoreDiscordAudio();
        IgnoreDiscordAudioChanged += ApplyIgnoreDiscordAudio;
        SelectedShareQuality = ShareQualities[0];
        SelectedShareFrameRate = ShareFrameRates[1];
        RefreshMonitors();
    }

    public string LogDirectory =>
        FrameRelayLogging.Current?.LogDirectory ?? "logging not initialized";

    public long UiFramesDelivered => Interlocked.Read(ref _uiFramesDelivered);

    public DateTimeOffset? LastUiFrameAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastUiFrameUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
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
            _pendingShareIntent = null;
            Raise(nameof(LaunchNotice));
            _composition = null;
            Raise();
            Raise(nameof(IsBackendAddressValid));
        }
    }

    public bool IsBackendAddressValid => BackendSettings.TryParse(_backendAddress) is not null;

    public bool IgnoreDiscordAudio
    {
        get => _ignoreDiscordAudio;
        set
        {
            if (_ignoreDiscordAudio == value) return;
            _ignoreDiscordAudio = value;
            try
            {
                _userPreferencesStore.WriteIgnoreDiscordAudio(value);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ShellError = $"Could not save audio preference: {e.Message}";
            }

            Raise();
            IgnoreDiscordAudioChanged?.Invoke(value);
        }
    }

    public bool IsDiscordAudioExclusionAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public string DiscordAudioExclusionUnavailableExplanation =>
        "Discord audio exclusion requires Windows 10 build 20348 or later.";

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
    /// The viewer layout mode; Fit leaves the native window state alone.
    /// </summary>
    public ViewerDisplayMode VideoDisplayMode
    {
        get => _videoDisplayMode;
        set
        {
            if (_videoDisplayMode == value) return;
            _videoDisplayMode = value;
            Raise();
            Raise(nameof(IsVideoFullScreen));
            Raise(nameof(IsVideoExpanded));
            Raise(nameof(WatchPagePadding));
            Raise(nameof(ViewerRailWidth));
        }
    }

    public bool IsVideoFullScreen => VideoDisplayMode == ViewerDisplayMode.FullScreen;
    public bool IsVideoExpanded => VideoDisplayMode != ViewerDisplayMode.Normal;
    public Avalonia.Thickness WatchPagePadding => IsVideoExpanded ? new(0) : new(24);
    public Avalonia.Controls.GridLength ViewerRailWidth => new(IsVideoExpanded ? 0 : 180);

    public double ViewerVolume
    {
        get => _viewerVolume;
        set
        {
            var volume = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 100;
            if (_viewerVolume == volume) return;
            _viewerVolume = volume;
            Raise();
            ApplyViewerAudio();
        }
    }

    public bool ViewerMuted
    {
        get => _viewerMuted;
        set
        {
            if (_viewerMuted == value) return;
            _viewerMuted = value;
            Raise();
            ApplyViewerAudio();
        }
    }

    private void ApplyViewerAudio() => _composition?.WatchHost.SetPlaybackVolume(ViewerVolume, ViewerMuted);

    public IReadOnlyList<ShareQualityOption> ShareQualities { get; } =
    [
        new("1080p", 1080),
        new("720p", 720),
        new("540p", 540),
        new("360p", 360)
    ];

    public IReadOnlyList<ShareFrameRateOption> ShareFrameRates { get; } =
    [
        new("15 FPS", 15),
        new("30 FPS", 30),
        new("60 FPS", 60)
    ];

    public ShareQualityOption? SelectedShareQuality
    {
        get => _selectedShareQuality;
        set
        {
            if (_selectedShareQuality == value) return;
            _selectedShareQuality = value;
            Raise();
        }
    }

    public ShareFrameRateOption? SelectedShareFrameRate
    {
        get => _selectedShareFrameRate;
        set
        {
            if (_selectedShareFrameRate == value) return;
            _selectedShareFrameRate = value;
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

        var lastCapture = host.LastCapturedFrameAt?.ToString("HH:mm:ss.fff") ?? "never";
        var lastEncoded = host.LastEncodedAccessUnitAt?.ToString("HH:mm:ss.fff") ?? "never";
        var pipelineFailure = string.IsNullOrWhiteSpace(host.VideoPipelineFailure)
            ? "none"
            : host.VideoPipelineFailure;
        var effective = host.EffectiveQuality is { } quality
            ? $"{quality.MaxHeight}p@{quality.FramesPerSecond}/{quality.TargetBitsPerSecond}bps"
            : "pending";
        var transport = host.TransportDiagnostics.Count == 0
            ? "pending"
            : string.Join(",", host.TransportDiagnostics.Values
                .Select(x => x.ToString())
                .Distinct(StringComparer.Ordinal));
        var encodeMs = host.LastEncodeDuration?.TotalMilliseconds.ToString("F2") ?? "n/a";
        var sendMs = host.LastVideoSendDuration?.TotalMilliseconds.ToString("F2") ?? "n/a";
        var recoveryMs = host.LastKeyFrameRecoveryLatency?.TotalMilliseconds.ToString("F1") ?? "n/a";

        return $"Video: Windows.Graphics.Capture -> Media Foundation H.264 [{transform}] | " +
               $"Audio: {audio} | viewers={snapshot.ViewerCount} transport={transport} effective={effective} | " +
               $"captured={host.FramesCaptured} encoded={host.EncodedAccessUnits} " +
               $"keyframes={host.KeyframesProduced} keyframeRequests={host.KeyFrameRequests} " +
               $"keyframeMode={host.KeyFrameMode} recoveryMs={recoveryMs} encodeMs={encodeMs} sendMs={sendMs} " +
               $"sendPending={host.PendingVideoSamples} sendDropped={host.DroppedVideoSamples} " +
               $"sendFailures={host.VideoSendFailures} " +
               $"viewersAwaitingKeyFrame={host.ViewersAwaitingKeyFrame} " +
               $"captureArrived={host.CaptureFramesArrived} captureDelivered={host.CaptureFramesDelivered} " +
               $"captureDropped={host.CaptureFramesDropped} encodeDropped={host.EncodeFramesDropped} " +
               $"maxAccessUnitBytes={host.MaximumAccessUnitBytes} lastCapture={lastCapture} " +
               $"lastEncoded={lastEncoded} pipelineFailure={pipelineFailure} | {rejected}";
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
        var lastAccessUnit = host.LastAccessUnitAt is { } accessUnitAt
            ? accessUnitAt.ToString("HH:mm:ss.fff")
            : "never";
        var lastFrame = host.LastDecodedFrameAt is { } decodedAt
            ? decodedAt.ToString("HH:mm:ss.fff")
            : "never";
        var frameAge = host.LastDecodedFrameAge is { } age
            ? $"{Math.Max(0, age.TotalSeconds):F1}s"
            : "n/a";
        var decoderFailure = string.IsNullOrWhiteSpace(host.VideoDecoderFailure)
            ? "none"
            : host.VideoDecoderFailure;

        var lastUiFrame = LastUiFrameAt?.ToString("HH:mm:ss.fff") ?? "never";
        var transport = host.TransportDiagnostics?.ToString() ?? "pending";

        return $"Video: Media Foundation H.264 [{transform}] | Audio: {audio} | " +
               $"watch={snapshot.Watching?.ToString() ?? "not watching"} transport={transport} | " +
               $"videoAccessUnits={host.VideoAccessUnitsReceived} keyAccessUnits={host.KeyAccessUnitsReceived} " +
               $"maxAccessUnitBytes={host.MaximumAccessUnitBytes} nullDecodes={host.NullDecodeResults} " +
               $"keyframeRequests={host.KeyFrameRequests} decodedFrames={host.DecodedFrames} " +
               $"lastAccessUnit={lastAccessUnit} lastFrame={lastFrame} age={frameAge} " +
               $"uiFrames={UiFramesDelivered} lastUiFrame={lastUiFrame} " +
               $"decoderFailure={decoderFailure} | {rejected}";
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
        if (_activatingLaunch) return;
        var runtime = TryGetRuntime();
        if (runtime is null) return;

        if (SelectedMonitor is not { } monitor)
        {
            ShellError = "No monitor is available to share.";
            return;
        }

        if (SelectedShareQuality is not { } quality || SelectedShareFrameRate is not { } frameRate)
        {
            ShellError = "Choose a video quality and frame rate before sharing.";
            return;
        }

        var profile = new VideoPublishProfile(quality.MaxHeight, frameRate.FramesPerSecond);
        var composition = _composition!;
        var launchIntent = _pendingShareIntent;
        await GuardAsync(() => runtime.StartSharingAsync(monitor, profile, DefaultMaxViewers, ct));
        if (launchIntent is { } intentId && runtime.Snapshot.Phase == SessionPhase.Sharing
            && runtime.Snapshot.SessionId is { } sessionId)
        {
            _pendingShareIntent = null;
            Raise(nameof(LaunchNotice));
            try
            {
                if (!ReferenceEquals(composition, _composition))
                    throw new InvalidOperationException("Backend changed while starting the share.");
                await composition.LaunchIntents.BindAsync(intentId, sessionId, ct);
            }
            catch (Exception error) when (error is ApiException or HttpRequestException or InvalidOperationException or OperationCanceledException)
            {
                // A consumed/expired launch must not leave an unannounced capture running.
                await GuardAsync(() => runtime.StopAsync(CancellationToken.None));
                ShellError = "Could not connect this share to Discord. The link may have expired; request a new share link.";
            }
        }
    }

    public async Task ActivateLaunchAsync(string token, CancellationToken ct)
    {
        if (_activatingLaunch || _pendingShareIntent is not null
            || ViewModel.Snapshot.Phase is not (SessionPhase.Idle or SessionPhase.Failed))
        {
            ShellError = "Stop the current session before opening a launch link.";
            return;
        }
        if (LaunchUri.ParseToken("framerelay://launch?token=" + token) is null) return;
        var runtime = TryGetRuntime();
        if (runtime is null) return;
        _activatingLaunch = true;
        ShellError = null;
        var composition = _composition!;
        try
        {
            var intent = await composition.LaunchIntents.RedeemAsync(token, ct);
            if (!ReferenceEquals(composition, _composition))
                throw new InvalidOperationException("Backend changed while opening the link.");
            if (intent.Kind == "share")
            {
                _pendingShareIntent = intent.Id;
                ViewModel.CurrentPage = Page.Share;
                Raise(nameof(LaunchNotice));
            }
            else
            {
                ViewModel.CurrentPage = Page.Watch;
                await WatchAsync(intent.WatchTarget, ct);
            }
        }
        catch (Exception error) when (error is ApiException or HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            // Never log HTTP exception details for a capability redemption.
            ShellError = "Could not open this launch link. It may be expired or already used; request a new link.";
        }
        finally { _activatingLaunch = false; }
    }

    public async Task WatchAsync(string code, CancellationToken ct)
    {
        _pendingShareIntent = null;
        Raise(nameof(LaunchNotice));
        var runtime = TryGetRuntime();
        if (runtime is null) return;
        await GuardAsync(() => runtime.StartWatchingAsync(code, ct));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _pendingShareIntent = null;
        Raise(nameof(LaunchNotice));
        VideoDisplayMode = ViewerDisplayMode.Normal;
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
            ApplyViewerAudio();
            ApplyIgnoreDiscordAudio(IgnoreDiscordAudio);
            _composition.Runtime.Changed += OnSnapshot;
            _composition.Runtime.SignalingDiagnosticAdded += OnSignalingDiagnostic;
            _composition.PublishHost.VideoDiagnosticsChanged += OnVideoDiagnosticsChanged;
            _composition.WatchHost.WebRtcDiagnosticAdded += OnWebRtcDiagnostic;
            _composition.WatchHost.VideoDiagnosticsChanged += OnVideoDiagnosticsChanged;
            _composition.WatchHost.FrameDecoded += PublishFrame;
            ViewModel.Apply(_composition.Runtime.Snapshot);
        }

        return _composition.Runtime;
    }

    private async void ApplyIgnoreDiscordAudio(bool value)
    {
        var host = _composition?.PublishHost;
        if (host is null) return;
        try { await host.SetIgnoreDiscordAudioAsync(value); }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not apply Discord audio preference.");
            ShellError = $"Could not apply audio preference: {error.Message}";
        }
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
            _logger.LogWarning(
                e,
                "Shell operation failed. phase={Phase} signaling={Signaling}",
                ViewModel.Snapshot.Phase,
                ViewModel.Snapshot.Signaling);

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

            var delivered = Interlocked.Increment(ref _uiFramesDelivered);
            Interlocked.Exchange(ref _lastUiFrameUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            if (delivered == 1 || delivered % 120 == 0)
            {
                _logger.LogTrace(
                    "Decoded frame delivered to UI. uiFrames={UiFrames} width={Width} height={Height} timestampMs={TimestampMs:F1}",
                    delivered,
                    frame.Width,
                    frame.Height,
                    frame.Timestamp.TotalMilliseconds);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or TaskCanceledException
                                      or OperationCanceledException)
        {
            _logger.LogDebug(e, "Decoded frame could not be delivered because the UI dispatcher is shutting down.");
            // The dispatcher is shutting down: the window is closing and there is nothing left
            // to draw on. A frame in flight at that moment is not a failure.
        }
    }

    // Snapshots arrive on whatever thread the signaling receive loop is running on; bindings
    // and the observable collection are the UI thread's alone.
    private void OnSnapshot(SessionSnapshot snapshot)
    {
        _logger.LogInformation(
            "Session snapshot. phase={Phase} signaling={Signaling} session={SessionId} viewers={ViewerCount} watching={Watching}",
            snapshot.Phase,
            snapshot.Signaling,
            snapshot.SessionId,
            snapshot.ViewerCount,
            snapshot.Watching);

        Dispatcher.UIThread.Post(() =>
        {
            ViewModel.Apply(snapshot);
            Raise(nameof(MediaStatusText));
            Diagnostics.Insert(0,
                $"{DateTimeOffset.Now:HH:mm:ss}  {snapshot.Phase}  signaling={snapshot.Signaling}  " +
                $"session={snapshot.SessionId?.ToString() ?? "-"}  viewers={snapshot.ViewerCount}");
        });
    }

    private void OnSignalingDiagnostic(SignalingDiagnosticEntry entry)
    {
        _logger.LogTrace(
            "Signaling envelope metadata. direction={Direction} type={Type} phase={Phase} signaling={Signaling} from={From} to={To} handled={Handled}",
            entry.Direction,
            entry.Type,
            entry.Phase,
            entry.Signaling,
            entry.From,
            entry.To,
            entry.Handled);

        Dispatcher.UIThread.Post(() =>
        {
            var handled = entry.Handled is { } value ? value.ToString().ToLowerInvariant() : "-";
            SignalingDiagnostics.Insert(0,
                $"{entry.Timestamp:HH:mm:ss.fff} {entry.Direction} {entry.Type} " +
                $"phase={entry.Phase} signaling={entry.Signaling} " +
                $"from={entry.From?.ToString() ?? "-"} to={entry.To?.ToString() ?? "-"} handled={handled}");

            if (SignalingDiagnostics.Count > SignalingDiagnosticBuffer.DefaultCapacity)
                SignalingDiagnostics.RemoveAt(SignalingDiagnostics.Count - 1);
        });
    }

    private void OnVideoDiagnosticsChanged() =>
        Dispatcher.UIThread.Post(() => Raise(nameof(MediaStatusText)));

    private void OnWebRtcDiagnostic(ViewerNegotiationDiagnosticEntry entry)
    {
        // Capture phase before crossing to the dispatcher: the session snapshot may advance
        // before the posted UI action runs.
        var phase = _composition?.Runtime.Snapshot.Phase ?? ViewModel.Snapshot.Phase;

        var logLevel = entry.ExceptionType is null && !entry.Event.EndsWith(".failed", StringComparison.Ordinal)
            ? LogLevel.Information
            : LogLevel.Warning;
        _logger.Log(
            logLevel,
            "Viewer WebRTC diagnostic. event={Event} phase={Phase} signaling={SignalingState} iceGathering={IceGatheringState} iceConnection={IceConnectionState} connection={ConnectionState} from={From} to={To} result={Result} exception={ExceptionType} message={Message}",
            entry.Event,
            phase,
            entry.SignalingState,
            entry.IceGatheringState,
            entry.IceConnectionState,
            entry.ConnectionState,
            entry.From,
            entry.To,
            entry.SetDescriptionResult,
            entry.ExceptionType,
            entry.Message);

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
