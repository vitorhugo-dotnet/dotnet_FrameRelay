using SonicDesktopRelay.Core;

namespace SonicDesktopRelay.Core.Tests;

public sealed class FileUserPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"sonicdesktoprelay-preferences-{Guid.NewGuid():N}");
    private string PathUnderTest => Path.Combine(_directory, "user-preferences.json");

    [Fact]
    public void Missing_preference_defaults_to_not_ignoring_discord_audio()
    {
        var store = new FileUserPreferencesStore(PathUnderTest);

        Assert.False(store.ReadIgnoreDiscordAudio());
    }

    [Fact]
    public void Ignore_discord_audio_survives_a_new_store_instance()
    {
        new FileUserPreferencesStore(PathUnderTest).WriteIgnoreDiscordAudio(true);

        Assert.True(new FileUserPreferencesStore(PathUnderTest).ReadIgnoreDiscordAudio());
    }

    [Fact]
    public void Invalid_preferences_default_to_not_ignoring_discord_audio()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathUnderTest, "not json");

        Assert.False(new FileUserPreferencesStore(PathUnderTest).ReadIgnoreDiscordAudio());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Non_object_json_preferences_default_to_not_ignoring_discord_audio(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathUnderTest, json);

        Assert.False(new FileUserPreferencesStore(PathUnderTest).ReadIgnoreDiscordAudio());
    }

    [Fact]
    public void Missing_and_legacy_preferences_default_tray_and_startup_to_disabled()
    {
        var store = new FileUserPreferencesStore(PathUnderTest);
        Assert.False(store.ReadStartOnSystemStartup());
        Assert.False(store.ReadMinimizeToTray());

        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathUnderTest, "{\"ignoreDiscordAudio\":true}");
        store = new FileUserPreferencesStore(PathUnderTest);

        Assert.True(store.ReadIgnoreDiscordAudio());
        Assert.False(store.ReadStartOnSystemStartup());
        Assert.False(store.ReadMinimizeToTray());
    }

    [Fact]
    public void All_preferences_survive_writes_from_separate_store_instances()
    {
        var store = new FileUserPreferencesStore(PathUnderTest);
        store.WriteIgnoreDiscordAudio(true);
        store.WriteStartOnSystemStartup(true);
        store.WriteMinimizeToTray(true);

        store = new FileUserPreferencesStore(PathUnderTest);
        Assert.True(store.ReadIgnoreDiscordAudio());
        Assert.True(store.ReadStartOnSystemStartup());
        Assert.True(store.ReadMinimizeToTray());
    }

    [Fact]
    public void Invalid_json_defaults_new_preferences_to_disabled()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathUnderTest, "not json");
        var store = new FileUserPreferencesStore(PathUnderTest);

        Assert.False(store.ReadStartOnSystemStartup());
        Assert.False(store.ReadMinimizeToTray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
