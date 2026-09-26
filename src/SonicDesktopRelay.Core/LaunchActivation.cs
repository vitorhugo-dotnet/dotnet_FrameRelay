namespace SonicDesktopRelay.Core;

public enum LaunchActivationKind
{
    Share,
    Watch
}

public sealed record LaunchActivation(LaunchActivationKind Kind, string Token);

public static class LaunchActivationParser
{
    public static bool TryParse(string? value, out LaunchActivation? activation)
    {
        activation = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != "framerelay" || uri.Host != "open"
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || segments[1].Length != 43
            || segments[1].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return false;

        var kind = segments[0] switch
        {
            "share" => LaunchActivationKind.Share,
            "watch" => LaunchActivationKind.Watch,
            _ => (LaunchActivationKind?)null
        };
        if (kind is null) return false;
        activation = new LaunchActivation(kind.Value, segments[1]);
        return true;
    }

    public static string[] RemoveActivationArgument(IReadOnlyList<string> arguments, out LaunchActivation? activation)
    {
        activation = null;
        var remaining = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            if (TryParse(argument, out var parsed)) activation = parsed;
            else remaining.Add(argument);
        }
        return [.. remaining];
    }
}
