using System.Diagnostics;
using System.Text.Json;
using System.Runtime.Versioning;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using Xunit.Abstractions;

namespace SonicDesktopRelay.Media.Windows.Tests;

/// <summary>
/// Opt-in spot comparison of the local hardware AV1 and H.264 encoders. This deliberately
/// measures the public Encode call and emitted samples, not the full screen-sharing path.
/// </summary>
public sealed class MediaFoundationCodecBenchmarkTests(ITestOutputHelper output)
{
    private const int Width = 640;
    private const int Height = 360;
    private const int FramesPerSecond = 30;
    private const int TargetBitsPerSecond = 1_500_000;
    private const int WarmupFrames = 5;
    private const int MeasuredFrames = 30;
    private const int DrainFrames = 5;

    [MediaFoundationCodecBenchmarkFact]
    [Trait("Category", "Performance")]
    public void Compare_matching_AV1_and_H264_hardware_encoder_workloads()
    {
        var quality = new VideoQuality(Height, FramesPerSecond, TargetBitsPerSecond);
        var av1 = Measure(
            "AV1",
            () => new MediaFoundationAv1Encoder(),
            encoder => encoder.Diagnostics,
            (encoder, frame) => encoder.Encode(frame, quality));
        var h264 = Measure(
            "H.264",
            () => new MediaFoundationH264Encoder(),
            encoder => encoder.Diagnostics,
            (encoder, frame) => encoder.Encode(frame, quality));

        var report = new
        {
            workload = new
            {
                width = Width,
                height = Height,
                fps = FramesPerSecond,
                targetBitsPerSecond = TargetBitsPerSecond,
                equalTargetBitrate = true,
                warmupFrames = WarmupFrames,
                measuredFrames = MeasuredFrames,
                drainFrames = DrainFrames,
                order = new[] { "AV1", "H.264" },
                pixelPattern = "For each pixel (x,y) and frame n: B=(x+n) mod 256, "
                    + "G=(2y+n) mod 256, R=((x XOR y)+n) mod 256, A=255.",
                frameTimestamp = "frameIndex * TimeSpan.TicksPerSecond / 30",
                profile = new { av1 = "Main, profile 0", h264 = "Baseline, profile 66" }
            },
            statistics = new
            {
                mean = "arithmetic mean of measured Encode() call milliseconds",
                median = "middle sorted observation; mean of the two middle values when count is even",
                p95 = "nearest rank: sorted observation at one-based rank ceil(0.95 * N)",
                maximum = "largest measured Encode() call duration",
                encodedOutputBitrate = "8 * sum(selected sample bytes) / sum(selected sample durations)"
            },
            av1,
            h264
        };

        output.WriteLine("AV1_H264_NATIVE_CODEC_BENCHMARK_JSON_BEGIN");
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine("AV1_H264_NATIVE_CODEC_BENCHMARK_JSON_END");
    }

