using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using SonicDesktopRelay.Media;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// H.264 encoder backed by Windows Media Foundation. Hardware transforms are considered first;
/// candidates that cannot satisfy the required low-latency NV12 -> H.264 contract are recorded
/// and the selector falls back to a system software transform.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    private const uint MftEnumFlagSync = 0x00000001;
    private const uint MftEnumFlagAsync = 0x00000002;
    private const uint MftEnumFlagHardware = 0x00000004;
    private const uint MftEnumFlagLocal = 0x00000010;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;

    private const uint HardwareFlags = MftEnumFlagHardware | MftEnumFlagSortAndFilter;
    private const uint SoftwareFlags =
        MftEnumFlagSync | MftEnumFlagAsync | MftEnumFlagLocal | MftEnumFlagSortAndFilter;

    private const int ProgressiveInterlaceMode = 2;
    private const int BaselineProfile = 66;
    private const int ProbeWidth = 640;
    private const int ProbeHeight = 360;
    private const int ProbeFps = 30;
    private const int ProbeBitrate = 1_500_000;
    private const int NeedMoreInputHResult = unchecked((int)0xC00D6D72);

    private readonly IDisposable _runtimeLease;
    private readonly BgraToNv12Converter _converter = new();
    private readonly Lock _gate = new();
    private readonly List<string> _rejections = [];

    private IMFTransform? _transform;
    private MediaFoundationAsyncMftPump? _asyncPump;
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrate;
    private bool _forceKeyFrame;
    private bool _asyncInputReady;
    private bool _disposed;

    public MediaFoundationH264Encoder()
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
                using var lease = MediaFoundationRuntime.Shared.Acquire();
                var output = H264RegistrationType();

                using var hardware = MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoEncoder,
                    HardwareFlags,
                    null,
                    output);
                if (hardware.Any())
                    return true;

                using var software = MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoEncoder,
                    SoftwareFlags,
                    null,
                    output);
                return software.Any();
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

    public NativeVideoDiagnostics Diagnostics => new(
        "Media Foundation",
        TransformInfo?.Name ?? Name,
        TransformInfo?.Clsid ?? Guid.Empty,
        TransformInfo?.IsHardware ?? false,
        "NV12",
        "H264",
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
                || quality.TargetBitsPerSecond != _bitrate
                || _forceKeyFrame;

            if (requiresReconfigure)
            {
                SelectAndConfigure(
                    width,
                    height,
                    quality.FramesPerSecond,
                    quality.TargetBitsPerSecond);
                _forceKeyFrame = false;
            }

            var nv12 = _converter.Convert(frame, width, height);
            using var input = CreateInputSample(
                nv12,
                frame.Timestamp,
                TimeSpan.FromTicks(TimeSpan.TicksPerSecond / quality.FramesPerSecond));

            if (_asyncPump is not null)
            {
                if (!_asyncInputReady
                    && !WaitForAsyncCredit(_asyncPump, static pump => pump.TryTakeInput(), 500))
                {
                    throw new InvalidOperationException(
                        "Hardware H.264 encoder did not request another input sample.");
                }

                _asyncInputReady = false;
                _transform!.ProcessInput(0, input, 0);

                // Hardware MFTs signal output asynchronously. Keep any NeedInput event queued
                // for the next frame while waiting for HaveOutput from this one.
                if (!WaitForAsyncCredit(_asyncPump, static pump => pump.TryTakeOutput(), 250))
                {
                    _asyncPump.DrainAvailable();
                    if (_asyncPump.InputCredits > 0)
                    {
                        _asyncInputReady = _asyncPump.TryTakeInput();
                        return null;
                    }

                    throw new InvalidOperationException(
                        "Hardware H.264 encoder produced neither output nor another input request.");
                }

                _asyncPump.DrainAvailable();
                if (_asyncPump.InputCredits > 0)
                    _asyncInputReady = _asyncPump.TryTakeInput();

                return TryReadOutput(frame.Timestamp, width, height);
            }

            _transform!.ProcessInput(0, input, 0);
            return TryReadOutput(frame.Timestamp, width, height);
        }
    }

    public void RequestKeyFrame()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The public Vortice bindings do not expose ICodecAPI today. Reopening the selected
            // MFT is the deterministic fallback: the first sample of a new encoder instance is
            // a random-access picture, which gives PLI/FIR the semantics the RTC layer needs.
            _forceKeyFrame = true;
        }
    }

    private void SelectAndConfigure(int width, int height, int fps, int bitrate)
    {
        ReleaseTransform();

        Exception? lastError = null;

        if (TryCandidates(HardwareFlags, isHardware: true, width, height, fps, bitrate, ref lastError))
            return;

        if (TryCandidates(SoftwareFlags, isHardware: false, width, height, fps, bitrate, ref lastError))
            return;

        var rejected = _rejections.Count == 0
            ? lastError?.Message ?? "no candidates were enumerated"
            : string.Join(" | ", _rejections);
        throw new InvalidOperationException(
            "No Media Foundation H.264 encoder could be configured. Rejections: " + rejected);
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
        var outputRegistration = H264RegistrationType();
        using var candidates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            flags,
            null,
            outputRegistration);

        foreach (var activation in candidates)
        {
            var friendlyName = ReadFriendlyName(activation);
            IMFTransform? transform = null;
            try
            {
                transform = activation.ActivateObject<IMFTransform>();

                var attributes = transform.Attributes;
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

                var clsid = ReadClsid(activation);
                _transform = transform;
                _asyncPump = asyncPump;
                transform = null;
                _width = width;
                _height = height;
                _fps = fps;
                _bitrate = bitrate;
                Name = string.IsNullOrWhiteSpace(friendlyName)
                    ? $"Media Foundation H.264 {(isHardware ? "hardware" : "software")} encoder"
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
                transform?.Dispose();
            }
        }

        return false;
    }

    private static void ConfigureTransform(
        IMFTransform transform,
        int width,
        int height,
        int fps,
        int bitrate)
    {
        using var outputType = MediaFactory.MFCreateMediaType();
        RunConfigurationStep("configure H.264 output attributes", () =>
        {
            SetVideoTypeCommon(outputType, VideoFormatGuids.H264, width, height, fps);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, checked((uint)bitrate)).CheckError();
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, checked((uint)BaselineProfile)).CheckError();
        });

        // Microsoft H.264 encoder requires output before input.
        RunConfigurationStep("SetOutputType(H264)", () => transform.SetOutputType(0, outputType, 0));

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

    private EncodedVideoSample? TryReadOutput(
        TimeSpan timestamp,
        int width,
        int height)
    {
        var transform = _transform!;
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

            byte[] annexB;
            try
            {
                annexB = H264AccessUnit.ToAnnexB(bytes);
            }
            catch (InvalidDataException)
            {
                // Some MFTs emit a complete raw access unit that is neither AVCC nor prefixed
                // with a four-byte Annex-B start code. Do not corrupt it; RTC diagnostics will
                // still expose the selected transform if a vendor-specific output appears.
                annexB = bytes;
            }

            var cleanPoint =
                encodedSample.GetUInt32(SampleAttributeKeys.CleanPoint, out var clean).Success
                && clean != 0;
            var keyFrame = cleanPoint || H264AccessUnit.ContainsKeyFrame(annexB);

            return new EncodedVideoSample(annexB, timestamp, keyFrame, width, height);
        }
        finally
        {
            output.Events?.Dispose();

            if (output.Sample is not null && !ReferenceEquals(output.Sample, clientSample))
                output.Sample.Dispose();

            clientSample?.Dispose();
        }
    }

    private static RegisterTypeInfo H264RegistrationType() => new()
    {
        GuidMajorType = MediaTypeGuids.Video,
        GuidSubtype = VideoFormatGuids.H264
    };

    private static string ReadFriendlyName(IMFActivate activation)
    {
        try
        {
            return activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
        }
        catch
        {
            return "Media Foundation H.264 encoder";
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
