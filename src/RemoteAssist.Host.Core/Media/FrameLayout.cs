namespace RemoteAssist.Host.Core.Media;

public static class FrameLayout
{
    public static void ValidateDimensions(int width, int height)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height * 4 > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(width), "프레임 크기가 지원 범위를 벗어났다.");
    }
    public static void Validate(ReadOnlyMemory<byte> pixels, int width, int height, int stride)
    {
        ValidateDimensions(width, height);
        if (stride < checked(width * 4) || (long)stride * height > pixels.Length) throw new ArgumentException("잘린 BGRA 버퍼 또는 stride다.");
    }
    public static byte[] Pack(ReadOnlyMemory<byte> pixels, int width, int height, int stride)
    {
        Validate(pixels, width, height, stride);
        var packed = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++) pixels.Span.Slice(y * stride, width * 4).CopyTo(packed.AsSpan(y * width * 4));
        return packed;
    }
    public static void Rotate(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int rotation)
    {
        ValidateDimensions(width, height);
        if (source.Length != checked(width * height * 4) || destination.Length != source.Length || source.Overlaps(destination)) throw new ArgumentException("잘못된 회전 버퍼다.");
        if (rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotation));
        var outputWidth = rotation is 90 or 270 ? height : width;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var (dx, dy) = rotation switch { 90 => (height - 1 - y, x), 180 => (width - 1 - x, height - 1 - y), 270 => (y, width - 1 - x), _ => (x, y) };
            source.Slice((y * width + x) * 4, 4).CopyTo(destination.Slice((dy * outputWidth + dx) * 4, 4));
        }
    }
}
