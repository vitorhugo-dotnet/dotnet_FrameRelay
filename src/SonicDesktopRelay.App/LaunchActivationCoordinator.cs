using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using SonicDesktopRelay.Core;

namespace SonicDesktopRelay.App;

internal sealed class LaunchActivationCoordinator : IDisposable
{
    private const string MutexPrefix = "Local\\FrameRelay.Activation.";
    private const string PipePrefix = "FrameRelay.Activation.";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly Task _serverTask;
    private bool _ownsMutex;

    private LaunchActivationCoordinator(ILogger logger)
    {
        _logger = logger;
        var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var safeUserId = string.Concat(userId.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
        _pipeName = PipePrefix + safeUserId;
        _mutex = new Mutex(false, MutexPrefix + safeUserId);
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (_ownsMutex) _serverTask = RunServerAsync(_shutdown.Token);
        else _serverTask = Task.CompletedTask;
    }

    public event Action<LaunchActivation>? Activated;

    public static async Task<(LaunchActivationCoordinator? Coordinator, string[] Arguments, LaunchActivation? Initial)>
        StartAsync(string[] arguments, ILogger logger, CancellationToken ct)
    {
        var remaining = LaunchActivationParser.RemoveActivationArgument(arguments, out var initial);
        var coordinator = new LaunchActivationCoordinator(logger);
        if (coordinator._ownsMutex)
        {
            return (coordinator, remaining, initial);
        }

        if (initial is not null)
        {
            var forwarded = await TryForwardAsync(initial, coordinator._pipeName, ct);
            if (!forwarded) logger.LogWarning("Could not forward FrameRelay activation to the running instance.");
        }
        coordinator.Dispose();
        return (null, remaining, initial);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _serverTask.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }
        _mutex.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(ct);
                if (LaunchActivationParser.TryParse(line, out var activation))
                {
                    Dispatch(activation!);
                    await writer.WriteLineAsync("OK");
                }
                else
                {
                    await writer.WriteLineAsync("REJECTED");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                if (!ct.IsCancellationRequested) _logger.LogDebug("FrameRelay activation pipe client disconnected.");
            }
            catch (Exception exception)
            {
                _logger.LogWarning("FrameRelay activation pipe failed. type={ExceptionType}", exception.GetType().Name);
            }
        }
    }

    private void Dispatch(LaunchActivation activation)
    {
        try { Activated?.Invoke(activation); }
        catch (Exception exception)
        {
            _logger.LogWarning("FrameRelay activation could not be dispatched. type={ExceptionType}", exception.GetType().Name);
        }
    }

    private static async Task<bool> TryForwardAsync(LaunchActivation activation, string pipeName, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(4000, ct);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
            var mode = activation.Kind == LaunchActivationKind.Share ? "share" : "watch";
            await writer.WriteLineAsync($"framerelay://open/{mode}/{activation.Token}");
            return await reader.ReadLineAsync(ct) == "OK";
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }
}
