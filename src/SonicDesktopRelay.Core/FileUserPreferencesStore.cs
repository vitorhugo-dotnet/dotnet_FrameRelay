using System.Text.Json;

namespace SonicDesktopRelay.Core;

/// <summary>Persists local per-user application preferences.</summary>
public sealed class FileUserPreferencesStore(string filePath)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SonicDesktopRelay", "user-preferences.json");

    public bool ReadIgnoreDiscordAudio()
    {
        if (!File.Exists(filePath)) return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            return document.RootElement.TryGetProperty("ignoreDiscordAudio", out var value)
                   && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public void WriteIgnoreDiscordAudio(bool value)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var preferences = new UserPreferences(value);
        File.WriteAllText(filePath, JsonSerializer.Serialize(preferences));
    }

    private sealed record UserPreferences(
        [property: System.Text.Json.Serialization.JsonPropertyName("ignoreDiscordAudio")]
        bool IgnoreDiscordAudio);
}
