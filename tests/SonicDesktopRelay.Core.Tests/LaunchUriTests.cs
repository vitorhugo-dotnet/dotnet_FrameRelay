using SonicDesktopRelay.Core;

namespace SonicDesktopRelay.Core.Tests;

public sealed class LaunchUriTests
{
    [Fact]
    public void Accepts_only_exact_capability_uri()
    {
        var token = new string('a', 64);
        Assert.Equal(token, LaunchUri.ParseToken("framerelay://launch?token=" + token));
        foreach (var input in new[] {
            "framerelay://launch/?token=" + token,
            "framerelay://user@launch?token=" + token,
            "framerelay://launch:80?token=" + token,
            "framerelay://launch?token=" + token + "&token=" + token,
            "framerelay://launch?token=" + token + "#fragment",
            "framerelay://launch?token=" + new string('A', 64),
            "https://launch?token=" + token,
            "framerelay://launch?token=" + new string('a', 63) })
            Assert.Null(LaunchUri.ParseToken(input));
    }

    [Fact]
    public void Registration_quotes_executable_and_argument()
    {
        // Use the platform's absolute path so the pure contract test also runs outside Windows.
        var path = Path.GetFullPath("directory with spaces/FrameRelay.exe");
        Assert.Equal($"\"{path}\" \"%1\"", LaunchUri.RegistrationCommand(path));
        Assert.Throws<ArgumentException>(() => LaunchUri.RegistrationCommand("relative.exe"));
        Assert.Throws<ArgumentException>(() => LaunchUri.RegistrationCommand(path + "\" -bad"));
    }
}
