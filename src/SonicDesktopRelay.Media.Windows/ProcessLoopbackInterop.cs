using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>Creates process-loopback recorders through NAudio's supported WASAPI activation path.</summary>
internal sealed class ProcessLoopbackClientFactory : IProcessLoopbackClientFactory
{
    public async Task<IProcessLoopbackClient> CreateAsync(
        uint targetPid, bool includeProcessTree, int sampleRate, int channels, int bitsPerSample, int frameSamples)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("Process loopback requires Windows build 20348 or later.");

        _ = frameSamples;
        var mode = includeProcessTree
            ? ProcessLoopbackMode.IncludeTargetProcessTree
            : ProcessLoopbackMode.ExcludeTargetProcessTree;
        var recorder = await new WasapiRecorderBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithProcessLoopback(targetPid, mode)
            .WithBufferLength(20)
            .WithFormat(new WaveFormat(sampleRate, bitsPerSample, channels))
            .WithMmcssThreadPriority("Audio")
            .BuildAsync()
            .ConfigureAwait(false);

        return new NAudioProcessLoopbackClient(recorder);
    }
}

internal sealed class NAudioProcessLoopbackClient : IProcessLoopbackClient
{
    private readonly WasapiRecorder _recorder;
    private bool _disposed;

    public NAudioProcessLoopbackClient(WasapiRecorder recorder)
    {
        _recorder = recorder;
        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    public event WasapiPcmDataAvailableHandler? DataAvailable;
    public event Action<Exception?>? Stopped;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recorder.StartRecording();
    }

    public void Stop()
    {
        if (_disposed) return;
        _recorder.StopRecording();
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> data,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
        => DataAvailable?.Invoke(data);

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        => Stopped?.Invoke(e.Exception);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.RecordingStopped -= OnRecordingStopped;
        await _recorder.DisposeAsync().ConfigureAwait(false);
    }
}
