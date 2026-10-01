namespace SonicDesktopRelay.Media.WebSocket;

public sealed record WebSocketMediaOptions(bool Enabled)
{
    public static WebSocketMediaOptions FromEnvironment(Func<string, string?> getEnvironment)
    {
        var value = getEnvironment("FRAMERELAY_WEBSOCKET_MEDIA_ENABLED");
        return new(value is null || (bool.TryParse(value, out var enabled) && enabled));
    }
}
