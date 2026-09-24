using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>Entry point isolated from the existing system-loopback recorder factory.</summary>
internal sealed class ProcessLoopbackClientFactory : IProcessLoopbackClientFactory
{
    public IProcessLoopbackClient Create(
        uint targetPid, bool includeProcessTree, int sampleRate, int channels, int bitsPerSample, int frameSamples)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("Process loopback requires Windows build 20348 or later.");

        return ProcessLoopbackInterop.Create(targetPid, includeProcessTree, sampleRate, channels, bitsPerSample, frameSamples);
    }
}

/// <summary>
/// Native process-loopback activation belongs here. The API uses ActivateAudioInterfaceAsync with
/// AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK and an IAudioClient initialized on the activated
/// stream; keeping that COM callback/buffer lifetime separate prevents accidental system capture.
/// </summary>
internal static class ProcessLoopbackInterop
{
    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    internal static IProcessLoopbackClient Create(
        uint targetPid, bool includeProcessTree, int sampleRate, int channels, int bitsPerSample, int frameSamples)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("Process loopback requires Windows build 20348 or later.");

        var parameters = new ActivationParameters
        {
            ActivationType = 1,
            Process = new ProcessParameters
            {
                TargetProcessId = targetPid,
                Mode = includeProcessTree ? 0 : 1
            }
        };
        var prop = new PropVariant
        {
            VariantType = 65, // VT_BLOB
            BlobSize = (uint)Marshal.SizeOf<ActivationParameters>(),
            BlobData = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParameters>())
        };
        Marshal.StructureToPtr(parameters, prop.BlobData, false);
        try
        {
            using var completed = new ManualResetEvent(false);
            var handler = new ActivationHandler(completed);
            var audioClientIid = AudioClientIid;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref audioClientIid,
                ref prop, handler, out var activationOperation));
            try { completed.WaitOne(); }
            finally { if (activationOperation != nint.Zero) Marshal.Release(activationOperation); }
            Marshal.ThrowExceptionForHR(handler.Result);
            if (handler.Client is null) throw new InvalidOperationException("Audio activation returned no client.");
            return new ProcessLoopbackClient(handler.Client, sampleRate, channels, bitsPerSample, frameSamples);
        }
        finally
        {
            Marshal.FreeHGlobal(prop.BlobData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessParameters { public uint TargetProcessId; public int Mode; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParameters
    {
        public int ActivationType;
        public ProcessParameters Process;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VariantType, Reserved1, Reserved2, Reserved3;
        public uint BlobSize;
        public nint BlobData;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivationOperation operation);
    }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler(ManualResetEvent completed) : IActivationCompletionHandler
    {
        public int Result { get; private set; } = unchecked((int)0x80004005);
        public IAudioClient? Client { get; private set; }
        public int ActivateCompleted(IActivationOperation operation)
        {
            try
            {
                var hr = operation.GetActivateResult(out var activationResult, out var activated);
                Result = hr < 0 ? hr : activationResult;
                if (Result >= 0 && activated is not null) Client = (IAudioClient)activated;
            }
            catch (COMException e) { Result = e.HResult; }
            finally { completed.Set(); }
            return 0;
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormat format, nint sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormat format, out nint closestMatch);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    private sealed class ProcessLoopbackClient : IProcessLoopbackClient
    {
        private readonly IAudioClient _audio;
        private readonly IAudioCaptureClient _capture;
        private readonly AutoResetEvent _ready = new(false);
        private readonly int _bytesPerFrame;
        private Thread? _thread;
        private volatile bool _stopping;
        private bool _disposed;
        private static readonly Guid CaptureClientIid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public event WasapiPcmDataAvailableHandler? DataAvailable;
        public event Action<Exception?>? Stopped;

        public ProcessLoopbackClient(IAudioClient audio, int sampleRate, int channels, int bits, int frameSamples)
        {
            _audio = audio;
            _bytesPerFrame = channels * bits / 8;
            var format = new WaveFormat
            {
                FormatTag = 1, Channels = checked((ushort)channels), SamplesPerSecond = (uint)sampleRate,
                BitsPerSample = checked((ushort)bits), BlockAlign = checked((ushort)_bytesPerFrame),
                AverageBytesPerSecond = (uint)(sampleRate * _bytesPerFrame)
            };
            _ = frameSamples;
            Marshal.ThrowExceptionForHR(audio.Initialize(0, 0x80060000, 0,
                0, ref format, nint.Zero));
            Marshal.ThrowExceptionForHR(audio.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()));
            var captureClientIid = CaptureClientIid;
            Marshal.ThrowExceptionForHR(audio.GetService(ref captureClientIid, out var service));
            _capture = (IAudioCaptureClient)service;
        }

        public void Start()
        {
            Marshal.ThrowExceptionForHR(_audio.Start());
            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "Process loopback capture" };
            _thread.Start();
        }
        public void Stop()
        {
            _stopping = true;
            _ready.Set();
            if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(2));
            _ = _audio.Stop();
        }
        private void ReadLoop()
        {
            Exception? failure = null;
            try
            {
                while (!_stopping)
                {
                    _ready.WaitOne();
                    if (_stopping) break;
                    Marshal.ThrowExceptionForHR(_capture.GetNextPacketSize(out var packetFrames));
                    while (packetFrames > 0)
                    {
                        Marshal.ThrowExceptionForHR(_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                        try
                        {
                            var bytes = checked((int)frames * _bytesPerFrame);
                            var buffer = new byte[bytes];
                            if ((flags & 2) == 0) Marshal.Copy(data, buffer, 0, bytes);
                            DataAvailable?.Invoke(buffer);
                        }
                        finally { _ = _capture.ReleaseBuffer(frames); }
                        Marshal.ThrowExceptionForHR(_capture.GetNextPacketSize(out packetFrames));
                    }
                }
            }
            catch (Exception e) { failure = e; }
            if (!_stopping || failure is not null) Stopped?.Invoke(failure);
        }
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            Stop();
            _ready.Dispose();
            if (Marshal.IsComObject(_capture)) Marshal.ReleaseComObject(_capture);
            if (Marshal.IsComObject(_audio)) Marshal.ReleaseComObject(_audio);
            return ValueTask.CompletedTask;
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid riid,
        ref PropVariant activationParams, IActivationCompletionHandler completionHandler, out nint activationOperation);
}
