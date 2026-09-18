using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows;

internal enum MftAsyncSignal
{
    Other,
    NeedInput,
    HaveOutput
}

internal interface IMediaFoundationAsyncEventSource : IDisposable
{
    bool TryRead(out MftAsyncSignal signal);
}

/// <summary>
/// Converts the asynchronous MFT event queue into independent input/output credits. Hardware
/// video transforms may emit NeedInput before HaveOutput (or vice versa); keeping those credits
/// separate lets a synchronous caller drive the async transform without losing either signal.
/// </summary>
internal sealed class MediaFoundationAsyncMftPump(IMediaFoundationAsyncEventSource source) : IDisposable
{
    private int _inputCredits;
    private int _outputCredits;
    private bool _disposed;

    internal int InputCredits => _inputCredits;
    internal int OutputCredits => _outputCredits;

    internal void DrainAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A broken MFT must not trap a capture callback in an unbounded event loop.
        for (var i = 0; i < 64 && source.TryRead(out var signal); i++)
        {
            switch (signal)
            {
                case MftAsyncSignal.NeedInput:
                    if (_inputCredits < int.MaxValue) _inputCredits++;
                    break;

                case MftAsyncSignal.HaveOutput:
                    if (_outputCredits < int.MaxValue) _outputCredits++;
                    break;
            }
        }
    }

    internal bool TryTakeInput()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inputCredits <= 0) return false;
        _inputCredits--;
        return true;
    }

    internal bool TryTakeOutput()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_outputCredits <= 0) return false;
        _outputCredits--;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        source.Dispose();
    }
}

internal sealed class VorticeMediaFoundationAsyncEventSource : IMediaFoundationAsyncEventSource
{
    private const int MfEventFlagNoWait = 1;
    private const int MfNoEventsAvailable = unchecked((int)0xC00D3E80);
    private const int MeTransformNeedInput = 601;
    private const int MeTransformHaveOutput = 602;

    private readonly IMFMediaEventGenerator _events;
    private bool _disposed;

    internal VorticeMediaFoundationAsyncEventSource(IMFTransform transform)
    {
        _events = transform.QueryInterface<IMFMediaEventGenerator>();
    }

    public bool TryRead(out MftAsyncSignal signal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            using var mediaEvent = _events.GetEvent(MfEventFlagNoWait);
            signal = (int)mediaEvent.EventType switch
            {
                MeTransformNeedInput => MftAsyncSignal.NeedInput,
                MeTransformHaveOutput => MftAsyncSignal.HaveOutput,
                _ => MftAsyncSignal.Other
            };
            return true;
        }
        catch (SharpGenException e) when (e.HResult == MfNoEventsAvailable)
        {
            signal = MftAsyncSignal.Other;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _events.Dispose();
    }
}
