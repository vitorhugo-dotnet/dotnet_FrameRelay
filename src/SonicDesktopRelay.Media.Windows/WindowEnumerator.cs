using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SonicDesktopRelay.Media.Windows;

internal interface IWindowApi
{
    IEnumerable<nint> EnumerateTopLevelWindows();

    bool IsWindow(nint handle);

    bool IsVisible(nint handle);

    bool IsToolWindow(nint handle);

    bool IsShellWindow(nint handle);

    string GetTitle(nint handle);

    uint GetProcessId(nint handle);

    bool TryGetBounds(nint handle, out int width, out int height);

    bool TryGetProcessIdentity(uint processId, out string processName, out DateTime startTimeUtc);
}

/// <summary>Returns a snapshot of top-level windows that are reasonable share targets.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowEnumerator : IWindowEnumerator
{
    private readonly IWindowApi _windowApi;
    private readonly uint _currentProcessId;

    public WindowEnumerator()
        : this(new Win32WindowApi(), checked((uint)Environment.ProcessId))
    {
    }

    internal WindowEnumerator(IWindowApi windowApi, uint currentProcessId)
    {
        _windowApi = windowApi ?? throw new ArgumentNullException(nameof(windowApi));
        _currentProcessId = currentProcessId;
    }

    public IReadOnlyList<WindowInfo> List()
    {
        var windows = new List<WindowInfo>();

        foreach (var handle in _windowApi.EnumerateTopLevelWindows())
        {
            if (handle == nint.Zero
                || !_windowApi.IsWindow(handle)
                || !_windowApi.IsVisible(handle)
                || _windowApi.IsToolWindow(handle)
                || _windowApi.IsShellWindow(handle))
            {
                continue;
            }

            var title = _windowApi.GetTitle(handle).Trim();
            if (title.Length == 0) continue;

            var processId = _windowApi.GetProcessId(handle);
            if (processId == 0 || processId == _currentProcessId) continue;
            if (!_windowApi.TryGetProcessIdentity(processId, out var processName, out var processStartTimeUtc)
                || string.IsNullOrWhiteSpace(processName))
            {
                continue;
            }

            if (!_windowApi.TryGetBounds(handle, out var width, out var height) || width <= 0 || height <= 0)
                continue;

            // A window can close or be reused while process metadata is being read. Pair the
            // final validity/PID check with the process start time captured above.
            if (!_windowApi.IsWindow(handle) || _windowApi.GetProcessId(handle) != processId
                || !_windowApi.TryGetProcessIdentity(processId, out var finalProcessName, out var finalStartTimeUtc)
                || !string.Equals(processName, finalProcessName, StringComparison.Ordinal)
                || processStartTimeUtc != finalStartTimeUtc)
            {
                continue;
            }

            windows.Add(new WindowInfo(
                handle,
                processId,
                processStartTimeUtc,
                title,
                processName,
                width,
                height));
        }

        return windows;
    }
}

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed unsafe partial class Win32WindowApi : IWindowApi
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    public IEnumerable<nint> EnumerateTopLevelWindows()
    {
        var handles = new List<nint>();
        var handle = GCHandle.Alloc(handles);
        try
        {
            EnumWindows(&CollectWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return handles;
    }

    public bool IsWindow(nint handle) => NativeMethods.IsWindow(handle);

    public bool IsVisible(nint handle) => NativeMethods.IsWindowVisible(handle);

    public bool IsToolWindow(nint handle) =>
        (NativeMethods.GetWindowLongW(handle, GwlExStyle) & WsExToolWindow) != 0;

    public bool IsShellWindow(nint handle) => NativeMethods.GetShellWindow() == handle;

    public string GetTitle(nint handle)
    {
        var length = NativeMethods.GetWindowTextLengthW(handle);
        if (length <= 0) return string.Empty;

        var chars = new char[length + 1];
        unsafe
        {
            fixed (char* buffer = chars)
                _ = NativeMethods.GetWindowTextW(handle, buffer, chars.Length);
        }

        return new string(chars, 0, Math.Min(length, Array.IndexOf(chars, '\0') is var end && end >= 0 ? end : length));
    }

    public uint GetProcessId(nint handle)
    {
        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        return processId;
    }

    public bool TryGetBounds(nint handle, out int width, out int height)
    {
        if (!NativeMethods.GetWindowRect(handle, out var rect))
        {
            width = height = 0;
            return false;
        }

        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return width > 0 && height > 0;
    }

    public bool TryGetProcessIdentity(uint processId, out string processName, out DateTime startTimeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            if (process.HasExited)
            {
                processName = string.Empty;
                startTimeUtc = default;
                return false;
            }

            processName = process.ProcessName;
            startTimeUtc = process.StartTime.ToUniversalTime();
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            processName = string.Empty;
            startTimeUtc = default;
            return false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CollectWindow(nint handle, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is List<nint> windows) windows.Add(handle);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint data);

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindow(nint handle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindowVisible(nint handle);

        [LibraryImport("user32.dll")]
        internal static partial nint GetShellWindow();

        [LibraryImport("user32.dll")]
        internal static partial int GetWindowLongW(nint handle, int index);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        internal static partial int GetWindowTextLengthW(nint handle);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
        internal static unsafe partial int GetWindowTextW(nint handle, char* text, int maxCount);

        [LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(nint handle, out uint processId);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowRect(nint handle, out WindowRect rect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
