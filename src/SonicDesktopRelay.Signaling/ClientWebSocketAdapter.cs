using System.Net.WebSockets;
using System.Text;

namespace SonicDesktopRelay.Signaling;

public sealed class ClientWebSocketAdapter(Action<string, Exception?>? diagnostic = null) : IWebSocketAdapter
{
    private ClientWebSocket? _socket;

    public async Task ConnectAsync(Uri uri, string accessToken, CancellationToken ct)
    {
        if (_socket is not null) await DisposeSocketAsync();
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        await socket.ConnectAsync(uri, ct);
        _socket = socket;
    }

    public Task SendAsync(string json, CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("The signaling socket is not connected.");
        // The byte[] argument binds to the ArraySegment overload, which already returns a Task.
        return socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("The signaling socket is not connected.");
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct);
            }
            catch (WebSocketException e)
            {
                // An abrupt close is reported the same way as a graceful one: null means
                // "the socket is gone", and deciding what to do about it belongs upstairs.
                diagnostic?.Invoke("signaling.websocket.receive_failed", e);
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                diagnostic?.Invoke($"signaling.websocket.close_frame status={result.CloseStatus}", null);
                return null;
            }
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return builder.ToString();
        }
    }

    public async ValueTask DisposeAsync() => await DisposeSocketAsync();

    private async Task DisposeSocketAsync()
    {
        if (_socket is null) return;
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
        _socket.Dispose();
        _socket = null;
    }
}
