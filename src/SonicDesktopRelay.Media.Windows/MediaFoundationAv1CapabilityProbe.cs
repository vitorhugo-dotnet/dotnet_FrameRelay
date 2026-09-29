using System.Runtime.Versioning;
using SharpGen.Runtime;
using SonicDesktopRelay.Media;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>Probes hardware Media Foundation transforms for the AV1 Main profile path.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MediaFoundationAv1CapabilityProbe
{
    // MFVideoFormat_AV1 is the documented MF subtype for the AV01 FOURCC.
    private static readonly Guid Av1Subtype = new("31305641-0000-0010-8000-00AA00389B71");
    private const uint HardwareFlags = 0x00000004 | 0x00000040;
    private const int ProbeWidth = 640;
    private const int ProbeHeight = 360;
    private const int ProbeFps = 30;
    // 640x360 at 30 fps requires AV1 seq_level_idx 1. Keep the capability contract bounded to
    // the workload exercised by this probe because MFTs do not expose a maximum level API.
    private const int ProbedMaximumLevel = 1;
    private const int NoMoreTypesHResult = unchecked((int)0xC00D36B9);

    public VideoCodecCapabilities Detect() => Detect(new NativeTransformCatalog());

    internal VideoCodecCapabilities Detect(IAv1TransformCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        using var lease = MediaFoundationRuntime.Shared.Acquire();
        var encoders = ProbeDirection(catalog.Enumerate(Av1TransformDirection.Encoder), Av1TransformDirection.Encoder);
        var decoders = ProbeDirection(catalog.Enumerate(Av1TransformDirection.Decoder), Av1TransformDirection.Decoder);

        var encoderSet = encoders.Any(static result => result.IsUsable)
            ? (IReadOnlySet<VideoCodec>)new HashSet<VideoCodec> { VideoCodec.Av1 }
            : new HashSet<VideoCodec>();
        var decoderSet = decoders.Any(static result => result.IsUsable)
            ? (IReadOnlySet<VideoCodec>)new HashSet<VideoCodec> { VideoCodec.Av1 }
            : new HashSet<VideoCodec>();
        var encoderConstraints = encoderSet.Count == 0
            ? new Dictionary<VideoCodec, VideoCodecConstraints>()
            : new Dictionary<VideoCodec, VideoCodecConstraints>
            {
                [VideoCodec.Av1] = new("0", ProbedMaximumLevel)
            };
        var decoderConstraints = decoderSet.Count == 0
            ? new Dictionary<VideoCodec, VideoCodecConstraints>()
            : new Dictionary<VideoCodec, VideoCodecConstraints>
            {
                [VideoCodec.Av1] = new("0", ProbedMaximumLevel)
            };
        var failed = encoders.Concat(decoders)
            .Where(static result => !result.IsUsable)
            .Select(static result => result.Reason)
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .ToArray();
        var rejections = failed.Length == 0
            ? new Dictionary<VideoCodec, string>()
            : new Dictionary<VideoCodec, string> { [VideoCodec.Av1] = string.Join(" | ", failed) };

        return new VideoCodecCapabilities(
            encoderSet,
            decoderSet,
            encoderConstraints,
            decoderConstraints,
            rejections);
    }

    private static IReadOnlyList<Av1TransformProbeResult> ProbeDirection(
        IEnumerable<IAv1TransformCandidate> candidates,
        Av1TransformDirection direction)
    {
        var results = new List<Av1TransformProbeResult>();
        foreach (var candidate in candidates)
        {
            using (candidate)
            {
                if (!candidate.IsHardware)
                {
                    results.Add(new(direction, candidate.Name, false, false, "software-transform-rejected"));
                    continue;
                }

                try
                {
                    candidate.ActivateAndConfigure(Av1Subtype, ProbeWidth, ProbeHeight, ProbeFps);
                    results.Add(new(direction, candidate.Name, true, true, string.Empty));
                }
                catch (Exception exception) when (
                    exception is SharpGenException or InvalidOperationException or NotSupportedException
                        or System.Runtime.InteropServices.COMException or ArgumentException)
                {
                    results.Add(new(direction, candidate.Name, true, false,
                        $"{candidate.Name}: activation/configuration failed: {exception.Message}"));
                }
            }
        }
        if (results.Count == 0)
        {
            var role = direction == Av1TransformDirection.Encoder ? "encoder" : "decoder";
            results.Add(new(direction, $"hardware AV1 {role} enumeration", false, false,
                $"MFTEnumEx returned no hardware AV1 {role} activation."));
        }
        return results;
    }

    private sealed class NativeTransformCatalog : IAv1TransformCatalog
    {
        public IEnumerable<IAv1TransformCandidate> Enumerate(Av1TransformDirection direction)
        {
            using var activations = MediaFactory.MFTEnumEx(
                direction == Av1TransformDirection.Encoder
                    ? TransformCategoryGuids.VideoEncoder
                    : TransformCategoryGuids.VideoDecoder,
                HardwareFlags,
                direction == Av1TransformDirection.Decoder ? RegistrationType(Av1Subtype) : null,
                direction == Av1TransformDirection.Encoder ? RegistrationType(Av1Subtype) : null);

            foreach (var activation in activations)
            {
                var name = ReadName(activation);
                var clsid = ReadClsid(activation);
                // Transfer activation ownership to the candidate; the activation list's COM
                // collection still owns its reference and is disposed after the iteration.
                yield return new NativeCandidate(activation, name, clsid, direction);
            }
        }

        private static RegisterTypeInfo RegistrationType(Guid subtype) => new()
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = subtype
        };

        private static string ReadName(IMFActivate activation)
        {
            try { return activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); }
            catch { return "AV1 hardware transform"; }
        }

        private static Guid ReadClsid(IMFActivate activation)
        {
            try { return activation.GetGUID(TransformAttributeKeys.MftTransformClsidAttribute); }
            catch { return Guid.Empty; }
        }
    }

    private sealed class NativeCandidate(
        IMFActivate activation,
        string name,
        Guid clsid,
        Av1TransformDirection direction) : IAv1TransformCandidate
    {
        private IMFActivate? _activation = activation;
        public string Name { get; } = name;
        public bool IsHardware => true; // MFT_ENUM_FLAG_HARDWARE restricted this enumeration.

        public void ActivateAndConfigure(Guid av1Subtype, int width, int height, int fps)
        {
            var source = _activation ?? throw new ObjectDisposedException(nameof(NativeCandidate));
            using var transform = source.ActivateObject<IMFTransform>();
            if (clsid == Guid.Empty)
                throw new NotSupportedException("Activation did not expose an MFT CLSID.");

            using (var attributes = transform.Attributes)
            {
                var isAsync = attributes.GetUInt32(
                    TransformAttributeKeys.TransformAsync, out var asyncValue).Success
                    && asyncValue != 0;
                if (isAsync && direction == Av1TransformDirection.Decoder)
                {
                    throw new NotSupportedException(
                        "asynchronous AV1 decoder is rejected because this decoder path has no async event pump");
                }
                if (isAsync)
                {
                    attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true).CheckError();
                }
            }

            if (source is null) throw new InvalidOperationException("MFT activation was unavailable.");
            if (av1Subtype == Guid.Empty) throw new ArgumentException("AV1 subtype cannot be empty.", nameof(av1Subtype));
            if (width <= 0 || height <= 0 || fps <= 0) throw new ArgumentOutOfRangeException(nameof(width));

            // Configure the actual stream contract. Encoder output is set before NV12 input;
            // decoder input is set before selecting a concrete NV12 output type.
            if (direction == Av1TransformDirection.Encoder)
            {
                using var output = MediaFactory.MFCreateMediaType();
                SetVideoType(output, av1Subtype, width, height, fps);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, 1_500_000u).CheckError();
                output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 0u).CheckError();
                transform.SetOutputType(0, output, 0);
                using var input = MediaFactory.MFCreateMediaType();
                SetVideoType(input, VideoFormatGuids.NV12, width, height, fps);
                transform.SetInputType(0, input, 0);
            }
            else
            {
                using var input = MediaFactory.MFCreateMediaType();
                SetVideoType(input, av1Subtype, width, height, fps);
                input.Set(MediaTypeAttributeKeys.Mpeg2Profile, 0u).CheckError();
                transform.SetInputType(0, input, 0);
                for (var index = 0; ; index++)
                {
                    IMFMediaType available;
                    try { available = transform.GetOutputAvailableType(0, index); }
                    catch (SharpGenException exception) when (exception.HResult == NoMoreTypesHResult)
                    {
                        throw new NotSupportedException("AV1 decoder exposes no output type.", exception);
                    }
                    using (available)
                    {
                        if (available.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                            continue;
                        transform.SetOutputType(0, available, 0);
                        return;
                    }
                }
            }
        }

        private static void SetVideoType(IMFMediaType mediaType, Guid subtype, int width, int height, int fps)
        {
            mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
            MediaFactory.MFSetAttributeSize(mediaType, MediaTypeAttributeKeys.FrameSize,
                checked((uint)width), checked((uint)height)).CheckError();
            MediaFactory.MFSetAttributeRatio(mediaType, MediaTypeAttributeKeys.FrameRate,
                checked((uint)fps), 1).CheckError();
            MediaFactory.MFSetAttributeRatio(mediaType, MediaTypeAttributeKeys.PixelAspectRatio,
                1, 1).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
        }

        public void Dispose() => Interlocked.Exchange(ref _activation, null)?.Dispose();
    }
}

internal enum Av1TransformDirection { Encoder, Decoder }

internal sealed record Av1TransformProbeResult(
    Av1TransformDirection Direction, string Name, bool IsHardware, bool IsUsable, string Reason);

internal interface IAv1TransformCatalog
{
    IEnumerable<IAv1TransformCandidate> Enumerate(Av1TransformDirection direction);
}

internal interface IAv1TransformCandidate : IDisposable
{
    string Name { get; }
    bool IsHardware { get; }
    void ActivateAndConfigure(Guid av1Subtype, int width, int height, int fps);
}
