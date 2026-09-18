using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationRuntimeTests
{
    [Fact]
    public void Runtime_starts_once_and_shuts_down_after_last_lease()
    {
        var native = new FakeMediaFoundationNative();
        var runtime = new MediaFoundationRuntime(native);

        var a = runtime.Acquire();
        using (runtime.Acquire())
            Assert.Equal(1, native.StartupCalls);

        Assert.Equal(0, native.ShutdownCalls);
        a.Dispose();
        Assert.Equal(1, native.ShutdownCalls);
    }

    [Fact]
    public void Failed_startup_does_not_leak_a_reference_and_can_retry()
    {
        var native = new FakeMediaFoundationNative { FailNextStartup = true };
        var runtime = new MediaFoundationRuntime(native);

        Assert.Throws<InvalidOperationException>(() => runtime.Acquire());
        Assert.Equal(1, native.StartupCalls);
        Assert.Equal(0, native.ShutdownCalls);

        using var lease = runtime.Acquire();
        Assert.Equal(2, native.StartupCalls);
        Assert.Equal(0, native.ShutdownCalls);
    }

    private sealed class FakeMediaFoundationNative : IMediaFoundationNative
    {
        public int StartupCalls { get; private set; }
        public int ShutdownCalls { get; private set; }
        public bool FailNextStartup { get; set; }

        public void Startup()
        {
            StartupCalls++;
            if (!FailNextStartup) return;
            FailNextStartup = false;
            throw new InvalidOperationException("startup failed");
        }

        public void Shutdown() => ShutdownCalls++;
    }
}
