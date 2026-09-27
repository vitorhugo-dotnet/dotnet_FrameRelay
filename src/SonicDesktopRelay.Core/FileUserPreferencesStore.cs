using System.Text.Json;

namespace SonicDesktopRelay.Core;

/// <summary>Persists local per-user application preferences.</summary>
public sealed class FileUserPreferencesStore(string filePath)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SonicDesktopRelay", "user-preferences.json");

    public bool ReadIgnoreDiscordAudio()
        => ReadBoolean("ignoreDiscordAudio");

    public bool ReadStartOnSystemStartup()
        => ReadBoolean("startOnSystemStartup");

    public bool ReadMinimizeToTray()
        => ReadBoolean("minimizeToTray");

    private bool ReadBoolean(string propertyName)
    {
        if (!File.Exists(filePath)) return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            return document.RootElement.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public void WriteIgnoreDiscordAudio(bool value)
        => WritePreferences(ReadStartOnSystemStartup(), ReadMinimizeToTray(), value);

    public void WriteStartOnSystemStartup(bool value)
        => WritePreferences(value, ReadMinimizeToTray(), ReadIgnoreDiscordAudio());

    public void WriteMinimizeToTray(bool value)
        => WritePreferences(ReadStartOnSystemStartup(), value, ReadIgnoreDiscordAudio());

    private void WritePreferences(bool startOnSystemStartup, bool minimizeToTray, bool ignoreDiscordAudio)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var preferences = new UserPreferences(startOnSystemStartup, minimizeToTray, ignoreDiscordAudio);
        File.WriteAllText(filePath, JsonSerializer.Serialize(preferences));
    }

    private sealed record UserPreferences(
        [property: System.Text.Json.Serialization.JsonPropertyName("startOnSystemStartup")]
        bool StartOnSystemStartup,
        [property: System.Text.Json.Serialization.JsonPropertyName("minimizeToTray")]
        bool MinimizeToTray,
        [property: System.Text.Json.Serialization.JsonPropertyName("ignoreDiscordAudio")]
        bool IgnoreDiscordAudio);
}
