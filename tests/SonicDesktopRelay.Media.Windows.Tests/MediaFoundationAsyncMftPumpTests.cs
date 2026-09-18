using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationAsyncMftPumpTests
{
    [Fact]
    public void Need_input_and_have_output_are_counted_independently()
    {
        var source = new FakeSource(
            MftAsyncSignal.NeedInput,
            MftAsyncSignal.HaveOutput,
            MftAsyncSignal.NeedInput);
        using var pump = new MediaFoundationAsyncMftPump(source);

        pump.DrainAvailable();

        Assert.True(pump.TryTakeInput());
        Assert.True(pump.TryTakeOutput());
        Assert.True(pump.TryTakeInput());
        Assert.False(pump.TryTakeInput());
        Assert.False(pump.TryTakeOutput());
    }

    [Fact]
    public void Unknown_events_do_not_create_false_credits()
    {
        var source = new FakeSource(
            MftAsyncSignal.Other,
            MftAsyncSignal.NeedInput,
            MftAsyncSignal.Other);
        using var pump = new MediaFoundationAsyncMftPump(source);

        pump.DrainAvailable();

        Assert.True(pump.TryTakeInput());
        Assert.False(pump.TryTakeOutput());
    }

    private sealed class FakeSource(params MftAsyncSignal[] signals) : IMediaFoundationAsyncEventSource
    {
        private readonly Queue<MftAsyncSignal> _signals = new(signals);

        public bool TryRead(out MftAsyncSignal signal)
        {
            if (_signals.TryDequeue(out signal))
                return true;

            signal = MftAsyncSignal.Other;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
