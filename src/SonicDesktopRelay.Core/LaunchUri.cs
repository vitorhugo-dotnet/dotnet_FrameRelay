namespace SonicDesktopRelay.Core;

/// <summary>Strict protocol boundary. Capability tokens must never be logged.</summary>
public static class LaunchUri
{
    public static string? ParseToken(string? argument)
    {
        // Match the literal wire shape, rejecting extra fields, escapes and alternative authorities.
        const string prefix = "framerelay://launch?token=";
        if (argument is null || !argument.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var token = argument[prefix.Length..];
        return token.Length == 64 && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? token : null;
    }

    public static string RegistrationCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"')
            || executablePath.Any(char.IsControl) || !Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("A full executable path is required.", nameof(executablePath));
        return $"\"{executablePath}\" \"%1\"";
    }
}
