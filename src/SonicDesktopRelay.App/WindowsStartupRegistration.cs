using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace SonicDesktopRelay.App;

internal interface IStartupRegistration
{
    void SetEnabled(bool enabled);
}

internal interface IStartupRunKey
{
    string? Read(string name);
    void Write(string name, string value);
    void Delete(string name);
}

internal sealed class WindowsStartupRegistration(
    IStartupRunKey key,
    Func<string?> executablePath,
    ILogger logger) : IStartupRegistration
{
    private const string ValueName = "FrameRelay";

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                key.Delete(ValueName);
                return;
            }

            var executable = executablePath();
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException("Could not determine the FrameRelay executable path.");

            key.Write(ValueName, FormatCommand(executable));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not update FrameRelay login startup registration. enabled={Enabled}", enabled);
            throw;
        }
    }

    public static string FormatCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"'))
            throw new ArgumentException("The executable path cannot contain a quote character.", nameof(executablePath));
        return $"\"{executablePath}\" --startup";
    }
}

[SupportedOSPlatform("windows")]
internal sealed class CurrentUserStartupRunKey : IStartupRunKey
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Write(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Could not open the current user's startup registry key.");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
