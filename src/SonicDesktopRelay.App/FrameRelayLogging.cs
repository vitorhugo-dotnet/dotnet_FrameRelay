using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace SonicDesktopRelay.App;

/// <summary>
/// Application logging bootstrap. This deliberately remains a thin adapter around
/// Microsoft.Extensions.Logging + Serilog rather than becoming a second logging framework.
/// </summary>
public sealed class FrameRelayLogging : IDisposable
{
    private readonly Serilog.ILogger _serilog;
    private bool _disposed;

    private FrameRelayLogging(
        string logDirectory,
        string logFileTemplate,
        Serilog.ILogger serilog,
        ILoggerFactory loggerFactory)
    {
        LogDirectory = logDirectory;
        LogFileTemplate = logFileTemplate;
        _serilog = serilog;
        LoggerFactory = loggerFactory;
    }

    public static FrameRelayLogging? Current { get; private set; }

    public string LogDirectory { get; }

    public string LogFileTemplate { get; }

    public ILoggerFactory LoggerFactory { get; }

    public static FrameRelayLogging InitializeDefault()
    {
        Current?.Dispose();
        Current = Create();
        return Current;
    }

    public static FrameRelayLogging Create(string? localApplicationDataRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(localApplicationDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localApplicationDataRoot;

        var directory = Path.Combine(root, "FrameRelay", "logs");
        Directory.CreateDirectory(directory);

        // Serilog's daily rolling file sink appends the date to this template. Keeping the
        // installation directory out of the path matters for normal non-admin installs.
        var fileTemplate = Path.Combine(directory, "FrameRelay-.log");

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("Avalonia", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .WriteTo.File(
                fileTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] [T{ThreadId}] " +
                    "{Message:lj} {Properties:j}{NewLine}{Exception}")
            .CreateLogger();

        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddSerilog(serilog, dispose: false);
        });

        return new FrameRelayLogging(directory, fileTemplate, serilog, factory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        LoggerFactory.Dispose();
        (_serilog as IDisposable)?.Dispose();

        if (ReferenceEquals(Current, this))
            Current = null;
    }
}
