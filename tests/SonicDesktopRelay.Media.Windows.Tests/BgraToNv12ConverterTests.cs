using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class BgraToNv12ConverterTests
{
    [Fact]
    public void Two_by_two_black_frame_has_expected_nv12_layout()
    {
        using var converter = new BgraToNv12Converter();
        var frame = Frame(2, 2, 0, 0, 0);

        var converted = converter.Convert(frame);

        Assert.Equal(6, converted.Length);
        Assert.Equal(new byte[] {16,16,16,16,128,128}, converted.Buffer.AsSpan(0, converted.Length).ToArray());
    }

    [Fact]
    public void White_pixels_use_limited_range_luma_and_neutral_chroma()
    {
        using var converter = new BgraToNv12Converter();
        var frame = Frame(2, 2, 255, 255, 255);

        var converted = converter.Convert(frame);

        Assert.Equal(new byte[] {235,235,235,235,128,128}, converted.Buffer.AsSpan(0, converted.Length).ToArray());
    }

    [Fact]
    public void Nv12_buffer_is_reused_for_same_dimensions()
    {
        using var converter = new BgraToNv12Converter();

        var first = converter.Convert(Frame(4, 2, 10, 20, 30));
        var second = converter.Convert(Frame(4, 2, 30, 20, 10));

        Assert.Same(first.Buffer, second.Buffer);
        Assert.Equal(12, second.Length);
    }

    [Fact]
    public void Odd_dimensions_are_rejected()
    {
        using var converter = new BgraToNv12Converter();

        Assert.Throws<ArgumentException>(() => { converter.Convert(Frame(3, 2, 0, 0, 0)); });
    }

    private static VideoFrame Frame(int width, int height, byte b, byte g, byte r)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = b;
            data[i + 1] = g;
            data[i + 2] = r;
            data[i + 3] = 255;
        }
        return new VideoFrame(width, height, data, TimeSpan.Zero);
    }
}
