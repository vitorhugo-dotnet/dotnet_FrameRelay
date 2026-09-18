using Microsoft.Extensions.Time.Testing;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Tests;

public sealed class ScreenWatchPipelineFailureDiagnosticsTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 18, 11, 37, 0, TimeSpan.Zero);

    [Fact]
    public void Terminal_decode_failure_preserves_the_exception_reason()
    {
        using var pipeline = new ScreenWatchPipeline(
            new ThrowingDecoder(),
            new FakeTimeProvider(Start));

        pipeline.Submit(new EncodedVideoSample(
            new byte[256],
            TimeSpan.Zero,
            IsKeyFrame: false,
            Width: 0,
            Height: 0));

        Assert.Equal(WatchState.Failed, pipeline.State);
        Assert.NotNull(pipeline.LastFailure);
        Assert.Contains(nameof(UnexpectedDecoderException), pipeline.LastFailure);
        Assert.Contains("decoder boundary exploded", pipeline.LastFailure);
    }

    private sealed class ThrowingDecoder : IVideoDecoder
    {
        public string Name => "throwing-decoder";

        public VideoFrame? Decode(EncodedVideoSample sample) =>
            throw new UnexpectedDecoderException("decoder boundary exploded");

        public void Dispose()
        {
        }
    }

    private sealed class UnexpectedDecoderException(string message) : Exception(message);
}
