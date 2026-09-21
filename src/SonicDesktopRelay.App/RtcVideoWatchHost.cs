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
/// The real media stack behind <see cref="IVideoWatchHost"/>: one H.264 decode pipeline and one
/// independent Opus/WASAPI audio pipeline carried by the same receive-only peer connection.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class RtcVideoWatchHost(
    IceApiClient iceApi,
    Func<ISignalingConnection?> signaling,
    ILoggerFactory? loggerFactory = null) : IVideoWatchHost
{
    private readonly ILogger<RtcVideoWatchHost> _logger =
        loggerFactory?.CreateLogger<RtcVideoWatchHost>() ?? NullLogger<RtcVideoWatchHost>.Instance;
    /// <summary>
    /// How often the watchdog looks. The pipeline decides what counts as a stall; this only
    /// decides how quickly it notices, and a second is well under the four it waits for.
    /// </summary>
    private static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private ScreenWatchPipeline? _pipeline;
    private MediaFoundationH264Decoder? _decoder;
    private AudioWatchPipeline? _audioPipeline;
    private WasapiAudioSink? _audioSink;
    private VideoSubscriber? _subscriber;
    private ITimer? _watchdog;
    private string? _audioPipelineFailure;

    public string? DecoderName { get; private set; }

    public NativeVideoDiagnostics? VideoDiagnostics => _decoder?.Diagnostics;

    public RtcTransportDiagnostics? TransportDiagnostics => _subscriber?.TransportDiagnostics;

    public string? VideoDecoderFailure => _pipeline?.LastFailure ?? _decoder?.LastFailure;

    public long VideoAccessUnitsReceived => _pipeline?.VideoAccessUnitsReceived ?? 0;

    public long DecodedFrames => _pipeline?.DecodedFrames ?? 0;

    public long KeyAccessUnitsReceived => _pipeline?.KeyAccessUnitsReceived ?? 0;

    public long NullDecodeResults => _pipeline?.NullDecodeResults ?? 0;

    public long KeyFrameRequests => _pipeline?.KeyFrameRequests ?? 0;

    public long MaximumAccessUnitBytes => _pipeline?.MaximumAccessUnitBytes ?? 0;

    public DateTimeOffset? LastAccessUnitAt => _pipeline?.LastAccessUnitAt;

    public DateTimeOffset? LastDecodedFrameAt => _pipeline?.LastDecodedFrameAt;

    public TimeSpan? LastDecodedFrameAge =>
        _pipeline?.LastDecodedFrameAt is { } last ? TimeProvider.System.GetUtcNow() - last : null;

    public string? AudioDecoderName => _audioPipeline?.DecoderName;

    public string? AudioSinkName => _audioPipeline?.SinkName ?? _audioSink?.Name;

    public string? AudioDegradedReason => _audioPipelineFailure ?? _audioSink?.DegradedReason;

    /// <summary>Why the required video media stack could not start, when it could not.</summary>
    public string? StartFailure { get; private set; }

    /// <summary>Each video decoder candidate that was rejected, with the reason it supplied.</summary>
    public IReadOnlyList<string> DecoderRejections { get; private set; } = [];

    public event Action<WatchState>? WatchStateChanged;

    public event Action<string>? NegotiationFailed;

    public event Action<ViewerNegotiationDiagnosticEntry>? WebRtcDiagnosticAdded;

    /// <summary>
    /// Raised by the existing one-second media watchdog so the Diagnostics page can refresh
    /// counters/failure state even while no decoded frame reaches the UI.
    /// </summary>
    public event Action? VideoDiagnosticsChanged;

    /// <summary>
    /// One decoded frame, on the decode thread. The shell marshals it to the UI thread before
    /// anything touches a bitmap.
    /// </summary>
    public event Action<VideoFrame>? FrameDecoded;

    public async Task StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_pipeline is not null) return;

            StartFailure = null;
            _audioPipelineFailure = null;

            var connection = signaling()
                             ?? throw new InvalidOperationException(
                                 "Signaling must be connected before watching starts.");

            var decoder = new MediaFoundationH264Decoder(
                loggerFactory?.CreateLogger<MediaFoundationH264Decoder>());
            _decoder = decoder;
            DecoderName = decoder.Name;
            DecoderRejections = decoder.RejectionLog;

            var pipeline = new ScreenWatchPipeline(
                decoder,
                TimeProvider.System,
                loggerFactory?.CreateLogger<ScreenWatchPipeline>());
            pipeline.FrameDecoded += OnFrame;
            pipeline.StateChanged += OnState;
            // Own the decoder pipeline before any later async setup. ICE/audio failures must
            // still unwind the native decoder and its Media Foundation runtime lease.
            _pipeline = pipeline;

            AudioWatchPipeline? audioPipeline = null;
            var sink = new WasapiAudioSink();
            _audioSink = sink;
            var candidateAudioPipeline = new AudioWatchPipeline(
                new OpusAudioCodec(channels: 2),
                sink);
            candidateAudioPipeline.Failed += OnAudioPipelineFailed;

            try
            {
                await candidateAudioPipeline.StartAsync(ct);
                if (sink.DegradedReason is not null)
                {
                    _audioPipelineFailure = sink.DegradedReason;
                    candidateAudioPipeline.Failed -= OnAudioPipelineFailed;
                    await candidateAudioPipeline.DisposeAsync();
                }
                else
                {
                    audioPipeline = candidateAudioPipeline;
                    _audioPipeline = candidateAudioPipeline;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _audioPipelineFailure = sink.DegradedReason ?? e.Message;
                candidateAudioPipeline.Failed -= OnAudioPipelineFailed;
                await candidateAudioPipeline.DisposeAsync();
            }

            var subscriber = new VideoSubscriber(
                pipeline,
                audioPipeline,
                new SipSorceryViewerPeerConnectionFactory(await LoadIceAsync(ct)),
                connection);

            // The pipeline deliberately holds no clock of its own; something outside has to
            // ask it whether the media has gone quiet.
            _watchdog = TimeProvider.System.CreateTimer(
                _ =>
                {
                    pipeline.CheckForStall();
                    VideoDiagnosticsChanged?.Invoke();
                },
                null,
                StallCheckInterval,
                StallCheckInterval);

            subscriber.NegotiationFailed += OnNegotiationFailed;
            subscriber.Diagnostic += OnWebRtcDiagnostic;
            subscriber.TransportDiagnosticsChanged += OnTransportDiagnosticsChanged;
            _subscriber = subscriber;

            _logger.LogInformation(
                "Viewer media stack started. decoder={DecoderName} transform={TransformName} acceleration={Acceleration}",
                decoder.Name,
                decoder.TransformInfo?.Name ?? decoder.Name,
                decoder.TransformInfo?.IsHardware == true ? "hardware" : "software");
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException
                                      or HttpRequestException or ApiException)
        {
            // Watching without a video decoder is not recoverable, but the session is already up:
            // record why and let Diagnostics say it out loud rather than taking the app down.
            StartFailure = e.Message;
            _logger.LogError(
                e,
                "Viewer media stack failed to start. hresult=0x{HResult:X8}",
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

    public Task HandleSignalingAsync(SignalingEnvelope envelope, CancellationToken ct) =>
        _subscriber?.HandleAsync(envelope, ct) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

    private void OnFrame(VideoFrame frame) => FrameDecoded?.Invoke(frame);

    private void OnState(WatchState state)
    {
        if (state == WatchState.Failed)
        {
            _logger.LogError(
                "Viewer media pipeline entered Failed. accessUnits={AccessUnits} decodedFrames={DecodedFrames} nullDecodes={NullDecodes} keyFrameRequests={KeyFrameRequests} lastFailure={LastFailure}",
                VideoAccessUnitsReceived,
                DecodedFrames,
                NullDecodeResults,
                KeyFrameRequests,
                VideoDecoderFailure);
        }
        else
        {
            _logger.LogInformation(
                "Viewer media pipeline state changed to {State}. accessUnits={AccessUnits} decodedFrames={DecodedFrames} nullDecodes={NullDecodes}",
                state,
                VideoAccessUnitsReceived,
                DecodedFrames,
                NullDecodeResults);
        }

        WatchStateChanged?.Invoke(state);
    }

    private void OnAudioPipelineFailed(Exception error)
        => _audioPipelineFailure ??= error.Message;

    private void OnNegotiationFailed(string failure) => NegotiationFailed?.Invoke(failure);

    private void OnWebRtcDiagnostic(ViewerNegotiationDiagnosticEntry entry) =>
        WebRtcDiagnosticAdded?.Invoke(entry);

    private void OnTransportDiagnosticsChanged(RtcTransportDiagnostics diagnostics)
    {
        _logger.LogInformation(
            "Viewer WebRTC transport selected. path={Path} protocol={Protocol} localType={LocalType} remoteType={RemoteType}",
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
        if (_watchdog is not null)
        {
            await _watchdog.DisposeAsync();
            _watchdog = null;
        }

        if (_subscriber is not null)
        {
            _subscriber.NegotiationFailed -= OnNegotiationFailed;
            _subscriber.Diagnostic -= OnWebRtcDiagnostic;
            _subscriber.TransportDiagnosticsChanged -= OnTransportDiagnosticsChanged;
            await _subscriber.DisposeAsync();
            _subscriber = null;
        }

        if (_audioPipeline is not null)
        {
            _audioPipeline.Failed -= OnAudioPipelineFailed;
            await _audioPipeline.DisposeAsync();
            _audioPipeline = null;
        }

        _audioSink = null;

        if (_pipeline is not null)
        {
            _pipeline.FrameDecoded -= OnFrame;
            _pipeline.StateChanged -= OnState;
            // Disposing the pipeline disposes the video decoder with it.
            _pipeline.Dispose();
            _pipeline = null;
            _decoder = null;
        }
    }
}