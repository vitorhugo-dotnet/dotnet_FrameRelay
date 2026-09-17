using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    private readonly IDisposable _runtimeLease;
    private readonly Lock _gate = new();
    private readonly List<string> _rejections = [];

    private IMFTransform? _transform;
    private int _visibleWidth;
    private int _visibleHeight;
    private int _codedWidth;
    private int _codedHeight;
    private int _stride;
    private int _outputBufferSize;
    private byte[] _bgra = [];
    private bool _configured;
    private bool _disposed;

    public MediaFoundationH264Decoder()
    {
        _runtimeLease = MediaFoundationRuntime.Shared.Acquire();

        try
        {
            SelectCandidate();
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

    public string? LastFailure { get; private set; }

    public VideoFrame? Decode(EncodedVideoSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (sample.Data.IsEmpty)
                return null;

            LastFailure = null;

            try
            {
                if (!_configured || sample.Width != _visibleWidth || sample.Height != _visibleHeight)
                    Reconfigure(sample.Width, sample.Height);

                using var input = CreateInputSample(sample);
                _transform!.ProcessInput(0, input, 0);
                return DrainOutput(sample.Timestamp);
            }
            catch (Exception e) when (
                e is SharpGenException
                    or COMException
                    or InvalidOperationException
                    or ArgumentException)
            {
                LastFailure = $"{e.GetType().Name}: {e.Message}";
                // Packet loss/corruption is normal network weather. Keep the decoder alive and
                // wait for the next clean access unit/keyframe rather than killing the viewer.
                return null;
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

                var clsid = ReadClsid(activation);
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

    private void Reconfigure(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (_configured)
        {
            // Resolution changes arrive on a keyframe from the publisher. A fresh transform
            // avoids carrying stale reference surfaces or coded geometry across the boundary.
            ReleaseTransform();
            SelectCandidate();
        }

        using var inputType = MediaFactory.MFCreateMediaType();
        SetVideoTypeCommon(inputType, VideoFormatGuids.H264, width, height);
        _transform!.SetInputType(0, inputType, 0);

        _visibleWidth = width;
        _visibleHeight = height;
        _codedWidth = width;
        _codedHeight = height;
        _stride = width;

        SelectNv12OutputType();

        _transform.ProcessMessage(
            TMessageType.MessageNotifyBeginStreaming,
            UIntPtr.Zero);
        _transform.ProcessMessage(
            TMessageType.MessageNotifyStartOfStream,
            UIntPtr.Zero);
        _configured = true;
    }

    private void SelectNv12OutputType()
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

        _visibleWidth = Math.Min(_visibleWidth, _codedWidth);
        _visibleHeight = Math.Min(_visibleHeight, _codedHeight);

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

    private VideoFrame? DrainOutput(TimeSpan timestamp)
    {
        VideoFrame? last = null;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var streamInfo = _transform!.GetOutputStreamInfo(0);
            var providesSamples =
                (streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;

            IMFSample? allocated = null;
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
                    allocated = MediaFactory.MFCreateSample();
                    using var buffer = MediaFactory.MFCreateMemoryBuffer(
                        Math.Max(streamInfo.Size, _outputBufferSize));
                    allocated.AddBuffer(buffer);
                    output.Sample = allocated;
                }

                var result = _transform.ProcessOutput(
                    ProcessOutputFlags.None,
                    1,
                    ref output,
                    out _);

                if (result.Code == NeedMoreInputHResult)
                    return last;

                if (result.Code == StreamChangeHResult)
                {
                    SelectNv12OutputType();
                    continue;
                }

                if (result.Failure)
                {
                    LastFailure = $"ProcessOutput failed: 0x{result.Code:X8}";
                    return last;
                }

                var decodedSample = output.Sample ?? allocated;
                if (decodedSample is null)
                    return last;

                last = ConvertOutput(decodedSample, timestamp) ?? last;
            }
            finally
            {
                output.Events?.Dispose();

                if (output.Sample is not null && !ReferenceEquals(output.Sample, allocated))
                    output.Sample.Dispose();

                allocated?.Dispose();
            }
        }

        return last;
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
