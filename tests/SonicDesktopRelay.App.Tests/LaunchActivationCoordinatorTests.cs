using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using SonicDesktopRelay.App;
using SonicDesktopRelay.Core;

namespace SonicDesktopRelay.App.Tests;

public sealed class LaunchActivationCoordinatorTests
{
    [Theory]
    [InlineData(LaunchActivationKind.Share)]
    [InlineData(LaunchActivationKind.Watch)]
    public async Task Protocol_launch_reaches_the_running_instance(LaunchActivationKind kind)
    {
        var pipeName = "FrameRelay.Activation.Tests." + Guid.NewGuid().ToString("N");
        var received = Channel.CreateUnbounded<LaunchActivation>();
        // Exercise the real pipe server/client without acquiring the installed app's mutex.
        var coordinator = (LaunchActivationCoordinator)RuntimeHelpers.GetUninitializedObject(
            typeof(LaunchActivationCoordinator));
        typeof(LaunchActivationCoordinator).GetField("_pipeName", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, pipeName);
        typeof(LaunchActivationCoordinator).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, NullLogger.Instance);
        coordinator.Activated += activation => received.Writer.TryWrite(activation);
        using var shutdown = new CancellationTokenSource();
        var server = (Task)typeof(LaunchActivationCoordinator)
            .GetMethod("RunServerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, [shutdown.Token])!;
        try
        {
            foreach (var tokenCharacter in new[] { 'a', 'b' })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var activation = new LaunchActivation(kind, new string(tokenCharacter, 43));
                var forward = (Task<bool>)typeof(LaunchActivationCoordinator)
                    .GetMethod("TryForwardAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [activation, pipeName, timeout.Token])!;

                Assert.True(await forward.WaitAsync(TimeSpan.FromSeconds(10)));
                var delivered = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(activation, delivered);
            }
        }
        finally
        {
            await shutdown.CancelAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
