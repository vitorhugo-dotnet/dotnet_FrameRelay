namespace SonicDesktopRelay.Media.WebSocket;

public sealed record WebSocketMediaOptions(bool Enabled)
{
    public static WebSocketMediaOptions FromEnvironment(Func<string, string?> getEnvironment) =>
        new(bool.TryParse(getEnvironment("FRAMERELAY_WEBSOCKET_MEDIA_ENABLED"), out var enabled) && enabled);
}
