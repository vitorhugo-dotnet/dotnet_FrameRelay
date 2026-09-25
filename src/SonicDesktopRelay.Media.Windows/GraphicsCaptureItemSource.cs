using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// Owns the shared Windows.Graphics.Capture/D3D11 frame lifecycle for any supported capture item.
/// <para>
/// The frame buffer handed to <see cref="FrameCaptured"/> is reused between frames: at 1080p30
/// a fresh array per frame is a quarter of a gigabyte of garbage a second. Subscribers must
/// consume it before returning — the pipeline encodes synchronously, which is why this is safe.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class GraphicsCaptureItemSource : IScreenCaptureSource, IScreenCaptureDiagnostics
{
    private const int PoolDepth = 2;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _runtimeDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private byte[]? _buffer;
    private SizeInt32 _poolSize;
    private TimeSpan _firstFrameTime = TimeSpan.MinValue;
    private TimeSpan _lastDelivered = TimeSpan.MinValue;
    private TimeSpan _minimumInterval = TimeSpan.Zero;
    private bool _running;
    private bool _disposed;
    private long _framesArrived;
    private long _framesDelivered;
    private long _framesDropped;
    private readonly IGraphicsCaptureItemFactory _itemFactory;
    private readonly IBorderlessCapturePolicy _borderlessPolicy;
    private readonly ILogger _logger;
    private CaptureTarget? _target;

    internal GraphicsCaptureItemSource() : this(
        new GraphicsCaptureItemFactory(),
        new BorderlessCapturePolicy(new WinRtBorderlessCapturePlatform()),
        NullLogger.Instance) { }

    internal GraphicsCaptureItemSource(ILogger logger) : this(
        new GraphicsCaptureItemFactory(),
        new BorderlessCapturePolicy(new WinRtBorderlessCapturePlatform()),
        logger) { }

    internal GraphicsCaptureItemSource(IBorderlessCapturePolicy borderlessPolicy, ILogger? logger = null) : this(
        new GraphicsCaptureItemFactory(), borderlessPolicy, logger ?? NullLogger.Instance) { }

    internal GraphicsCaptureItemSource(IGraphicsCaptureItemFactory itemFactory) : this(
        itemFactory,
        new BorderlessCapturePolicy(new WinRtBorderlessCapturePlatform()),
        NullLogger.Instance) { }

    internal GraphicsCaptureItemSource(
        IGraphicsCaptureItemFactory itemFactory,
        IBorderlessCapturePolicy borderlessPolicy,
        ILogger logger)
    {
        _itemFactory = itemFactory ?? throw new ArgumentNullException(nameof(itemFactory));
        _borderlessPolicy = borderlessPolicy ?? throw new ArgumentNullException(nameof(borderlessPolicy));
        _logger = logger ?? NullLogger.Instance;
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

    public MonitorInfo Monitor => _target switch
    {
        CaptureTarget.Monitor monitor => monitor.Info with { Width = CurrentDimensions.Width, Height = CurrentDimensions.Height },
        CaptureTarget.Window window => new MonitorInfo($"HWND:{window.Info.Handle:X}", window.Info.Title,
            CurrentDimensions.Width, CurrentDimensions.Height, false),
        _ => new MonitorInfo(string.Empty, string.Empty, 0, 0, false)
    };

    public CaptureTarget Target => _target ?? throw new InvalidOperationException("Capture has not started.");

    public (int Width, int Height) CurrentDimensions => _poolSize.Width > 0 && _poolSize.Height > 0
        ? (_poolSize.Width, _poolSize.Height)
        : _target is CaptureTarget.Monitor m ? (m.Info.Width, m.Info.Height)
        : _target is CaptureTarget.Window w ? (w.Info.Width, w.Info.Height) : (0, 0);

    public event Action<VideoFrame>? FrameCaptured;

    public event Action<int, int>? DimensionsChanged;

    public event Action<string>? TargetClosed;

    public long FramesArrived => Interlocked.Read(ref _framesArrived);

    public long FramesDelivered => Interlocked.Read(ref _framesDelivered);

    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct) =>
        StartAsync(new CaptureTarget.Monitor(monitor), quality, ct);

    public async Task StartAsync(CaptureTarget target, VideoQuality quality, CancellationToken ct)
    {
        // Preserve the caller context while the system may display the access prompt.
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            lock (_gate)
            {
                if (_running) return;

                if (!IsSupported)
                    throw new PlatformNotSupportedException(
                        "Windows.Graphics.Capture is not available in this session.");

                ct.ThrowIfCancellationRequested();

                _item = target switch
                {
                    CaptureTarget.Monitor monitor => _itemFactory.CreateForMonitor(monitor.Info),
                    CaptureTarget.Window window => _itemFactory.CreateForWindow(window.Info),
                    _ => throw new ArgumentOutOfRangeException(nameof(target))
                };
                _target = target;
                CreateDevice();
                _poolSize = _item.Size;
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _runtimeDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolDepth, _poolSize);
                _session = _pool.CreateCaptureSession(_item);

                // A screen share without the pointer is markedly harder to follow.
                _session.IsCursorCaptureEnabled = true;

                _item.Closed += OnItemClosed;
                _pool.FrameArrived += OnFrameArrived;
            }

            var result = await BorderlessCaptureStartupCoordinator.ConfigureAndStartAsync(
                _borderlessPolicy,
                _session!,
                () =>
                {
                    lock (_gate)
                    {
                        ct.ThrowIfCancellationRequested();
                        _firstFrameTime = TimeSpan.MinValue;
                        _lastDelivered = TimeSpan.MinValue;
                        _minimumInterval = quality.FramesPerSecond > 0
                            ? TimeSpan.FromSeconds(1.0 / quality.FramesPerSecond)
                            : TimeSpan.Zero;
                        _running = true;
                        _session!.StartCapture();
                    }
                },
                ct).ConfigureAwait(false);
            BorderlessCaptureDiagnostics.Log(_logger, target is CaptureTarget.Window ? "window" : "monitor", result);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void SetFrameRate(int framesPerSecond)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (framesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));

        lock (_gate)
        {
            _minimumInterval = TimeSpan.FromSeconds(1.0 / framesPerSecond);
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private Task StopCoreAsync()
    {
        GraphicsCaptureItem? item;
        Direct3D11CaptureFramePool? pool;
        GraphicsCaptureSession? session;
        ID3D11Texture2D? staging;
        ID3D11DeviceContext? context;
        ID3D11Device? device;
        IDirect3DDevice? runtimeDevice;

        lock (_gate)
        {
            if (!_running && _item is null && _pool is null && _session is null && _staging is null
                && _context is null && _device is null && _runtimeDevice is null)
                return Task.CompletedTask;

            // Cleared inside the lock so that any callback already in flight finishes, and any
            // callback that arrives next sees a stopped source and returns before touching
            // anything. The native objects are then torn down outside the lock: closing them
            // can wait on the capture callback, which would deadlock against a held lock.
            _running = false;
            item = _item;
            pool = _pool;
            session = _session;
            staging = _staging;
            context = _context;
            device = _device;
            runtimeDevice = _runtimeDevice;
            _item = null;
            _pool = null;
            _session = null;
            _staging = null;
            _context = null;
            _device = null;
            _runtimeDevice = null;
            _buffer = null;
        }

        if (item is not null) item.Closed -= OnItemClosed;
        if (pool is not null) pool.FrameArrived -= OnFrameArrived;
        session?.Dispose();
        pool?.Dispose();
        staging?.Dispose();
        context?.Dispose();
        device?.Dispose();
        (runtimeDevice as IDisposable)?.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }

    private void CreateDevice()
    {
        // BgraSupport is required: the capture pool hands out B8G8R8A8 surfaces.
        var result = D3D11.D3D11CreateDevice(
            nint.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null!,
            out var device, out var context);

        if (result.Failure)
        {
            // A machine with no usable GPU (or a stripped-down VM) still has to be able to
            // share a screen; WARP is slow but correct.
            D3D11.D3D11CreateDevice(
                nint.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, null!,
                out device, out context).CheckError();
        }

        _device = device;
        _context = context;
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        _runtimeDevice = CaptureInterop.CreateDirect3DDevice(dxgiDevice.NativePointer);
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        var target = _target;
        _ = StopAsync();
        if (target is not null) TargetClosed?.Invoke(target is CaptureTarget.Window window
            ? $"Window '{window.Info.Title}' was closed."
            : "The selected monitor capture was closed.");
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        VideoFrame? frame = null;

        lock (_gate)
        {
            if (!_running || _context is null || _device is null) return;
            Interlocked.Increment(ref _framesArrived);

            try
            {
                frame = TryBuildFrame(sender);
            }
            catch (Exception e) when (e is SharpGen.Runtime.SharpGenException
                                          or System.Runtime.InteropServices.COMException
                                          or ObjectDisposedException)
            {
                // A device loss or a surface that vanished under us is a dropped frame, not a
                // dead session: the next FrameArrived recreates whatever went away.
                Interlocked.Increment(ref _framesDropped);
                return;
            }
        }

        if (frame is not null)
        {
            Interlocked.Increment(ref _framesDelivered);
            FrameCaptured?.Invoke(frame);
        }
        else
        {
            Interlocked.Increment(ref _framesDropped);
        }
    }

    private VideoFrame? TryBuildFrame(Direct3D11CaptureFramePool pool)
    {
        using var captured = pool.TryGetNextFrame();
        if (captured is null) return null;

        // A resolution change mid-session must not end the session: resize the pool and drop
        // this frame, which was produced against the old size.
        var contentSize = captured.ContentSize;
        if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
        {
            _poolSize = contentSize;
            pool.Recreate(_runtimeDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolDepth, _poolSize);
            DimensionsChanged?.Invoke(contentSize.Width, contentSize.Height);
            return null;
        }

        var timestamp = captured.SystemRelativeTime;
        if (_firstFrameTime == TimeSpan.MinValue) _firstFrameTime = timestamp;

        if (_lastDelivered != TimeSpan.MinValue
            && timestamp - _lastDelivered < _minimumInterval)
        {
            // WGC delivers on the compositor's cadence, which can be well above the session's
            // frame rate. Dropping here is far cheaper than encoding and discarding later.
            return null;
        }

        using var texture = CaptureInterop.GetTexture(captured.Surface);
        var description = texture.Description;
        var width = (int)description.Width;
        var height = (int)description.Height;
        if (width <= 0 || height <= 0) return null;

        EnsureStaging(width, height);
        _context!.CopyResource(_staging!, texture);

        var stride = width * 4;
        var buffer = _buffer ??= new byte[stride * height];
        if (buffer.Length < stride * height) buffer = _buffer = new byte[stride * height];

        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            CopyRows(map, buffer, stride, height);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        _lastDelivered = timestamp;
        return new VideoFrame(width, height, buffer.AsMemory(0, stride * height), timestamp - _firstFrameTime);
    }

    private static unsafe void CopyRows(MappedSubresource map, byte[] destination, int stride, int height)
    {
        var source = (byte*)map.DataPointer;
        fixed (byte* target = destination)
        {
            // The mapped row pitch is not width * 4 — D3D pads rows. Copying the block whole
            // produces a sheared image, so every row is copied at its own offset.
            if (map.RowPitch == (uint)stride)
            {
                Buffer.MemoryCopy(source, target, destination.Length, (long)stride * height);
                return;
            }

            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(source + (long)y * map.RowPitch, target + (long)y * stride, stride, stride);
        }
    }

    private void EnsureStaging(int width, int height)
    {
        var existing = _staging?.Description;
        if (existing is { } description
            && description.Width == (uint)width
            && description.Height == (uint)height)
        {
            return;
        }

        _staging?.Dispose();
        var staging = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };
        _staging = _device!.CreateTexture2D(in staging);
        _buffer = null;
    }
}
