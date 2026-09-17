namespace SonicDesktopRelay.Core;

/// <summary>
/// Persists the backend address under the current user's profile. Invalid or unreadable
/// preferences fall back to the production backend so a stale settings file cannot strand
/// the app before Settings is reachable.
/// </summary>
public sealed class FileBackendAddressStore(string filePath)
{
    public const string DefaultAddress = "https://sonicrelay-api.hugodotnet.dev";

    // Keep the existing product storage directory for compatibility with installed builds.
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SonicDesktopRelay", "backend-address.txt");

    public string Read()
    {
        if (!File.Exists(filePath)) return DefaultAddress;

        try
        {
            var value = File.ReadAllText(filePath).Trim();
            return BackendSettings.TryParse(value) is not null ? value : DefaultAddress;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DefaultAddress;
        }
    }

    public void Write(string value)
    {
        var normalized = value.Trim();
        if (BackendSettings.TryParse(normalized) is null)
            throw new ArgumentException("Backend address must be an absolute HTTP or HTTPS URL.", nameof(value));

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, normalized);
    }
}
