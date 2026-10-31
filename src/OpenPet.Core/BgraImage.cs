namespace OpenPet.Core.Imaging;

/// <summary>Mirroring applied to the canvas before/after rotation.</summary>
public enum FlipMode
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Both = 3,
}

/// <summary>32-bit BGRA, top-down, tightly packed (stride = width * 4).</summary>
public sealed class BgraImage
{
    public BgraImage(int width, int height)
        : this(width, height, new byte[checked(width * height * 4)])
    {
    }

    public BgraImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException(
                $"Pixel buffer must be {width * height * 4} bytes for {width}x{height} BGRA, got {pixels.Length}.",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; }

    public void SetPixel(int x, int y, byte b, byte g, byte r, byte a = 255)
    {
        int offset = (y * Width + x) * 4;
        Pixels[offset] = b;
        Pixels[offset + 1] = g;
        Pixels[offset + 2] = r;
        Pixels[offset + 3] = a;
    }

    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        int offset = (y * Width + x) * 4;
        return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]);
    }
}
