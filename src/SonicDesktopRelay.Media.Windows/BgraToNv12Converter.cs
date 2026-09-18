using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Windows;

internal readonly record struct Nv12Frame(byte[] Buffer, int Length, int Width, int Height);

internal sealed class BgraToNv12Converter : IDisposable
{
    private byte[] _buffer = [];
    private int _width;
    private int _height;
    private bool _disposed;

    internal Nv12Frame Convert(VideoFrame frame) => Convert(frame, frame.Width, frame.Height);

    internal Nv12Frame Convert(VideoFrame frame, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0 || (frame.Width & 1) != 0 || (frame.Height & 1) != 0)
            throw new ArgumentException("NV12 requires positive even source dimensions.", nameof(frame));
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("NV12 requires positive even target dimensions.", nameof(width));

        var sourceBytes = checked(frame.Width * frame.Height * 4);
        if (frame.Bgra.Length < sourceBytes)
            throw new ArgumentException("BGRA payload is shorter than the declared dimensions.", nameof(frame));

        EnsureBuffer(width, height);
        var source = frame.Bgra.Span;
        var yPlaneLength = checked(width * height);

        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(frame.Height - 1, y * frame.Height / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(frame.Width - 1, x * frame.Width / width);
                ReadPixel(source, frame.Width, sourceX, sourceY, out var r, out var g, out var b);
                _buffer[y * width + x] = ToY(r, g, b);
            }
        }

        var uv = yPlaneLength;
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                var uSum = 0;
                var vSum = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    var sourceY = Math.Min(frame.Height - 1, (y + dy) * frame.Height / height);
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var sourceX = Math.Min(frame.Width - 1, (x + dx) * frame.Width / width);
                        ReadPixel(source, frame.Width, sourceX, sourceY, out var r, out var g, out var b);
                        uSum += ToU(r, g, b);
                        vSum += ToV(r, g, b);
                    }
                }
                _buffer[uv++] = (byte)((uSum + 2) / 4);
                _buffer[uv++] = (byte)((vSum + 2) / 4);
            }
        }

        return new Nv12Frame(_buffer, checked(yPlaneLength * 3 / 2), width, height);
    }

    private static void ReadPixel(ReadOnlySpan<byte> source, int sourceWidth, int x, int y,
        out int r, out int g, out int b)
    {
        var index = (y * sourceWidth + x) * 4;
        b = source[index];
        g = source[index + 1];
        r = source[index + 2];
    }

    private void EnsureBuffer(int width, int height)
    {
        var required = checked(width * height * 3 / 2);
        if (_buffer.Length != required || _width != width || _height != height)
            _buffer = new byte[required];
        _width = width;
        _height = height;
    }

    private static byte ToY(int r, int g, int b) => Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
    private static byte ToU(int r, int g, int b) => Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
    private static byte ToV(int r, int g, int b) => Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    public void Dispose()
    {
        _disposed = true;
        _buffer = [];
        _width = _height = 0;
    }
}
