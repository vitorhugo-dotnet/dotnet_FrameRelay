using SonicDesktopRelay.Media.Windows;
using Xunit;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationKeyFramePolicyTests
{
    [Fact]
    public void Supported_codec_control_forces_the_next_frame_without_reconfigure()
    {
        var control = new FakeCodecControl(forceResult: true);
        var policy = new EncoderKeyFramePolicy(control);

        policy.Request();
        var action = policy.BeforeNextInput();

        Assert.Equal(EncoderKeyFrameAction.CodecApi, action);
        Assert.Equal(1, control.ForceCalls);
    }

    [Fact]
    public void Unsupported_codec_control_uses_the_reconfigure_fallback()
    {
        var control = new FakeCodecControl(forceResult: false);
        var policy = new EncoderKeyFramePolicy(control);

        policy.Request();
        var action = policy.BeforeNextInput();

        Assert.Equal(EncoderKeyFrameAction.ReconfigureFallback, action);
        Assert.Equal(1, control.ForceCalls);
    }

    [Fact]
    public void Repeated_requests_before_one_input_are_coalesced()
    {
        var control = new FakeCodecControl(forceResult: true);
        var policy = new EncoderKeyFramePolicy(control);

        policy.Request();
        policy.Request();

        Assert.Equal(EncoderKeyFrameAction.CodecApi, policy.BeforeNextInput());
        Assert.Equal(EncoderKeyFrameAction.None, policy.BeforeNextInput());
        Assert.Equal(1, control.ForceCalls);
    }

    private sealed class FakeCodecControl(bool forceResult) : IMediaFoundationCodecControl
    {
        public int ForceCalls { get; private set; }

        public bool TryForceNextKeyFrame()
        {
            ForceCalls++;
            return forceResult;
        }
    }
}
