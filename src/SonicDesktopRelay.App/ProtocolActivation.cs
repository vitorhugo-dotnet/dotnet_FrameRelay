using System.Runtime.Versioning;
using Microsoft.Win32;
using SonicDesktopRelay.Core;

namespace SonicDesktopRelay.App;

[SupportedOSPlatform("windows")]
internal static class ProtocolActivation
{
    public static void RegisterCurrentUser()
    {
        var executable = Environment.ProcessPath;
        // A framework-dependent `dotnet app.dll` invocation is not a protocol executable.
        if (executable is null || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\framerelay");
        key.SetValue("", "URL:FrameRelay launch protocol");
        key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", LaunchUri.RegistrationCommand(executable));
    }
}
