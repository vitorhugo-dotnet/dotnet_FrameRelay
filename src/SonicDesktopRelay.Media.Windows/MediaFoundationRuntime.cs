using System.Runtime.CompilerServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

[assembly: InternalsVisibleTo("SonicDesktopRelay.Media.Windows.Tests")]

namespace SonicDesktopRelay.Media.Windows;

internal interface IMediaFoundationNative
{
    void Startup();

    void Shutdown();
}

internal sealed class MediaFoundationRuntime
{
    private readonly IMediaFoundationNative _native;
    private readonly Lock _gate = new();
    private int _leases;

    internal static MediaFoundationRuntime Shared { get; } = new(new VorticeMediaFoundationNative());

    internal MediaFoundationRuntime(IMediaFoundationNative native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    internal IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_leases == 0)
                _native.Startup();

            _leases++;
            return new Lease(this);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_leases <= 0) return;

            _leases--;
            if (_leases == 0)
                _native.Shutdown();
        }
    }

    private sealed class Lease(MediaFoundationRuntime owner) : IDisposable
    {
        private MediaFoundationRuntime? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private sealed class VorticeMediaFoundationNative : IMediaFoundationNative
    {
        public void Startup() => MediaFactory.MFStartup().CheckError();

        public void Shutdown() => MediaFactory.MFShutdown().CheckError();
    }
}
