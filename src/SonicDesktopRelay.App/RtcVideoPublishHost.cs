using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SonicDesktopRelay.ApiClient;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using SonicDesktopRelay.Presentation;
using SonicDesktopRelay.Rtc;
using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.App;

/// <summary>
/// The real media stack behind <see cref="IVideoPublishHost"/>: one screen capture and H.264
/// encoder plus one system-audio capture and Opus encoder per session. The resulting encoded
/// streams are fanned out to every viewer over the same peer connection.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class RtcVideoPublishHost(
    IceApiClient iceApi,
    Func<ISignalingConnection?> signaling,
    ILoggerFactory? loggerFactory = null) : IVideoPublishHost
{
    private readonly ILogger<RtcVideoPublishHost> _logger =
        loggerFactory?.CreateLogger<RtcVideoPublishHost>() ?? NullLogger<RtcVideoPublishHost>.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PublisherCaptureSelection _captureSelection = new();
    private static readonly IReadOnlyDictionary<Guid, RtcTransportDiagnostics> EmptyTransportDiagnostics =
        new Dictionary<Guid, RtcTransportDiagnostics>();

    private ScreenPublishPipeline? _pipeline;
    private MediaFoundationH264Encoder? _encoder;
    private IScreenCaptureSource? _capture;
    private AudioPublishPipeline? _audioPipeline;
    private IAudioCaptureSource? _audioSource;
    private VideoPublisher? _publisher;
    private string? _audioPipelineFailure;
    private CaptureTarget? _activeCaptureTarget;

    public string? EncoderName { get; private set; }

    public NativeVideoDiagnostics? VideoDiagnostics => _encoder?.Diagnostics;

    public VideoQuality? EffectiveQuality => _pipeline?.Quality;

    public string KeyFrameMode => _encoder?.KeyFrameMode ?? "not-started";

    public TimeSpan? LastEncodeDuration => _pipeline?.LastEncodeDuration;

    public TimeSpan? LastKeyFrameRecoveryLatency => _pipeline?.LastKeyFrameRecoveryLatency;

    public TimeSpan? LastVideoSendDuration => _publisher?.LastVideoSendDuration;

    public IReadOnlyDictionary<Guid, RtcTransportDiagnostics> TransportDiagnostics =>
        _publisher?.TransportDiagnostics ?? EmptyTransportDiagnostics;

    public long FramesCaptured => _pipeline?.FramesCaptured ?? 0;

    public long EncodedAccessUnits => _pipeline?.EncodedAccessUnits ?? 0;

    public long KeyframesProduced => _pipeline?.KeyframesProduced ?? 0;

    public long KeyFrameRequests => _pipeline?.KeyFrameRequests ?? 0;

    public long MaximumAccessUnitBytes => _pipeline?.MaximumAccessUnitBytes ?? 0;

    public long DroppedVideoSamples => _publisher?.DroppedVideoSamples ?? 0;

    public long VideoSendFailures => _publisher?.VideoSendFailures ?? 0;

    public int PendingVideoSamples => _publisher?.PendingVideoSamples ?? 0;

    public int ViewersAwaitingKeyFrame => _publisher?.ViewersAwaitingKeyFrame ?? 0;

    public long CaptureFramesArrived => (_capture as IScreenCaptureDiagnostics)?.FramesArrived ?? 0;

    public long CaptureFramesDelivered => (_capture as IScreenCaptureDiagnostics)?.FramesDelivered ?? 0;

    public long CaptureFramesDropped => (_capture as IScreenCaptureDiagnostics)?.FramesDropped ?? 0;

    public long EncodeFramesDropped => _pipeline?.DroppedEncodeFrames ?? 0;

    public DateTimeOffset? LastCapturedFrameAt => _pipeline?.LastCapturedFrameAt;

    public DateTimeOffset? LastEncodedAccessUnitAt => _pipeline?.LastEncodedAccessUnitAt;

    public string? VideoPipelineFailure => _pipeline?.LastFailure;

    public string? AudioEncoderName => _audioPipeline?.EncoderName;

    public string? AudioCaptureEndpoint => (_audioSource as WasapiLoopbackAudioSource)?.ActiveEndpointName
                                          ?? (_audioSource as ProcessLoopbackAudioSource)?.TargetProcessName;

    public string? AudioDegradedReason => _audioPipelineFailure
        ?? (_audioSource as WasapiLoopbackAudioSource)?.DegradedReason
        ?? (_audioSource as ProcessLoopbackAudioSource)?.DegradedReason;

    /// <summary>Why the required video media stack could not start, when it could not.</summary>
    public string? StartFailure { get; private set; }

    /// <summary>Each video encoder candidate that was rejected, with the reason it supplied.</summary>
    public IReadOnlyList<string> EncoderRejections { get; private set; } = [];

    /// <summary>
    /// Raised for structural diagnostics changes such as selected ICE transport. Per-frame timing
    /// remains sampled/read-on-demand rather than dispatching UI work at video frame rate.
    /// </summary>
    public event Action? VideoDiagnosticsChanged;

    public event Action<string>? CaptureTargetClosed;

    public Task StartAsync(CaptureTarget target, VideoPublishProfile profile, CancellationToken ct) =>
        StartCoreAsync(target, profile, ct);

    public async Task StartAsync(MonitorInfo monitor, VideoPublishProfile profile, CancellationToken ct)
        => await StartCoreAsync(new CaptureTarget.Monitor(monitor), profile, ct);

    private async Task StartCoreAsync(CaptureTarget target, VideoPublishProfile profile, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_pipeline is not null) return;

            StartFailure = null;
            _audioPipelineFailure = null;

            var connection = signaling()
                             ?? throw new InvalidOperationException(
                                 "Signaling must be connected before publishing starts.");

            var ice = await LoadIceAsync(ct);

            var clock = new MediaSessionClock(TimeProvider.System);
            var encoder = new MediaFoundationH264Encoder();
            _encoder = encoder;
            EncoderName = encoder.Name;
            EncoderRejections = encoder.RejectionLog;

            var capture = _captureSelection.CreateVideo(target);
            _capture = capture;
            _activeCaptureTarget = target;
            ((IScreenCaptureSource)capture).TargetClosed += OnCaptureTargetClosed;
            capture.DimensionsChanged += OnCaptureDimensionsChanged;
            var pipeline = new ScreenPublishPipeline(
                capture,
                encoder,
                clock,
                TimeProvider.System,
                loggerFactory?.CreateLogger<ScreenPublishPipeline>(),
                profile);
            // Transfer ownership before capture startup: if the native capture path throws,
            // DisposeStackAsync can still release the capture source and Media Foundation MFT.
            _pipeline = pipeline;

            await pipeline.StartAsync(target, ct);

            AudioPublishPipeline? audioPipeline = null;
            IAudioCaptureSource? audioSource = _captureSelection.CreateAudio(target);
            _audioSource = audioSource;
            if (audioSource is null)
                _audioPipelineFailure = "Per-process audio capture requires Windows build 20348 or later.";
            else
            {
                var candidateAudioPipeline = new AudioPublishPipeline(audioSource, new OpusAudioCodec(channels: 2), clock);
                candidateAudioPipeline.Failed += OnAudioPipelineFailed;
                try
                {
                    await candidateAudioPipeline.StartAsync(ct);
                    if (audioSource is not ProcessLoopbackAudioSource processAudio || processAudio.IsAvailable)
                    {
                        audioPipeline = candidateAudioPipeline;
                        _audioPipeline = candidateAudioPipeline;
                    }
                    else
                    {
                        _audioPipelineFailure = processAudio.DegradedReason;
                        candidateAudioPipeline.Failed -= OnAudioPipelineFailed;
                        await candidateAudioPipeline.DisposeAsync();
                        _audioSource = null;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _audioPipelineFailure = (audioSource as ProcessLoopbackAudioSource)?.DegradedReason
                                            ?? (audioSource as WasapiLoopbackAudioSource)?.DegradedReason ?? e.Message;
                    candidateAudioPipeline.Failed -= OnAudioPipelineFailed;
                    await candidateAudioPipeline.DisposeAsync();
                }
            }

            _publisher = new VideoPublisher(
                pipeline,
                new SipSorceryPeerConnectionFactory(ice),
                connection,
                audioPipeline);
            _publisher.TransportDiagnosticsChanged += OnTransportDiagnosticsChanged;

            _logger.LogInformation(
                "Publisher media stack started. encoder={EncoderName} transform={TransformName} acceleration={Acceleration} capture_source_type={CaptureSourceType} target_title={TargetTitle} target_process={TargetProcess} target_pid={TargetPid} dimensions_width={Width} dimensions_height={Height}",
                encoder.Name,
                encoder.TransformInfo?.Name ?? encoder.Name,
                encoder.TransformInfo?.IsHardware == true ? "hardware" : "software",
                target is CaptureTarget.Window ? "window" : "monitor",
                target is CaptureTarget.Window targetWindow ? targetWindow.Info.Title : null,
                target is CaptureTarget.Window processWindow ? processWindow.Info.ProcessName : null,
                target is CaptureTarget.Window pidWindow ? pidWindow.Info.ProcessId : null,
                capture.CurrentDimensions.Width,
                capture.CurrentDimensions.Height);

            _logger.LogInformation(
                "Publisher audio capture selected. audio_capture_mode={AudioCaptureMode} target_process={TargetProcess} target_pid={TargetPid} process_tree={IncludesProcessTree} available={Available} activation_result={ActivationResult} degraded_reason={DegradedReason}",
                target is CaptureTarget.Window ? "process_tree" : "system_loopback",
                target is CaptureTarget.Window audioWindow ? audioWindow.Info.ProcessName : null,
                target is CaptureTarget.Window audioPidWindow ? audioPidWindow.Info.ProcessId : null,
                target is CaptureTarget.Window,
                audioSource is not null
                    && (audioSource is not ProcessLoopbackAudioSource processSource || processSource.IsAvailable),
                audioSource switch
                {
                    null => "unsupported_os",
                    ProcessLoopbackAudioSource process => process.ActivationResult,
                    _ => "started"
                },
                _audioPipelineFailure);
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException
                                      or HttpRequestException or ApiException)
        {
            // Sharing without a working capture or video encoder is not recoverable, but the session
            // itself is already up: record why and let Diagnostics say it out loud rather than
            // taking the app down.
            StartFailure = e.Message;
            _logger.LogError(
                e,
                "Publisher media stack failed to start. hresult=0x{HResult:X8}",
                e.HResult);
            await DisposeStackAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisposeStackAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task AddViewerAsync(Guid participantId, CancellationToken ct) =>
        _publisher?.AddViewerAsync(participantId, ct) ?? Task.CompletedTask;

    public Task RemoveViewerAsync(Guid participantId) =>
        _publisher?.RemoveViewerAsync(participantId) ?? Task.CompletedTask;

    public Task HandleSignalingAsync(SignalingEnvelope envelope, CancellationToken ct) =>
        _publisher?.HandleAsync(envelope, ct) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

    private void OnAudioPipelineFailed(Exception error)
        => _audioPipelineFailure ??= error.Message;

    private void OnCaptureTargetClosed(string reason)
    {
        _logger.LogWarning("Capture target closed. close_reason={CloseReason}", reason);
        CaptureTargetClosed?.Invoke(reason);
    }

    private void OnCaptureDimensionsChanged(int width, int height)
    {
        _logger.LogInformation(
            "Capture target resized. capture_source_type={CaptureSourceType} target_title={TargetTitle} dimensions_width={Width} dimensions_height={Height} resize_reason={ResizeReason}",
            _activeCaptureTarget is CaptureTarget.Window ? "window" : "monitor",
            (_activeCaptureTarget as CaptureTarget.Window)?.Info.Title,
            width,
            height,
            "content_size_changed");
    }

    private void OnTransportDiagnosticsChanged(Guid participantId, RtcTransportDiagnostics diagnostics)
    {
        _logger.LogInformation(
            "Publisher WebRTC transport selected. participant={ParticipantId} path={Path} protocol={Protocol} localType={LocalType} remoteType={RemoteType}",
            participantId,
            diagnostics.Path,
            diagnostics.Protocol,
            diagnostics.LocalCandidateType,
            diagnostics.RemoteCandidateType);
        VideoDiagnosticsChanged?.Invoke();
    }

    private async Task<IceServerSettings> LoadIceAsync(CancellationToken ct)
    {
        var response = await iceApi.GetIceServersAsync(ct);
        var servers = response.IceServers
            .SelectMany(x => x.Urls.Select(url => new IceServer(url, x.Username, x.Credential)))
            .ToList();
        return new IceServerSettings(servers, ForceRelay: false);
    }

    private async Task DisposeStackAsync()
    {
        if (_publisher is not null)
        {
            _publisher.TransportDiagnosticsChanged -= OnTransportDiagnosticsChanged;
            await _publisher.DisposeAsync();
            _publisher = null;
        }

        if (_audioPipeline is not null)
        {
            _audioPipeline.Failed -= OnAudioPipelineFailed;
            await _audioPipeline.DisposeAsync();
            _audioPipeline = null;
        }

        _audioSource = null;

        if (_capture is { } captureSource)
        {
            captureSource.TargetClosed -= OnCaptureTargetClosed;
            captureSource.DimensionsChanged -= OnCaptureDimensionsChanged;
        }
        _capture = null;
        _activeCaptureTarget = null;

        if (_pipeline is not null)
        {
            // Disposing the pipeline disposes the capture source and the encoder with it, so
            // the GPU encode session is released the moment the share stops.
            await _pipeline.DisposeAsync();
            _pipeline = null;
            _encoder = null;
        }
    }
}

internal sealed class PublisherCaptureSelection
{
    private readonly Func<IScreenCaptureSource> _monitorVideo;
    private readonly Func<IScreenCaptureSource> _windowVideo;
    private readonly Func<IAudioCaptureSource> _systemAudio;
    private readonly Func<WindowInfo, IAudioCaptureSource> _processAudio;
    private readonly Func<bool> _processLoopbackSupported;

    public PublisherCaptureSelection()
        : this(() => new GraphicsCaptureScreenSource(), () => new GraphicsCaptureWindowSource(),
            () => new WasapiLoopbackAudioSource(), window => new ProcessLoopbackAudioSource(window),
            () => ProcessLoopbackAudioSource.IsSupported) { }

    internal PublisherCaptureSelection(
        Func<IScreenCaptureSource> monitorVideo,
        Func<IScreenCaptureSource> windowVideo,
        Func<IAudioCaptureSource> systemAudio,
        Func<WindowInfo, IAudioCaptureSource> processAudio,
        Func<bool> processLoopbackSupported)
    {
        _monitorVideo = monitorVideo;
        _windowVideo = windowVideo;
        _systemAudio = systemAudio;
        _processAudio = processAudio;
        _processLoopbackSupported = processLoopbackSupported;
    }

    public IScreenCaptureSource CreateVideo(CaptureTarget target) => target switch
    {
        CaptureTarget.Monitor => _monitorVideo(),
        CaptureTarget.Window => _windowVideo(),
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    public IAudioCaptureSource? CreateAudio(CaptureTarget target) => target switch
    {
        CaptureTarget.Monitor => _systemAudio(),
        CaptureTarget.Window window when _processLoopbackSupported() => _processAudio(window.Info),
        CaptureTarget.Window => null,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };
}
