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
    private static readonly IReadOnlyDictionary<Guid, RtcTransportDiagnostics> EmptyTransportDiagnostics =
        new Dictionary<Guid, RtcTransportDiagnostics>();

    private ScreenPublishPipeline? _pipeline;
    private MediaFoundationH264Encoder? _encoder;
    private GraphicsCaptureScreenSource? _capture;
    private AudioPublishPipeline? _audioPipeline;
    private WasapiLoopbackAudioSource? _audioSource;
    private VideoPublisher? _publisher;
    private string? _audioPipelineFailure;

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

    public long CaptureFramesArrived => _capture?.FramesArrived ?? 0;

    public long CaptureFramesDelivered => _capture?.FramesDelivered ?? 0;

    public long CaptureFramesDropped => _capture?.FramesDropped ?? 0;

    public long EncodeFramesDropped => _pipeline?.DroppedEncodeFrames ?? 0;

    public DateTimeOffset? LastCapturedFrameAt => _pipeline?.LastCapturedFrameAt;

    public DateTimeOffset? LastEncodedAccessUnitAt => _pipeline?.LastEncodedAccessUnitAt;

    public string? VideoPipelineFailure => _pipeline?.LastFailure;

    public string? AudioEncoderName => _audioPipeline?.EncoderName;

    public string? AudioCaptureEndpoint => _audioSource?.ActiveEndpointName;

    public string? AudioDegradedReason => _audioPipelineFailure ?? _audioSource?.DegradedReason;

    /// <summary>Why the required video media stack could not start, when it could not.</summary>
    public string? StartFailure { get; private set; }

    /// <summary>Each video encoder candidate that was rejected, with the reason it supplied.</summary>
    public IReadOnlyList<string> EncoderRejections { get; private set; } = [];

    /// <summary>
    /// Raised for structural diagnostics changes such as selected ICE transport. Per-frame timing
    /// remains sampled/read-on-demand rather than dispatching UI work at video frame rate.
    /// </summary>
    public event Action? VideoDiagnosticsChanged;

    public async Task StartAsync(MonitorInfo monitor, VideoPublishProfile profile, CancellationToken ct)
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

            var capture = new GraphicsCaptureScreenSource();
            _capture = capture;
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

            await pipeline.StartAsync(monitor, ct);

            AudioPublishPipeline? audioPipeline = null;
            var audioSource = new WasapiLoopbackAudioSource();
            _audioSource = audioSource;

            var candidateAudioPipeline = new AudioPublishPipeline(
                audioSource,
                new OpusAudioCodec(channels: 2),
                clock);
            candidateAudioPipeline.Failed += OnAudioPipelineFailed;

            try
            {
                await candidateAudioPipeline.StartAsync(ct);
                audioPipeline = candidateAudioPipeline;
                _audioPipeline = candidateAudioPipeline;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // System audio is optional to the survival of the screen share. A missing/removed
                // endpoint or Opus failure is surfaced in Diagnostics while video keeps publishing.
                _audioPipelineFailure = audioSource.DegradedReason ?? e.Message;
                candidateAudioPipeline.Failed -= OnAudioPipelineFailed;
                await candidateAudioPipeline.DisposeAsync();
            }

            _publisher = new VideoPublisher(
                pipeline,
                new SipSorceryPeerConnectionFactory(ice),
                connection,
                audioPipeline);
            _publisher.TransportDiagnosticsChanged += OnTransportDiagnosticsChanged;

            _logger.LogInformation(
                "Publisher media stack started. encoder={EncoderName} transform={TransformName} acceleration={Acceleration} monitor={MonitorId} dimensions={Width}x{Height}",
                encoder.Name,
                encoder.TransformInfo?.Name ?? encoder.Name,
                encoder.TransformInfo?.IsHardware == true ? "hardware" : "software",
                monitor.Id,
                monitor.Width,
                monitor.Height);
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
        _capture = null;

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
