using Microsoft.Extensions.Logging;
using SonicDesktopRelay.App;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class FrameRelayLoggingTests
{
    [Fact]
    public void Initialization_creates_daily_log_under_local_app_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "FrameRelayLoggingTests", Guid.NewGuid().ToString("N"));

        try
        {
            using (var logging = FrameRelayLogging.Create(root))
            {
                var logger = logging.LoggerFactory.CreateLogger("FrameRelay.LoggingTests");
                logger.LogInformation("diagnostic smoke {Probe}", 42);
            }

            var directory = Path.Combine(root, "FrameRelay", "logs");
            Assert.True(Directory.Exists(directory));

            var files = Directory.GetFiles(directory, "FrameRelay-*.log");
            var file = Assert.Single(files);
            var content = File.ReadAllText(file);

            Assert.Contains("[INF]", content);
            Assert.Contains("[FrameRelay.LoggingTests]", content);
            Assert.Contains("diagnostic smoke", content);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void File_sink_keeps_trace_warning_error_and_exception_details()
    {
        var root = Path.Combine(Path.GetTempPath(), "FrameRelayLoggingTests", Guid.NewGuid().ToString("N"));

        try
        {
            using (var logging = FrameRelayLogging.Create(root))
            {
                var logger = logging.LoggerFactory.CreateLogger("FrameRelay.LevelTests");
                logger.LogTrace("trace-marker");
                logger.LogWarning("warning-marker");
                logger.LogError(new InvalidOperationException("decoder-boom"), "error-marker");
            }

            var file = Assert.Single(
                Directory.GetFiles(Path.Combine(root, "FrameRelay", "logs"), "FrameRelay-*.log"));
            var content = File.ReadAllText(file);

            Assert.Contains("[VRB]", content);
            Assert.Contains("trace-marker", content);
            Assert.Contains("[WRN]", content);
            Assert.Contains("warning-marker", content);
            Assert.Contains("[ERR]", content);
            Assert.Contains("error-marker", content);
            Assert.Contains(nameof(InvalidOperationException), content);
            Assert.Contains("decoder-boom", content);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Log_path_never_depends_on_the_install_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "FrameRelayLoggingTests", Guid.NewGuid().ToString("N"));

        using var logging = FrameRelayLogging.Create(root);

        Assert.Equal(Path.Combine(root, "FrameRelay", "logs"), logging.LogDirectory);
        Assert.StartsWith(logging.LogDirectory, logging.LogFileTemplate, StringComparison.OrdinalIgnoreCase);
    }
}
