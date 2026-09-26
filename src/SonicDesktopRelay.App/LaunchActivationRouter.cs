using SonicDesktopRelay.Core;

namespace SonicDesktopRelay.App;

internal static class LaunchActivationRouter
{
    private static readonly object Gate = new();
    private static Action<LaunchActivation>? _handler;
    private static LaunchActivation? _pending;

    public static void SetInitial(LaunchActivation? activation)
    {
        if (activation is null) return;
        Action<LaunchActivation>? handler;
        lock (Gate)
        {
            handler = _handler;
            if (handler is null) _pending = activation;
        }
        handler?.Invoke(activation);
    }

    public static void Dispatch(LaunchActivation activation)
    {
        Action<LaunchActivation>? handler;
        lock (Gate)
        {
            handler = _handler;
            if (handler is null) _pending = activation;
        }
        handler?.Invoke(activation);
    }

    public static void Register(Action<LaunchActivation> handler)
    {
        LaunchActivation? pending;
        lock (Gate)
        {
            _handler = handler;
            pending = _pending;
            _pending = null;
        }
        if (pending is not null) handler(pending);
    }

    public static void Unregister(Action<LaunchActivation> handler)
    {
        lock (Gate)
            if (_handler == handler) _handler = null;
    }
}
