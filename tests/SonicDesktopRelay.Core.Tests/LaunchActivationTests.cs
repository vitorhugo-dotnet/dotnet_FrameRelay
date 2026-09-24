using SonicDesktopRelay.Core;
using Xunit;

namespace SonicDesktopRelay.Core.Tests;

public sealed class LaunchActivationTests
{
    [Theory]
    [InlineData("framerelay://open/share/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", LaunchActivationKind.Share)]
    [InlineData("framerelay://open/watch/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", LaunchActivationKind.Watch)]
    public void Parses_only_supported_activation_uris(string value, LaunchActivationKind expectedKind)
    {
        Assert.True(LaunchActivationParser.TryParse(value, out var activation));
        Assert.Equal(expectedKind, activation!.Kind);
        Assert.Equal(new string('a', 43), activation.Token);
    }

    [Theory]
    [InlineData("https://framerelay.hugojava.dev/open/share/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("framerelay://open/admin/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("framerelay://open/share/short")]
    [InlineData("framerelay://open/share/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?x=1")]
    [InlineData("framerelay://open/share/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa#token")]
    [InlineData("framerelay://open/share/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")]
    public void Rejects_invalid_or_non_custom_uris(string value) =>
        Assert.False(LaunchActivationParser.TryParse(value, out _));

    [Fact]
    public void Removes_activation_arguments_and_preserves_regular_arguments()
    {
        var remaining = LaunchActivationParser.RemoveActivationArgument(
            ["--diagnostics", "framerelay://open/watch/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"],
            out var activation);

        Assert.Equal(["--diagnostics"], remaining);
        Assert.Equal(LaunchActivationKind.Watch, activation!.Kind);
    }
}
