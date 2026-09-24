using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace SonicDesktopRelay.App;

internal static class FrameRelayProtocolRegistration
{
    public static void EnsureRegistered(ILogger logger)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            logger.LogWarning("Could not register the FrameRelay link protocol because the executable path is unavailable.");
            return;
        }

        try
        {
            using var protocol = Registry.CurrentUser.CreateSubKey("Software\\Classes\\framerelay", writable: true);
            protocol.SetValue(string.Empty, "URL:FrameRelay Protocol", RegistryValueKind.String);
            protocol.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
            using var command = protocol.CreateSubKey("shell\\open\\command", writable: true);
            command.SetValue(string.Empty, $"\"{executable}\" \"%1\"", RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            logger.LogWarning("Could not register the FrameRelay link protocol. type={ExceptionType}", exception.GetType().Name);
        }
    }
}
