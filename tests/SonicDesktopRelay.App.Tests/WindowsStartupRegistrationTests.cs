using SonicDesktopRelay.App;
using Microsoft.Extensions.Logging.Abstractions;

namespace SonicDesktopRelay.App.Tests;

public sealed class WindowsStartupRegistrationTests
{
    [Fact]
    public void FormatCommand_quotes_executable_path_with_spaces()
    {
        Assert.Equal("\"C:\\Program Files\\Frame Relay\\FrameRelay.exe\" --startup",
            WindowsStartupRegistration.FormatCommand("C:\\Program Files\\Frame Relay\\FrameRelay.exe"));
    }

    [Fact]
    public void Repeated_enable_updates_the_same_startup_value()
    {
        var key = new FakeStartupRunKey();
        var registration = new WindowsStartupRegistration(key,
            () => "C:\\FrameRelay.exe", NullLogger.Instance);

        registration.SetEnabled(true);
        registration.SetEnabled(true);

        Assert.Equal(2, key.WriteCount);
        Assert.Equal("\"C:\\FrameRelay.exe\" --startup", key.Values["FrameRelay"]);
    }

    [Fact]
    public void Disable_deletes_the_startup_value()
    {
        var key = new FakeStartupRunKey();
        key.Values["FrameRelay"] = "old command";
        var registration = new WindowsStartupRegistration(key, () => "app.exe", NullLogger.Instance);

        registration.SetEnabled(false);

        Assert.DoesNotContain("FrameRelay", key.Values.Keys);
        Assert.Equal(1, key.DeleteCount);
    }

    [Fact]
    public void Enable_without_a_current_executable_throws()
    {
        var registration = new WindowsStartupRegistration(new FakeStartupRunKey(), () => null,
            NullLogger.Instance);

        Assert.Throws<InvalidOperationException>(() => registration.SetEnabled(true));
    }

    [Fact]
    public void Registry_adapter_errors_are_propagated()
    {
        var key = new FakeStartupRunKey { WriteException = new UnauthorizedAccessException() };
        var registration = new WindowsStartupRegistration(key, () => "app.exe", NullLogger.Instance);

        Assert.Throws<UnauthorizedAccessException>(() => registration.SetEnabled(true));
    }

    private sealed class FakeStartupRunKey : IStartupRunKey
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }
        public Exception? WriteException { get; init; }

        public string? Read(string name) => Values.GetValueOrDefault(name);

        public void Write(string name, string value)
        {
            if (WriteException is not null) throw WriteException;
            WriteCount++;
            Values[name] = value;
        }

        public void Delete(string name)
        {
            DeleteCount++;
            Values.Remove(name);
        }
    }
}
