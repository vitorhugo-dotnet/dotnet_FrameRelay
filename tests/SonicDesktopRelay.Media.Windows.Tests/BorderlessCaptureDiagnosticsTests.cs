using Microsoft.Extensions.Logging;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class BorderlessCaptureDiagnosticsTests
{
    [Theory]
    [InlineData("monitor")]
    [InlineData("window")]
    public void Log_emits_capture_kind_and_fallback_reason(string targetKind)
    {
        var logger = new RecordingLogger();
        var result = new BorderlessCaptureResult(false, "denied", "user denied access");

        BorderlessCaptureDiagnostics.Log(logger, targetKind, result);

        var entry = Assert.Single(logger.Entries);
        var state = entry.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(targetKind, state["capture_target_kind"]);
        Assert.Equal("denied", state["borderless_outcome"]);
        Assert.Equal(false, state["borderless_enabled"]);
        Assert.Equal("user denied access", state["borderless_fallback_reason"]);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToArray()
                : [];
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), values));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State);
}
