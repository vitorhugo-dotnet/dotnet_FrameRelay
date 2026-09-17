using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Windows;

internal readonly record struct Nv12Frame(byte[] Buffer, int Length, int Width, int Height);

internal sealed class BgraToNv12Converter : IDisposable
{
    private byte[] _buffer = [];
    private int _width;
    private int _height;
    private bool _disposed;

    internal Nv12Frame Convert(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0 || (frame.Width & 1) != 0 || (frame.Height & 1) != 0)
            throw new ArgumentException("NV12 requires positive even frame dimensions.", nameof(frame));

        var sourceBytes = checked(frame.Width * frame.Height * 4);
        if (frame.Bgra.Length < sourceBytes)
            throw new ArgumentException("BGRA payload is shorter than the declared dimensions.", nameof(frame));

        EnsureBuffer(frame.Width, frame.Height);
        var source = frame.Bgra.Span;
        var yPlaneLength = checked(frame.Width * frame.Height);

        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var index = (y * frame.Width + x) * 4;
                var b = source[index];
                var g = source[index + 1];
                var r = source[index + 2];
                _buffer[y * frame.Width + x] = ToY(r, g, b);
            }
        }

        var uv = yPlaneLength;
        for (var y = 0; y < frame.Height; y += 2)
        {
            for (var x = 0; x < frame.Width; x += 2)
            {
                var uSum = 0;
                var vSum = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var index = ((y + dy) * frame.Width + x + dx) * 4;
                        var b = source[index];
                        var g = source[index + 1];
                        var r = source[index + 2];
                        uSum += ToU(r, g, b);
                        vSum += ToV(r, g, b);
                    }
                }
                _buffer[uv++] = (byte)((uSum + 2) / 4);
                _buffer[uv++] = (byte)((vSum + 2) / 4);
            }
        }

        return new Nv12Frame(_buffer, checked(yPlaneLength * 3 / 2), frame.Width, frame.Height);
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