    private static object Measure<TEncoder>(
        string codec,
        Func<TEncoder> create,
        Func<TEncoder, NativeVideoDiagnostics> getDiagnostics,
        Func<TEncoder, VideoFrame, EncodedVideoSample?> encode)
        where TEncoder : IDisposable
    {
        var constructionWatch = Stopwatch.StartNew();
        using var encoder = create();
        constructionWatch.Stop();

        var pixels = new byte[checked(Width * Height * 4)];
        var callMilliseconds = new List<double>(MeasuredFrames);
        var outputSamples = new List<EncodedVideoSample>();
        var nullOutputCalls = 0;
        var totalCalls = WarmupFrames + MeasuredFrames + DrainFrames;

        for (var frameIndex = 0; frameIndex < totalCalls; frameIndex++)
        {
            FillFrame(pixels, frameIndex);
            var frame = new VideoFrame(
                Width,
                Height,
                pixels,
                TimeSpan.FromTicks((long)frameIndex * TimeSpan.TicksPerSecond / FramesPerSecond));

            var callWatch = Stopwatch.StartNew();
            var encoded = encode(encoder, frame);
            callWatch.Stop();

            if (encoded is { } sample)
                outputSamples.Add(sample);
            else
                nullOutputCalls++;

            if (frameIndex >= WarmupFrames && frameIndex < WarmupFrames + MeasuredFrames)
                callMilliseconds.Add(callWatch.Elapsed.TotalMilliseconds);
        }

        var measuredStart = TimeSpan.FromTicks(
            (long)WarmupFrames * TimeSpan.TicksPerSecond / FramesPerSecond);
        var measuredEnd = TimeSpan.FromTicks(
            (long)(WarmupFrames + MeasuredFrames) * TimeSpan.TicksPerSecond / FramesPerSecond);
        var measuredSamples = outputSamples
            .Where(sample => sample.Timestamp >= measuredStart && sample.Timestamp < measuredEnd)
            .ToArray();
        var sortedCalls = callMilliseconds.Order().ToArray();
        var expectedMeasuredTimestamps = Enumerable.Range(WarmupFrames, MeasuredFrames)
            .Select(frameIndex => TimeSpan.FromTicks(
                (long)frameIndex * TimeSpan.TicksPerSecond / FramesPerSecond).Ticks)
            .ToHashSet();
        var observedMeasuredTimestamps = measuredSamples
            .Select(sample => sample.Timestamp.Ticks)
            .ToHashSet();
        var encodedBytes = measuredSamples.Sum(sample => (long)sample.Data.Length);
        var encodedDurationSeconds = measuredSamples.Sum(sample => sample.Duration.TotalSeconds);
        var diagnostics = getDiagnostics(encoder);

        return new
        {
            codec,
            transform = diagnostics.TransformName,
            transformClsid = diagnostics.TransformClsid,
            hardware = diagnostics.IsHardware,
            profile = codec == "AV1" ? "Main, profile 0" : "Baseline, profile 66",
            width = diagnostics.Width,
            height = diagnostics.Height,
            fps = diagnostics.FramesPerSecond,
            configuredTargetBitsPerSecond = diagnostics.Bitrate,
            constructionMilliseconds = constructionWatch.Elapsed.TotalMilliseconds,
            measuredEncodeCallCount = sortedCalls.Length,
            encodeCallMilliseconds = new
            {
                mean = sortedCalls.Average(),
                median = Median(sortedCalls),
                p95NearestRank = NearestRank(sortedCalls, 0.95),
                maximum = sortedCalls[^1]
            },
            measuredOutputSampleCount = measuredSamples.Length,
            measuredOutputBytes = encodedBytes,
            measuredOutputDurationSeconds = encodedDurationSeconds,
            encodedOutputBitsPerSecond = encodedDurationSeconds > 0
                ? encodedBytes * 8d / encodedDurationSeconds
                : (double?)null,
            nullOutputCallsAcrossWarmupMeasurementAndDrain = nullOutputCalls,
            missingReturnedSampleTimestampsInMeasuredWindow = expectedMeasuredTimestamps
                .Count(timestamp => !observedMeasuredTimestamps.Contains(timestamp))
        };
    }

    private static double Median(double[] sortedValues)
    {
        var middle = sortedValues.Length / 2;
        return sortedValues.Length % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2d
            : sortedValues[middle];
    }

    private static double NearestRank(double[] sortedValues, double percentile)
    {
        var oneBasedRank = (int)Math.Ceiling(percentile * sortedValues.Length);
        return sortedValues[oneBasedRank - 1];
    }

    private static void FillFrame(byte[] bgra, int frameIndex)
    {
        var offset = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            bgra[offset++] = (byte)(x + frameIndex);
            bgra[offset++] = (byte)(2 * y + frameIndex);
            bgra[offset++] = (byte)((x ^ y) + frameIndex);
            bgra[offset++] = byte.MaxValue;
        }
    }
}

/// <summary>Runs only when explicitly opted in and both configured encoders use hardware MFTs.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MediaFoundationCodecBenchmarkFactAttribute : FactAttribute
{
    private const string OptInVariable = "SONICDESKTOPRELAY_RUN_NATIVE_CODEC_BENCHMARK";

    public MediaFoundationCodecBenchmarkFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal))
        {
            Skip = $"Opt-in benchmark. Set {OptInVariable}=1 to run it.";
            return;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Skip = "The Media Foundation native codec benchmark requires Windows 10 version 2004 or later.";
            return;
        }

        try
        {
            using var av1 = new MediaFoundationAv1Encoder();
            if (!av1.Diagnostics.IsHardware)
            {
                Skip = "No usable hardware AV1 encoder MFT is available.";
                return;
            }

            using var h264 = new MediaFoundationH264Encoder();
            if (!h264.Diagnostics.IsHardware)
                Skip = "No usable hardware H.264 encoder MFT is available; software fallback is excluded.";
        }
        catch (Exception exception)
        {
            Skip = $"Required hardware encoder MFT unavailable: {exception.GetType().Name}: {exception.Message}";
        }
    }
}
