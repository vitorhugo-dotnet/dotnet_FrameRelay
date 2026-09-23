using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpGen.Runtime;
using SonicDesktopRelay.Media;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// H.264 decoder backed by Windows Media Foundation. Candidates are enumerated hardware-first,
/// then software; asynchronous hardware transforms are skipped until the shared async MFT driver
/// can safely feed their event queue. Output is normalized to the existing reusable BGRA frame.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MediaFoundationH264Decoder : IVideoDecoder
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
    private const int NeedMoreInputHResult = unchecked((int)0xC00D6D72);
    private const int StreamChangeHResult = unchecked((int)0xC00D6D61);
    private const int NoMoreTypesHResult = unchecked((int)0xC00D36B9);

    [StructLayout(LayoutKind.Sequential)]
    private struct MfOffset
    {
        public ushort Fraction;
        public short Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MfVideoArea
    {
        public MfOffset OffsetX;
        public MfOffset OffsetY;
        public int Width;
        public int Height;
    }

    private readonly IDisposable _runtimeLease;
    private readonly Lock _gate = new();
    private readonly List<string> _rejections = [];
    private readonly MediaFoundationTransformRetryPolicy _retryPolicy = new();
    private readonly ILogger<MediaFoundationH264Decoder> _logger;

    private IMFTransform? _transform;
    private int _visibleWidth;
    private int _visibleHeight;
    private int _codedWidth;
    private int _codedHeight;
    private int _stride;
    private int _outputBufferSize;
    private int? _lastOutputStreamFlags;
    private OutputSampleAllocationMode? _lastOutputSampleAllocationMode;
    private long _processOutputCalls;
    private byte[] _bgra = [];
    private bool _transportGeometryKnown;
    private bool _configured;
    private bool _disposed;

    public MediaFoundationH264Decoder(ILogger<MediaFoundationH264Decoder>? logger = null)
    {
        _logger = logger ?? NullLogger<MediaFoundationH264Decoder>.Instance;
        _runtimeLease = MediaFoundationRuntime.Shared.Acquire();

        try
        {
            SelectCandidate();
            _logger.LogInformation(
                "Media Foundation H.264 decoder selected. transform={TransformName} clsid={TransformClsid} acceleration={Acceleration}",
                TransformInfo?.Name ?? Name,
                TransformInfo?.Clsid ?? Guid.Empty,
                TransformInfo?.IsHardware == true ? "hardware" : "software");
        }
        catch (Exception exception)
        {
            _logger.LogCritical(
                exception,
                "Media Foundation H.264 decoder initialization failed. hresult=0x{HResult:X8}",
                exception.HResult);
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
                var input = H264RegistrationType();

                using var hardware = MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoDecoder,
                    HardwareFlags,
                    input,
                    null);
                if (hardware.Any())
                    return true;

                using var software = MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoDecoder,
                    SoftwareFlags,
                    input,
                    null);
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
        "H264",
        "NV12",
        _visibleWidth,
        _visibleHeight,
        0,
        0,
        _rejections.ToArray());

    public string? LastFailure { get; private set; }

    public VideoFrame? Decode(EncodedVideoSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (sample.Data.IsEmpty)
                return null;

            LastFailure = null;
            var stage = "validate-geometry";

            try
            {
                var hasTransportGeometry = HasKnownDimensions(sample.Width, sample.Height);
                if (!_configured ||
                    (hasTransportGeometry &&
                     (sample.Width != _visibleWidth || sample.Height != _visibleHeight)))
                {
                    stage = "reconfigure";
                    _logger.LogInformation(
                        "H.264 decoder reconfigure requested. transportWidth={Width} transportHeight={Height} configured={Configured} currentVisible={VisibleWidth}x{VisibleHeight}",
                        sample.Width,
                        sample.Height,
                        _configured,
                        _visibleWidth,
                        _visibleHeight);
                    Reconfigure(sample.Width, sample.Height);
                }

                stage = "create-input-sample";
                stage = "process-input";
                return MediaFoundationTransformRetryPolicy.ExecuteWithSingleFallback(
                    () => DecodeWithCurrentTransform(sample),
                    IsHardTransformFailure,
                    () =>
                    {
                        var failed = TransformInfo;
                        _retryPolicy.ExcludeFailed(failed?.Clsid ?? Guid.Empty);
                        _rejections.Add($"{failed?.Name ?? "active decoder"} ({failed?.Clsid}): runtime transform failure; selecting another candidate.");
                        ReleaseTransform();
                        SelectCandidate();
                        _configured = false;
                        Reconfigure(sample.Width, sample.Height);
                        return DecodeWithCurrentTransform(sample);
                    });
            }
            catch (Exception e) when (
                e is SharpGenException
                    or COMException
                    or InvalidOperationException
                    or ArgumentException)
            {
                LastFailure = $"{stage}: {e.GetType().Name} (0x{e.HResult:X8}): {e.Message}";
                _logger.LogWarning(
                    e,
                    "Recoverable H.264 decode failure. stage={Stage} hresult=0x{HResult:X8} bytes={AccessUnitBytes} keyFrame={IsKeyFrame} transportGeometry={Width}x{Height} visibleGeometry={VisibleWidth}x{VisibleHeight}",
                    stage,
                    e.HResult,
                    sample.Data.Length,
                    sample.IsKeyFrame,
                    sample.Width,
                    sample.Height,
                    _visibleWidth,
                    _visibleHeight);

                // Packet loss/corruption is normal network weather. Keep the decoder alive and
                // wait for the next clean access unit/keyframe rather than killing the viewer.
                return null;
            }
            catch (Exception e)
            {
                LastFailure = $"{stage}: {e.GetType().Name} (0x{e.HResult:X8}): {e.Message}";
                _logger.LogError(
                    e,
                    "Unexpected H.264 decoder exception escaped. stage={Stage} hresult=0x{HResult:X8} bytes={AccessUnitBytes} keyFrame={IsKeyFrame} transportGeometry={Width}x{Height} visibleGeometry={VisibleWidth}x{VisibleHeight}",
                    stage,
                    e.HResult,
                    sample.Data.Length,
                    sample.IsKeyFrame,
                    sample.Width,
                    sample.Height,
                    _visibleWidth,
                    _visibleHeight);
                throw;
            }
        }
    }

    private void SelectCandidate()
    {
        Exception? lastError = null;

        if (TryCandidates(HardwareFlags, isHardware: true, ref lastError))
            return;

        if (TryCandidates(SoftwareFlags, isHardware: false, ref lastError))
            return;

        var rejected = _rejections.Count == 0
            ? lastError?.Message ?? "no candidates were enumerated"
            : string.Join(" | ", _rejections);
        throw new InvalidOperationException(
            "No Media Foundation H.264 decoder could be activated. Rejections: " + rejected);
    }

    private bool TryCandidates(uint flags, bool isHardware, ref Exception? lastError)
    {
        var input = H264RegistrationType();
        using var candidates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            flags,
            input,
            null);

        foreach (var activation in candidates)
        {
            var friendlyName = ReadFriendlyName(activation);
            var clsid = ReadClsid(activation);
            if (_retryPolicy.OrderCandidates([new MediaFoundationTransformCandidate(clsid, isHardware)]).Count == 0)
                continue;
            IMFTransform? transform = null;
            try
            {
                transform = activation.ActivateObject<IMFTransform>();

                using var attributes = transform.Attributes;
                var isAsync = attributes is not null
                              && attributes.GetUInt32(
                                  TransformAttributeKeys.TransformAsync,
                                  out var asyncValue).Success
                              && asyncValue != 0;

                if (isAsync)
                    throw new NotSupportedException(
                        "asynchronous MFT requires the hardware event pump");

                try
                {
                    attributes?.Set(SinkWriterAttributeKeys.LowLatency, true).CheckError();
                }
                catch (SharpGenException)
                {
                    // Optional on third-party decoders. Failure here must not discard an
                    // otherwise usable software fallback.
                }

                _transform = transform;
                transform = null;
                Name = string.IsNullOrWhiteSpace(friendlyName)
                    ? $"Media Foundation H.264 {(isHardware ? "hardware" : "software")} decoder"
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

    private VideoFrame? DecodeWithCurrentTransform(EncodedVideoSample sample)
    {
        using var input = CreateInputSample(sample);
        _transform!.ProcessInput(0, input, 0);
        return DrainOutput(sample.Timestamp);
    }

    private static bool IsHardTransformFailure(Exception exception) =>
        exception is SharpGenException or COMException or InvalidOperationException;

    private void Reconfigure(int width, int height)
    {
        var hasTransportGeometry = HasKnownDimensions(width, height);

        if (_configured)
        {
            // A transport that knows the new size can force a fresh decoder immediately.
            // RTP does not carry dimensions, so dimensionless sessions stay on the same MFT
            // and let a new SPS trigger MF_E_TRANSFORM_STREAM_CHANGE instead.
            ReleaseTransform();
            SelectCandidate();
        }

        using var inputType = MediaFactory.MFCreateMediaType();
        if (hasTransportGeometry)
        {
            SetVideoTypeCommon(inputType, VideoFormatGuids.H264, width, height);
        }
        else
        {
            // Microsoft explicitly supports a partial H.264 input type. Feeding SPS/PPS then
            // makes the decoder expose the real output geometry through a stream change.
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        }

        _transform!.SetInputType(0, inputType, 0);

        _transportGeometryKnown = hasTransportGeometry;
        _visibleWidth = hasTransportGeometry ? width : 0;
        _visibleHeight = hasTransportGeometry ? height : 0;
        _codedWidth = hasTransportGeometry ? width : 0;
        _codedHeight = hasTransportGeometry ? height : 0;
        _stride = hasTransportGeometry ? width : 0;
        _outputBufferSize = 0;
        if (!hasTransportGeometry)
            _bgra = [];

        // With a partial H.264 input type Media Foundation initially exposes a placeholder
        // output type. Its geometry is intentionally ignored until SPS/PPS causes stream change.
        SelectNv12OutputType(requireGeometry: hasTransportGeometry);

        _transform.ProcessMessage(
            TMessageType.MessageNotifyBeginStreaming,
            UIntPtr.Zero);
        _transform.ProcessMessage(
            TMessageType.MessageNotifyStartOfStream,
            UIntPtr.Zero);
        _configured = true;
    }

    private void SelectNv12OutputType(bool requireGeometry)
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType available;
            try
            {
                available = _transform!.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException e) when (e.HResult == NoMoreTypesHResult)
            {
                throw new NotSupportedException(
                    "The selected Media Foundation H.264 decoder exposes no NV12 output.");
            }

            using (available)
            {
                if (available.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                    continue;

                _transform!.SetOutputType(0, available, 0);
                if (requireGeometry)
                    ReadOutputGeometry(available);
                return;
            }
        }
    }

    private void ReadOutputGeometry(IMFMediaType outputType)
    {
        MediaFactory.MFGetAttributeSize(
            outputType,
            MediaTypeAttributeKeys.FrameSize,
            out var codedWidth,
            out var codedHeight);

        _codedWidth = codedWidth > 0 ? checked((int)codedWidth) : _visibleWidth;
        _codedHeight = codedHeight > 0 ? checked((int)codedHeight) : _visibleHeight;

        if (_codedWidth <= 0 || _codedHeight <= 0)
        {
            throw new InvalidOperationException(
                "Media Foundation H.264 decoder output type did not expose a valid frame size.");
        }

        if (_transportGeometryKnown && _visibleWidth > 0 && _visibleHeight > 0)
        {
            _visibleWidth = Math.Min(_visibleWidth, _codedWidth);
            _visibleHeight = Math.Min(_visibleHeight, _codedHeight);
        }
        else if (TryReadDisplayAperture(outputType, out var displayWidth, out var displayHeight))
        {
            // H.264 coded dimensions are macroblock-aligned. For example, a 640x360 picture
            // can legitimately decode into a 640x368 NV12 surface. The display aperture is
            // the valid picture region; pixels outside it are padding and must not be shown.
            _visibleWidth = Math.Min(displayWidth, _codedWidth);
            _visibleHeight = Math.Min(displayHeight, _codedHeight);
        }
        else
        {
            _visibleWidth = _codedWidth;
            _visibleHeight = _codedHeight;
        }

        _stride = outputType.GetUInt32(
            MediaTypeAttributeKeys.DefaultStride,
            out var stride).Success && stride > 0
            ? checked((int)stride)
            : _codedWidth;

        var nv12Stride = Math.Max(_stride, _codedWidth);
        _outputBufferSize = checked(nv12Stride * _codedHeight * 3 / 2);

        var bgraSize = checked(_visibleWidth * _visibleHeight * 4);
        if (_bgra.Length != bgraSize)
            _bgra = new byte[bgraSize];
    }

    private static bool TryReadDisplayAperture(
        IMFMediaType outputType,
        out int width,
        out int height)
    {
        // Microsoft's display-area fallback order is minimum display aperture, then geometric
        // aperture, then the entire coded frame.
        if (TryReadVideoArea(
                outputType,
                MediaTypeAttributeKeys.MinimumDisplayAperture,
                out width,
                out height))
        {
            return true;
        }

        return TryReadVideoArea(
            outputType,
            MediaTypeAttributeKeys.GeometricAperture,
            out width,
            out height);
    }

    private static bool TryReadVideoArea(
        IMFMediaType outputType,
        Guid attribute,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;

        try
        {
            var blob = outputType.GetBlob(attribute);
            if (blob.Length < Marshal.SizeOf<MfVideoArea>())
                return false;

            var area = MemoryMarshal.Read<MfVideoArea>(blob);
            if (area.Width <= 0 || area.Height <= 0)
                return false;

            width = area.Width;
            height = area.Height;
            return true;
        }
        catch (SharpGenException)
        {
            // Attribute absence is normal. The caller falls back to the next aperture and,
            // finally, to MF_MT_FRAME_SIZE.
            return false;
        }
    }

    private VideoFrame? DrainOutput(TimeSpan timestamp)
    {
        VideoFrame? last = null;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var streamInfo = _transform!.GetOutputStreamInfo(0);
            var streamFlags = streamInfo.Flags;
            var providesSamples =
                (streamFlags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
            var canProvideSamples =
                (streamFlags & (int)OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0;
            var callerSuppliedSample =
                MediaFoundationOutputSampleLifetime.ShouldSupplyCallerSample(streamFlags);
            var allocationMode =
                MediaFoundationOutputSampleLifetime.ResolveAllocationMode(
                    streamFlags,
                    callerSuppliedSample);

            LogOutputSampleAllocation(
                streamFlags,
                providesSamples,
                canProvideSamples,
                allocationMode,
                callerSuppliedSample);

            IMFSample? callerAllocated = null;
            var output = new OutputDataBuffer
            {
                StreamID = 0,
                Status = 0,
                Sample = null!,
                Events = null!
            };

            try
            {
                if (callerSuppliedSample)
                {
                    callerAllocated = MediaFactory.MFCreateSample();
                    using var buffer = MediaFactory.MFCreateMemoryBuffer(
                        Math.Max(1, Math.Max(streamInfo.Size, _outputBufferSize)));
                    callerAllocated.AddBuffer(buffer);
                    output.Sample = callerAllocated;
                }

                var result = _transform.ProcessOutput(
                    ProcessOutputFlags.None,
                    1,
                    ref output,
                    out _);

                var processOutputCall = ++_processOutputCalls;
                if (processOutputCall == 1 ||
                    processOutputCall % 120 == 0 ||
                    result.Code == StreamChangeHResult ||
                    (result.Failure && result.Code != NeedMoreInputHResult))
                {
                    _logger.LogTrace(
                        "H.264 decoder ProcessOutput completed. call={Call} hresult=0x{HResult:X8} streamFlags=0x{OutputStreamFlags:X8} providesSamples={ProvidesSamples} canProvideSamples={CanProvideSamples} allocationMode={AllocationMode} callerSuppliedSample={CallerSuppliedSample} returnedSample={ReturnedSample} streamChange={StreamChange}",
                        processOutputCall,
                        result.Code,
                        streamFlags,
                        providesSamples,
                        canProvideSamples,
                        allocationMode,
                        callerSuppliedSample,
                        output.Sample is not null,
                        result.Code == StreamChangeHResult);
                }

                if (result.Code == NeedMoreInputHResult)
                    return last;

                if (result.Code == StreamChangeHResult)
                {
                    _logger.LogInformation(
                        "H.264 decoder reported output stream change. oldVisible={VisibleWidth}x{VisibleHeight} oldCoded={CodedWidth}x{CodedHeight}",
                        _visibleWidth,
                        _visibleHeight,
                        _codedWidth,
                        _codedHeight);

                    // SPS/PPS is authoritative for RTP. The fresh output type now carries the
                    // actual frame size (and may carry a new size later in the same session).
                    SelectNv12OutputType(requireGeometry: true);

                    _logger.LogInformation(
                        "H.264 decoder output stream change applied. newVisible={VisibleWidth}x{VisibleHeight} newCoded={CodedWidth}x{CodedHeight} stride={Stride}",
                        _visibleWidth,
                        _visibleHeight,
                        _codedWidth,
                        _codedHeight,
                        _stride);
                    continue;
                }

                if (result.Failure)
                {
                    LastFailure = $"process-output: HRESULT 0x{result.Code:X8}";
                    _logger.LogWarning(
                        "H.264 decoder ProcessOutput returned failure. hresult=0x{HResult:X8} visible={VisibleWidth}x{VisibleHeight} coded={CodedWidth}x{CodedHeight}",
                        result.Code,
                        _visibleWidth,
                        _visibleHeight,
                        _codedWidth,
                        _codedHeight);
                    return last;
                }

                var decodedSample =
                    MediaFoundationOutputSampleLifetime.SelectSampleForConversion(
                        allocationMode,
                        callerAllocated,
                        output.Sample);
                if (decodedSample is null)
                    return last;

                // Convert before cleanup. In caller-allocated mode, output.Sample may be a
                // second managed wrapper around the same native IMFSample reference.
                last = ConvertOutput(decodedSample, timestamp) ?? last;
            }
            finally
            {
                MediaFoundationOutputSampleLifetime.DisposeOwnedResources(
                    allocationMode,
                    callerAllocated,
                    output.Sample,
                    output.Events);
            }
        }

        return last;
    }

    private void LogOutputSampleAllocation(
        int streamFlags,
        bool providesSamples,
        bool canProvideSamples,
        OutputSampleAllocationMode allocationMode,
        bool callerSuppliedSample)
    {
        if (_lastOutputStreamFlags == streamFlags &&
            _lastOutputSampleAllocationMode == allocationMode)
        {
            return;
        }

        _lastOutputStreamFlags = streamFlags;
        _lastOutputSampleAllocationMode = allocationMode;

        _logger.LogDebug(
            "H.264 decoder output sample allocation selected. streamFlags=0x{OutputStreamFlags:X8} providesSamples={ProvidesSamples} canProvideSamples={CanProvideSamples} allocationMode={AllocationMode} callerSuppliedSample={CallerSuppliedSample}",
            streamFlags,
            providesSamples,
            canProvideSamples,
            allocationMode,
            callerSuppliedSample);
    }

    private VideoFrame? ConvertOutput(IMFSample sample, TimeSpan timestamp)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        var length = buffer.CurrentLength;
        if (length <= 0)
            return null;

        buffer.Lock(out var pointer, out _, out var lockedLength);
        try
        {
            var available = Math.Min(length, lockedLength);
            var required = checked(_stride * _codedHeight * 3 / 2);
            if (available < required)
                return null;

            ConvertNv12ToBgra(pointer, _stride, _codedHeight, _visibleWidth, _visibleHeight, _bgra);
            return new VideoFrame(
                _visibleWidth,
                _visibleHeight,
                _bgra.AsMemory(0, checked(_visibleWidth * _visibleHeight * 4)),
                timestamp);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static unsafe void ConvertNv12ToBgra(
        nint source,
        int stride,
        int codedHeight,
        int width,
        int height,
        byte[] destination)
    {
        var src = (byte*)source;
        var uvBase = src + checked(stride * codedHeight);

        fixed (byte* target = destination)
        {
            for (var y = 0; y < height; y++)
            {
                var yRow = src + checked(y * stride);
                var uvRow = uvBase + checked((y / 2) * stride);
                var outRow = target + checked(y * width * 4);

                for (var x = 0; x < width; x++)
                {
                    var yy = yRow[x];
                    var uv = x & ~1;
                    var u = uvRow[uv];
                    var v = uvRow[uv + 1];

                    var c = Math.Max(0, yy - 16);
                    var d = u - 128;
                    var e = v - 128;

                    var r = Clamp((298 * c + 409 * e + 128) >> 8);
                    var g = Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                    var b = Clamp((298 * c + 516 * d + 128) >> 8);

                    var offset = x * 4;
                    outRow[offset] = (byte)b;
                    outRow[offset + 1] = (byte)g;
                    outRow[offset + 2] = (byte)r;
                    outRow[offset + 3] = 255;
                }
            }
        }
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 255);

    private static IMFSample CreateInputSample(EncodedVideoSample sample)
    {
        var mediaSample = MediaFactory.MFCreateSample();
        IMFMediaBuffer? buffer = null;

        try
        {
            buffer = MediaFactory.MFCreateMemoryBuffer(sample.Data.Length);
            buffer.Lock(out var destination, out _, out _);
            try
            {
                Marshal.Copy(sample.Data.ToArray(), 0, destination, sample.Data.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = sample.Data.Length;
            mediaSample.AddBuffer(buffer);
            mediaSample.SampleTime = sample.Timestamp.Ticks;
            mediaSample.SampleDuration = TimeSpan.TicksPerSecond / 30;
            if (sample.IsKeyFrame)
                mediaSample.Set(SampleAttributeKeys.CleanPoint, 1u).CheckError();

            return mediaSample;
        }
        catch
        {
            mediaSample.Dispose();
            throw;
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    private static bool HasKnownDimensions(int width, int height)
    {
        if (width < 0 || height < 0 || (width == 0) != (height == 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "H.264 dimensions must either both be positive or both be zero when unknown.");
        }

        return width > 0;
    }

    private static void SetVideoTypeCommon(
        IMFMediaType mediaType,
        Guid subtype,
        int width,
        int height)
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
            30,
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
            return "Media Foundation H.264 decoder";
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
        if (_transform is null)
            return;

        try
        {
            if (_configured)
            {
                _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            }
        }
        catch
        {
            // Native teardown is best-effort; never leak COM objects over a shutdown error.
        }

        _transform.Dispose();
        _transform = null;
        _configured = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseTransform();
            _bgra = [];
            _runtimeLease.Dispose();
        }
    }
}
