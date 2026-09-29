using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using SonicDesktopRelay.Media;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// AV1 encoder backed by Windows Media Foundation. Hardware transforms are considered first;
/// candidates that cannot satisfy the required low-latency NV12 -> AV1 contract are rejected.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MediaFoundationAv1Encoder : IVideoEncoder
{
    private const uint MftEnumFlagHardware = 0x00000004;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;

    private const uint HardwareFlags = MftEnumFlagHardware | MftEnumFlagSortAndFilter;

    private const int ProgressiveInterlaceMode = 2;
    private const int ProbeWidth = 640;
    private const int ProbeHeight = 360;
    private const int ProbeFps = 30;
    private const int ProbeBitrate = 1_500_000;
    private static readonly Guid Av1Subtype = new("31305641-0000-0010-8000-00AA00389B71");
    private const int NeedMoreInputHResult = unchecked((int)0xC00D6D72);

    private readonly IDisposable _runtimeLease;
    private readonly BgraToNv12Converter _converter = new();
    private readonly Lock _gate = new();
    private readonly List<string> _rejections = [];
    private readonly EncoderKeyFramePolicy _keyFramePolicy = new(null);
    private readonly MediaFoundationTransformRetryPolicy _retryPolicy = new();
    private readonly MediaFoundationEncoderTimestampTracker _timestampTracker = new();

    private IMFTransform? _transform;
    private MediaFoundationCodecControl? _codecControl;
    private MediaFoundationAsyncMftPump? _asyncPump;
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrate;
    private bool _asyncInputReady;
    private bool _disposed;

    public MediaFoundationAv1Encoder()
    {
        _runtimeLease = MediaFoundationRuntime.Shared.Acquire();

        try
        {
            SelectAndConfigure(ProbeWidth, ProbeHeight, ProbeFps, ProbeBitrate);
        }
        catch
        {
            _runtimeLease.Dispose();
            throw;
        }
    }

    public static bool IsSupported
    {
        get
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                return false;

            try
            {
                return new MediaFoundationAv1CapabilityProbe()
                    .Detect().Encoders.Contains(VideoCodec.Av1);
            }
            catch
            {
                return false;
            }
        }
    }

    public string Name { get; private set; } = string.Empty;

    public MediaFoundationTransformInfo? TransformInfo { get; private set; }

    public IReadOnlyList<string> RejectionLog => _rejections;

    public string KeyFrameMode { get; private set; } = "not-requested";

    public NativeVideoDiagnostics Diagnostics => new(
        "Media Foundation",
        TransformInfo?.Name ?? Name,
        TransformInfo?.Clsid ?? Guid.Empty,
        TransformInfo?.IsHardware ?? false,
        "NV12",
        "AV1",
        _width,
        _height,
        _fps,
        _bitrate,
        _rejections.ToArray());

    public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            var (width, height) = quality.ScaleFor(frame.Width, frame.Height);
            if (width <= 0 || height <= 0)
                return null;

            var requiresReconfigure =
                _transform is null
                || width != _width
                || height != _height
                || quality.FramesPerSecond != _fps
                || quality.TargetBitsPerSecond != _bitrate;

            if (requiresReconfigure)
            {
                SelectAndConfigure(
                    width,
                    height,
                    quality.FramesPerSecond,
                    quality.TargetBitsPerSecond);

                // A required configuration change already rebuilds the transform and produces
                // the clean point needed for recovery. Consume a pending request so the next
                // input does not force a redundant codec-control attempt or second rebuild.
                _keyFramePolicy.ConsumeAfterRequiredReconfigure();
            }

            switch (_keyFramePolicy.BeforeNextInput())
            {
                case EncoderKeyFrameAction.CodecApi:
                    KeyFrameMode = "codec-api";
                    break;
                case EncoderKeyFrameAction.ReconfigureFallback:
                    SelectAndConfigure(
                        width,
                        height,
                        quality.FramesPerSecond,
                        quality.TargetBitsPerSecond);
                    KeyFrameMode = "reconfigure-fallback";
                    break;
            }

            var duration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / quality.FramesPerSecond);
            var nv12 = _converter.Convert(frame, width, height);
            return MediaFoundationTransformRetryPolicy.ExecuteWithSingleFallback(
                () => ProcessCurrentTransform(nv12, frame, duration, width, height),
                IsHardTransformFailure,
                () =>
                {
                    var failed = TransformInfo;
                    _retryPolicy.ExcludeFailed(failed?.Clsid ?? Guid.Empty);
                    _rejections.Add($"{failed?.Name ?? "active encoder"} ({failed?.Clsid}): runtime transform failure; selecting another candidate.");
                    SelectAndConfigure(width, height, quality.FramesPerSecond, quality.TargetBitsPerSecond);
                    return ProcessCurrentTransform(nv12, frame, duration, width, height);
                });
        }
    }

    public void RequestKeyFrame()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _keyFramePolicy.Request();
        }
    }

    private void SelectAndConfigure(int width, int height, int fps, int bitrate)
    {
        ReleaseTransform();

        Exception? lastError = null;

        if (TryCandidates(HardwareFlags, isHardware: true, width, height, fps, bitrate, ref lastError))
            return;

        var rejected = _rejections.Count == 0
            ? lastError?.Message ?? "no candidates were enumerated"
            : string.Join(" | ", _rejections);
        throw new InvalidOperationException(
            "No Media Foundation AV1 encoder could be configured. Rejections: " + rejected);
    }

    private bool TryCandidates(
        uint flags,
        bool isHardware,
        int width,
        int height,
        int fps,
        int bitrate,
        ref Exception? lastError)
    {
        var outputRegistration = AV1RegistrationType();
        using var candidates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            flags,
            null,
            outputRegistration);

        foreach (var activation in candidates)
        {
            var friendlyName = ReadFriendlyName(activation);
            var clsid = ReadClsid(activation);
            if (_retryPolicy.OrderCandidates([new MediaFoundationTransformCandidate(clsid, isHardware)]).Count == 0)
                continue;
            IMFTransform? transform = null;
            MediaFoundationCodecControl? codecControl = null;
            try
            {
                transform = activation.ActivateObject<IMFTransform>();

                using var attributes = transform.Attributes;
                var isAsync = attributes.GetUInt32(TransformAttributeKeys.TransformAsync, out var asyncValue).Success
                              && asyncValue != 0;

                MediaFoundationAsyncMftPump? asyncPump = null;
                if (isAsync)
                {
                    attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true).CheckError();

                    // MF_LOW_LATENCY is optional on vendor MFTs. Apply it when accepted but do
                    // not reject a perfectly usable hardware encoder over an optional tuning
                    // attribute.
                    try
                    {
                        attributes.Set(SinkWriterAttributeKeys.LowLatency, true).CheckError();
                    }
                    catch (SharpGenException)
                    {
                    }
                }

                ConfigureTransform(transform, width, height, fps, bitrate);

                if (isAsync)
                {
                    asyncPump = new MediaFoundationAsyncMftPump(
                        new VorticeMediaFoundationAsyncEventSource(transform));
                    if (!WaitForAsyncCredit(asyncPump, static pump => pump.TryTakeInput(), 500))
                    {
                        asyncPump.Dispose();
                        throw new NotSupportedException(
                            "asynchronous MFT did not request input after StartOfStream");
                    }

                    // The first input credit belongs to the first frame, so put it back as a
                    // synthetic event source credit by retaining it in the encoder.
                    _asyncInputReady = true;
                }

                codecControl = new MediaFoundationCodecControl(transform.NativePointer);
                _transform = transform;
                _asyncPump = asyncPump;
                _codecControl = codecControl;
                _keyFramePolicy.UpdateControl(codecControl);
                transform = null;
                codecControl = null;
                _width = width;
                _height = height;
                _fps = fps;
                _bitrate = bitrate;
                Name = string.IsNullOrWhiteSpace(friendlyName)
                    ? $"Media Foundation AV1 {(isHardware ? "hardware" : "software")} encoder"
                    : friendlyName;
                TransformInfo = new MediaFoundationTransformInfo(Name, clsid, isHardware);
                return true;
            }
            catch (Exception e) when (
                e is SharpGenException
                    or InvalidOperationException
                    or NotSupportedException
                    or COMException)
            {
                lastError = e;
                _rejections.Add($"{friendlyName}: {e.Message}");
            }
            finally
            {
                codecControl?.Dispose();
                transform?.Dispose();
            }
        }

        return false;
    }

    private EncodedVideoSample? ProcessCurrentTransform(
        Nv12Frame nv12,
        VideoFrame frame,
        TimeSpan duration,
        int width,
        int height)
    {
        using var input = CreateInputSample(nv12, frame.Timestamp, duration);
        if (_asyncPump is not null)
        {
            if (!_asyncInputReady
                && !WaitForAsyncCredit(_asyncPump, static pump => pump.TryTakeInput(), 500))
                throw new InvalidOperationException("Hardware AV1 encoder did not request another input sample.");

            _asyncInputReady = false;
            _transform!.ProcessInput(0, input, 0);
            _timestampTracker.Submitted(new(frame.Timestamp, duration, width, height));
            if (!WaitForAsyncCredit(_asyncPump, static pump => pump.TryTakeOutput(), 250))
            {
                _asyncPump.DrainAvailable();
                _asyncInputReady = _asyncPump.InputCredits > 0 && _asyncPump.TryTakeInput();
                // Async MFT output may legitimately lag its input. A wait timeout is not a
                // transform failure; drop this output opportunity and keep the active MFT.
                return MediaFoundationTransformRetryPolicy.ReadAsyncOutputIfReady<EncodedVideoSample>(
                    outputReady: false,
                    TryReadOutput);
            }
            _asyncPump.DrainAvailable();
            if (_asyncPump.InputCredits > 0)
                _asyncInputReady = _asyncPump.TryTakeInput();
        }
        else
        {
            _transform!.ProcessInput(0, input, 0);
            _timestampTracker.Submitted(new(frame.Timestamp, duration, width, height));
        }

        return TryReadOutput();
    }

    private static bool IsHardTransformFailure(Exception exception) =>
        exception is SharpGenException or COMException or InvalidOperationException;

    private static void ConfigureTransform(
        IMFTransform transform,
        int width,
        int height,
        int fps,
        int bitrate)
    {
        using var outputType = MediaFactory.MFCreateMediaType();
        RunConfigurationStep("configure AV1 output attributes", () =>
        {
            SetVideoTypeCommon(outputType, Av1Subtype, width, height, fps);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, checked((uint)bitrate)).CheckError();
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 0u).CheckError();
        });

        // Microsoft AV1 encoder requires output before input.
        RunConfigurationStep("SetOutputType(AV1)", () => transform.SetOutputType(0, outputType, 0));

        using var inputType = MediaFactory.MFCreateMediaType();
        RunConfigurationStep("configure NV12 input attributes", () =>
            SetVideoTypeCommon(inputType, VideoFormatGuids.NV12, width, height, fps));
        RunConfigurationStep("SetInputType(NV12)", () => transform.SetInputType(0, inputType, 0));

        RunConfigurationStep("MFT_MESSAGE_NOTIFY_BEGIN_STREAMING", () =>
            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero));
        RunConfigurationStep("MFT_MESSAGE_NOTIFY_START_OF_STREAM", () =>
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero));
    }

    private static bool WaitForAsyncCredit(
        MediaFoundationAsyncMftPump pump,
        Func<MediaFoundationAsyncMftPump, bool> takeCredit,
        int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (true)
        {
            pump.DrainAvailable();
            if (takeCredit(pump))
                return true;

            if (Environment.TickCount64 >= deadline)
                return false;

            Thread.Sleep(1);
        }
    }

    private static void RunConfigurationStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is SharpGenException or COMException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{step}: {e.Message}", e);
        }
    }

    private static void SetVideoTypeCommon(
        IMFMediaType mediaType,
        Guid subtype,
        int width,
        int height,
        int fps)
    {
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        mediaType.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
        MediaFactory.MFSetAttributeSize(
            mediaType,
            MediaTypeAttributeKeys.FrameSize,
            checked((uint)width),
            checked((uint)height)).CheckError();
        MediaFactory.MFSetAttributeRatio(
            mediaType,
            MediaTypeAttributeKeys.FrameRate,
            checked((uint)fps),
            1).CheckError();
        MediaFactory.MFSetAttributeRatio(
            mediaType,
            MediaTypeAttributeKeys.PixelAspectRatio,
            1,
            1).CheckError();
        mediaType.Set(
            MediaTypeAttributeKeys.InterlaceMode,
            checked((uint)ProgressiveInterlaceMode)).CheckError();
    }

    private static IMFSample CreateInputSample(
        Nv12Frame frame,
        TimeSpan timestamp,
        TimeSpan duration)
    {
        var sample = MediaFactory.MFCreateSample();
        IMFMediaBuffer? buffer = null;

        try
        {
            buffer = MediaFactory.MFCreateMemoryBuffer(frame.Length);
            buffer.Lock(out var destination, out _, out _);
            try
            {
                Marshal.Copy(frame.Buffer, 0, destination, frame.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = frame.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = timestamp.Ticks;
            sample.SampleDuration = duration.Ticks;
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    private EncodedVideoSample? TryReadOutput()
    {
        var transform = _transform!;
        var width = _width;
        var height = _height;
        var streamInfo = transform.GetOutputStreamInfo(0);
        var providesSamples =
            (streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;

        IMFSample? clientSample = null;
        var output = new OutputDataBuffer
        {
            StreamID = 0,
            Status = 0,
            Sample = null!,
            Events = null!
        };

        try
        {
            if (!providesSamples)
            {
                clientSample = MediaFactory.MFCreateSample();
                var capacity = Math.Max(streamInfo.Size, checked(width * height * 2));
                using var buffer = MediaFactory.MFCreateMemoryBuffer(capacity);
                clientSample.AddBuffer(buffer);
                output.Sample = clientSample;
            }

            var result = transform.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref output,
                out _);

            if (result.Failure)
            {
                if (result.Code == NeedMoreInputHResult)
                    return null;

                result.CheckError();
            }

            var encodedSample = output.Sample ?? clientSample;
            if (encodedSample is null || encodedSample.TotalLength <= 0)
                return null;

            using var contiguous = encodedSample.ConvertToContiguousBuffer();
            var length = contiguous.CurrentLength;
            if (length <= 0)
                return null;

            var bytes = new byte[length];
            contiguous.Lock(out var source, out _, out _);
            try
            {
                Marshal.Copy(source, bytes, 0, length);
            }
            finally
            {
                contiguous.Unlock();
            }

            var cleanPoint =
                encodedSample.GetUInt32(SampleAttributeKeys.CleanPoint, out var clean).Success
                && clean != 0;

            long sampleTime = 0;
            var hasMftTimestamp = true;
            try
            {
                sampleTime = encodedSample.SampleTime;
            }
            catch (Exception exception) when (exception is SharpGenException or COMException)
            {
                hasMftTimestamp = false;
            }

            var timing = _timestampTracker.ForOutput(
                hasMftTimestamp,
                hasMftTimestamp ? TimeSpan.FromTicks(sampleTime) : default);
            if (timing is null)
            {
                _rejections.Add("AV1 encoder produced an output sample without a timestamp or pending input timing.");
                return null;
            }

            return new EncodedVideoSample(
                bytes,
                timing.Value.Timestamp,
                cleanPoint,
                timing.Value.Width,
                timing.Value.Height,
                timing.Value.Duration)
            {
                Codec = VideoCodec.Av1
            };
        }
        finally
        {
            output.Events?.Dispose();

            if (output.Sample is not null && !ReferenceEquals(output.Sample, clientSample))
                output.Sample.Dispose();

            clientSample?.Dispose();
        }
    }

    private static RegisterTypeInfo AV1RegistrationType() => new()
    {
        GuidMajorType = MediaTypeGuids.Video,
        GuidSubtype = Av1Subtype
    };

    private static string ReadFriendlyName(IMFActivate activation)
    {
        try
        {
            return activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
        }
        catch
        {
            return "Media Foundation AV1 encoder";
        }
    }

    private static Guid ReadClsid(IMFActivate activation)
    {
        try
        {
            return activation.GetGUID(TransformAttributeKeys.MftTransformClsidAttribute);
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private void ReleaseTransform()
    {
        _timestampTracker.Clear();
        _keyFramePolicy.UpdateControl(null);
        _codecControl?.Dispose();
        _codecControl = null;

        _asyncPump?.Dispose();
        _asyncPump = null;
        _asyncInputReady = false;

        if (_transform is null)
            return;

        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch
        {
            // Disposal must remain best-effort; COM teardown errors cannot leak the transform.
        }

        _transform.Dispose();
        _transform = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseTransform();
            _converter.Dispose();
            _runtimeLease.Dispose();
        }
    }
}

internal readonly record struct MediaFoundationEncoderTiming(
    TimeSpan Timestamp,
    TimeSpan Duration,
    int Width,
    int Height);

/// <summary>Maps delayed AV1 encoder output to the input frame that produced it.</summary>
internal sealed class MediaFoundationEncoderTimestampTracker
{
    private const int MaxPendingTimings = 120;
    private readonly Queue<MediaFoundationEncoderTiming> _pending = new();

    internal void Submitted(MediaFoundationEncoderTiming timing)
    {
        if (_pending.Count == MaxPendingTimings)
            _pending.Dequeue();
        _pending.Enqueue(timing);
    }

    internal MediaFoundationEncoderTiming? ForOutput(bool hasMftTimestamp, TimeSpan mftTimestamp)
    {
        if (hasMftTimestamp)
        {
            var submitted = RemoveMatchingSubmission(mftTimestamp)
                ?? (_pending.Count > 0 ? _pending.Dequeue() : null);
            return submitted is { } timing ? timing with { Timestamp = mftTimestamp } : null;
        }

        return _pending.Count > 0 ? _pending.Dequeue() : null;
    }

    internal void Clear() => _pending.Clear();

    private MediaFoundationEncoderTiming? RemoveMatchingSubmission(TimeSpan timestamp)
    {
        var count = _pending.Count;
        MediaFoundationEncoderTiming? match = null;
        for (var index = 0; index < count; index++)
        {
            var candidate = _pending.Dequeue();
            if (match is null && candidate.Timestamp == timestamp)
            {
                match = candidate;
                continue;
            }

            _pending.Enqueue(candidate);
        }

        return match;
    }
}
