using SonicDesktopRelay.App;
using SonicDesktopRelay.Core;

namespace SonicDesktopRelay.App.Tests;

public sealed class LaunchInstancePolicyTests
{
    private static readonly LaunchActivation ProtocolActivation =
        new(LaunchActivationKind.Watch, new string('a', 43));

    [Fact]
    public void Duplicate_startup_launch_exits_without_forwarding_or_starting_ui()
    {
        Assert.Equal(LaunchInstanceAction.Exit,
            LaunchInstancePolicy.Decide(ownsInstance: false, initialActivation: null));
    }

    [Fact]
    public void Duplicate_protocol_launch_forwards_activation_to_running_instance()
    {
        Assert.Equal(LaunchInstanceAction.ForwardActivation,
            LaunchInstancePolicy.Decide(ownsInstance: false, initialActivation: ProtocolActivation));
    }

    [Fact]
    public void Mutex_owner_starts_as_the_primary_instance()
    {
        Assert.Equal(LaunchInstanceAction.Run,
            LaunchInstancePolicy.Decide(ownsInstance: true, initialActivation: null));
    }
}
