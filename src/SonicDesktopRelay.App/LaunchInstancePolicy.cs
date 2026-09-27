using SonicDesktopRelay.Core;

namespace SonicDesktopRelay.App;

internal enum LaunchInstanceAction
{
    Run,
    ForwardActivation,
    Exit
}

internal static class LaunchInstancePolicy
{
    public static LaunchInstanceAction Decide(bool ownsInstance, LaunchActivation? initialActivation) =>
        ownsInstance
            ? LaunchInstanceAction.Run
            : initialActivation is null
                ? LaunchInstanceAction.Exit
                : LaunchInstanceAction.ForwardActivation;
}
