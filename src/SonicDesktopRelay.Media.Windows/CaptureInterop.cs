using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SonicDesktopRelay.Media;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// The COM edges that the Windows.Graphics.Capture projection does not cover: creating a
/// capture item for an HMONITOR, wrapping a D3D11 device as a WinRT one, and getting back to
/// the ID3D11Texture2D behind a captured surface. All three are documented interop interfaces
/// with no projected equivalent, so they are called through their vtables directly.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static unsafe partial class CaptureInterop
{
    /// <summary>IGraphicsCaptureItemInterop, the activation-factory-side interop interface.</summary>
    private static readonly Guid ItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>IGraphicsCaptureItem, the runtime class's default interface.</summary>
    private static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>IDirect3DDxgiInterfaceAccess.</summary>
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    /// <summary>ID3D11Texture2D.</summary>
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private const int MonitorInfoPrimary = 1;

    internal readonly record struct Display(nint Handle, string DeviceName, int Width, int Height, bool IsPrimary);

    /// <summary>Every connected monitor, in the order Windows enumerates them.</summary>
    internal static List<Display> ListDisplays()
    {
        var displays = new List<Display>();
        var handle = GCHandle.Alloc(displays);
        try
        {
            EnumDisplayMonitors(nint.Zero, nint.Zero, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return displays;
    }

    internal static bool TryFindMonitorHandle(string deviceId, out nint hMonitor)
    {
        foreach (var display in ListDisplays())
        {
            if (!string.Equals(display.DeviceName, deviceId, StringComparison.OrdinalIgnoreCase)) continue;
            hMonitor = display.Handle;
            return true;
        }

        hMonitor = nint.Zero;
        return false;
    }

    internal static GraphicsCaptureItem CreateItemForMonitor(nint hMonitor)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", ItemInteropIid);

        var iid = CaptureItemIid;
        nint itemPtr;
        var thisPtr = factory.ThisPtr;
        // IGraphicsCaptureItemInterop: [0..2] IUnknown, [3] CreateForWindow, [4] CreateForMonitor.
        var createForMonitor =
            (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)thisPtr)[4];
        Marshal.ThrowExceptionForHR(createForMonitor(thisPtr, hMonitor, &iid, &itemPtr));

        try
        {
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    internal static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        if (hwnd == nint.Zero)
            throw new ArgumentException("A valid window handle is required.", nameof(hwnd));

        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", ItemInteropIid);

        var iid = CaptureItemIid;
        nint itemPtr;
        var thisPtr = factory.ThisPtr;
        // IGraphicsCaptureItemInterop: [0..2] IUnknown, [3] CreateForWindow, [4] CreateForMonitor.
        var createForWindow =
            (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)thisPtr)[3];
        Marshal.ThrowExceptionForHR(createForWindow(thisPtr, hwnd, &iid, &itemPtr));

        try
        {
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    internal static IDirect3DDevice CreateDirect3DDevice(nint dxgiDevice)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var devicePtr));
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(devicePtr);
        }
        finally
        {
            Marshal.Release(devicePtr);
        }
    }

    /// <summary>The texture behind a captured surface. The caller owns the returned reference.</summary>
    internal static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var native = ((IWinRTObject)surface).NativeObject;
        Marshal.ThrowExceptionForHR(native.TryAs(DxgiInterfaceAccessIid, out nint access));
        try
        {
            var iid = Texture2DIid;
            nint texture;
            // IDirect3DDxgiInterfaceAccess: [0..2] IUnknown, [3] GetInterface.
            var getInterface =
                (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
            return new ID3D11Texture2D(texture);
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnMonitor(nint hMonitor, nint hdc, nint clip, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is not List<Display> displays) return 1;

        var info = new MonitorInfoExW { cbSize = sizeof(MonitorInfoExW) };
        if (!GetMonitorInfoW(hMonitor, ref info)) return 1;

        var deviceName = new string(info.szDevice);
        displays.Add(new Display(
            hMonitor,
            deviceName,
            info.rcMonitor.Right - info.rcMonitor.Left,
            info.rcMonitor.Bottom - info.rcMonitor.Top,
            (info.dwFlags & MonitorInfoPrimary) != 0));
        return 1;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(
        nint hdc,
        nint clip,
        delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int> callback,
        nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint hMonitor, ref MonitorInfoExW info);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoExW
    {
        public int cbSize;
        public RectL rcMonitor;
        public RectL rcWork;
        public int dwFlags;

        /// <summary>CCHDEVICENAME, e.g. <c>\\.\DISPLAY1</c>.</summary>
        public fixed char szDevice[32];
    }
}

internal interface IGraphicsCaptureItemFactory
{
    GraphicsCaptureItem CreateForMonitor(MonitorInfo monitor);

    GraphicsCaptureItem CreateForWindow(WindowInfo window);
}

internal sealed class GraphicsCaptureItemFactory : IGraphicsCaptureItemFactory
{
    private readonly Func<MonitorInfo, GraphicsCaptureItem> _createForMonitor;
    private readonly Func<WindowInfo, GraphicsCaptureItem> _createForWindow;

    public GraphicsCaptureItemFactory()
        : this(CreateMonitorItem, CreateWindowItem)
    {
    }

    internal GraphicsCaptureItemFactory(
        Func<MonitorInfo, GraphicsCaptureItem> createForMonitor,
        Func<WindowInfo, GraphicsCaptureItem> createForWindow)
    {
        _createForMonitor = createForMonitor ?? throw new ArgumentNullException(nameof(createForMonitor));
        _createForWindow = createForWindow ?? throw new ArgumentNullException(nameof(createForWindow));
    }

    public GraphicsCaptureItem CreateForMonitor(MonitorInfo monitor) => _createForMonitor(monitor);

    public GraphicsCaptureItem CreateForWindow(WindowInfo window) => _createForWindow(window);

    private static GraphicsCaptureItem CreateMonitorItem(MonitorInfo monitor)
    {
        if (!CaptureInterop.TryFindMonitorHandle(monitor.Id, out var hMonitor))
            throw new InvalidOperationException($"Monitor '{monitor.Id}' is not connected.");
        return CaptureInterop.CreateItemForMonitor(hMonitor);
    }

    private static GraphicsCaptureItem CreateWindowItem(WindowInfo window) =>
        CaptureInterop.CreateItemForWindow(window.Handle);
}
