using SonicDesktopRelay.App;
using SonicDesktopRelay.Core;
using SonicDesktopRelay.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace SonicDesktopRelay.App.Tests;

public sealed class ShellPreferenceTests
{
    [Fact]
    public async Task Startup_and_tray_preferences_persist_independently()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileUserPreferencesStore(Path.Combine(directory.Path, "preferences.json"));
        var registration = new FakeStartupRegistration();
        await using (var shell = CreateShell(store, registration))
        {
            shell.StartOnSystemStartup = true;
            shell.MinimizeToTray = true;
            shell.StartOnSystemStartup = false;
        }

        Assert.False(store.ReadStartOnSystemStartup());
        Assert.True(store.ReadMinimizeToTray());
        Assert.Equal([false, true, false], registration.EnabledStates);
    }

    [Fact]
    public async Task Startup_registration_failure_is_reported_and_preference_is_kept()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileUserPreferencesStore(Path.Combine(directory.Path, "preferences.json"));
        var registration = new FakeStartupRegistration { Failure = new UnauthorizedAccessException("denied") };
        await using var shell = CreateShell(store, registration);

        shell.StartOnSystemStartup = true;

        Assert.True(shell.StartOnSystemStartup);
        Assert.True(store.ReadStartOnSystemStartup());
        Assert.Contains("startup", shell.ShellError, StringComparison.OrdinalIgnoreCase);
    }

    private static Shell CreateShell(FileUserPreferencesStore store, FakeStartupRegistration registration) =>
        new(new EmptyMonitorEnumerator(), new EmptyWindowEnumerator(), store, registration);

    private sealed class FakeStartupRegistration : IStartupRegistration
    {
        public List<bool> EnabledStates { get; } = [];
        public Exception? Failure { get; init; }

        public void SetEnabled(bool enabled)
        {
            if (Failure is not null) throw Failure;
            EnabledStates.Add(enabled);
        }
    }

    private sealed class EmptyMonitorEnumerator : IMonitorEnumerator
    {
        public IReadOnlyList<MonitorInfo> List() => [];
    }

    private sealed class EmptyWindowEnumerator : IWindowEnumerator
    {
        public IReadOnlyList<WindowInfo> List() => [];
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
