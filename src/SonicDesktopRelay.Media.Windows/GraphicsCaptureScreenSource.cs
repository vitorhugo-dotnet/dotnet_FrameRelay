using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// Captures one monitor with Windows.Graphics.Capture and hands out BGRA frames.
/// <para>
/// The frame buffer handed to <see cref="FrameCaptured"/> is reused between frames: at 1080p30
/// a fresh array per frame is a quarter of a gigabyte of garbage a second. Subscribers must
/// consume it before returning — the pipeline encodes synchronously, which is why this is safe.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class GraphicsCaptureScreenSource : IScreenCaptureSource, IScreenCaptureDiagnostics
{
    private readonly GraphicsCaptureItemSource _source;

    public GraphicsCaptureScreenSource() : this(new GraphicsCaptureItemSource()) { }

    public GraphicsCaptureScreenSource(ILogger logger) : this(new GraphicsCaptureItemSource(logger)) { }

    internal GraphicsCaptureScreenSource(IBorderlessCapturePolicy borderlessPolicy)
        : this(new GraphicsCaptureItemSource(borderlessPolicy)) { }

    private GraphicsCaptureScreenSource(GraphicsCaptureItemSource source)
    {
        _source = source;
        _source.FrameCaptured += frame => FrameCaptured?.Invoke(frame);
        _source.TargetClosed += reason => TargetClosed?.Invoke(reason);
        _source.DimensionsChanged += (width, height) => DimensionsChanged?.Invoke(width, height);
    }

    /// <summary>
    /// False on Windows builds without the capture API and inside sessions that cannot use it
    /// (some remote and service contexts). Every caller must gate on this.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                       && GraphicsCaptureSession.IsSupported();
            }
            catch (Exception e) when (e is TypeLoadException or DllNotFoundException
                                          or EntryPointNotFoundException or MissingMethodException)
            {
                return false;
            }
        }
    }

    public MonitorInfo Monitor => _source.Monitor;

    public event Action<VideoFrame>? FrameCaptured;

    public event Action<string>? TargetClosed;

    public event Action<int, int>? DimensionsChanged;

    public long FramesArrived => _source.FramesArrived;

    public long FramesDelivered => _source.FramesDelivered;

    public long FramesDropped => _source.FramesDropped;

    public (int Width, int Height) CurrentDimensions => _source.CurrentDimensions;

    public void SetFrameRate(int framesPerSecond) => _source.SetFrameRate(framesPerSecond);

    public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct)
    {
        return _source.StartAsync(new CaptureTarget.Monitor(monitor), quality, ct);
    }

    public Task StopAsync() => _source.StopAsync();

    public async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
    }
}
