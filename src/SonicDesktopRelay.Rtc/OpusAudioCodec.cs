using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// Stateful Opus adapter for the WebRTC audio stream. The transport and device layers only see
/// project media contracts; Concentus stays contained here.
/// </summary>
public sealed class OpusAudioCodec : IAudioEncoder, IAudioDecoder
{
    private const int SampleRate = 48_000;
    private const int MaxPacketBytes = 1_275;
    private const int MaxDecodeSamplesPerChannel = 5_760;

    private readonly int _channels;
    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;
    private readonly short[] _encodePcm;
    private readonly short[] _decodePcm;
    private readonly byte[] _packetBuffer = new byte[MaxPacketBytes];
    private bool _disposed;

    public OpusAudioCodec(int channels = 2)
    {
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "Opus WebRTC audio supports mono or stereo here.");

        _channels = channels;
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, channels);
        _encoder.Bitrate = channels == 2 ? 128_000 : 64_000;
        _encoder.UseVBR = true;

        // 60 ms is the largest frame accepted by the encoder; the decoder buffer is 120 ms so
        // it can safely decode any legal Opus packet even if a future sender bundles frames.
        _encodePcm = new short[2_880 * channels];
        _decodePcm = new short[MaxDecodeSamplesPerChannel * channels];
    }

    public string Name => $"Concentus Opus 48 kHz {(_channels == 2 ? "stereo" : "mono")}";

    public EncodedAudioSample? Encode(AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateFrame(frame);
        if (frame.SampleCount == 0) return null;

        var expectedBytes = checked(frame.SampleCount * _channels * sizeof(short));
        if (frame.Data.Length < expectedBytes)
            throw new ArgumentException("PCM payload is shorter than its declared sample count.", nameof(frame));

        var input = frame.Data.Span;
        var sampleTotal = frame.SampleCount * _channels;
        for (var i = 0; i < sampleTotal; i++)
            _encodePcm[i] = BinaryPrimitives.ReadInt16LittleEndian(input.Slice(i * sizeof(short), sizeof(short)));

        var packetLength = _encoder.Encode(
            _encodePcm.AsSpan(0, sampleTotal),
            frame.SampleCount,
            _packetBuffer,
            MaxPacketBytes);

        if (packetLength <= 0) return null;

        var packet = _packetBuffer.AsSpan(0, packetLength).ToArray();
        return new EncodedAudioSample(packet, frame.SampleCount, frame.Duration, frame.Timestamp);
    }

    public AudioFrame? Decode(EncodedAudioSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sample.Data.IsEmpty) return null;

        var decodedSamples = _decoder.Decode(
            sample.Data.Span,
            _decodePcm,
            MaxDecodeSamplesPerChannel,
            decode_fec: false);

        if (decodedSamples <= 0) return null;

        var sampleTotal = checked(decodedSamples * _channels);
        var pcm = new byte[checked(sampleTotal * sizeof(short))];
        for (var i = 0; i < sampleTotal; i++)
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(i * sizeof(short), sizeof(short)),
                _decodePcm[i]);

        return new AudioFrame(pcm, SampleRate, _channels, decodedSamples, sample.Timestamp);
    }

    private void ValidateFrame(AudioFrame frame)
    {
        if (frame.SampleRate != SampleRate)
            throw new ArgumentException($"Opus input must be {SampleRate} Hz.", nameof(frame));
        if (frame.Channels != _channels)
            throw new ArgumentException($"Opus input must contain {_channels} channel(s).", nameof(frame));

        if (frame.SampleCount is not (0 or 120 or 240 or 480 or 960 or 1920 or 2880))
            throw new ArgumentException("Invalid Opus frame duration for 48 kHz input.", nameof(frame));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
