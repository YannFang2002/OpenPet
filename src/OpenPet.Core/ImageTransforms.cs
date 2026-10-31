namespace OpenPet.Core.Imaging;

/// <summary>Clockwise rotation / mirroring of BGRA canvases, in place-free form.</summary>
public static class ImageTransforms
{
    /// <summary>Rotates clockwise by 0/90/180/270 degrees.</summary>
    public static BgraImage RotateClockwise(BgraImage source, int degrees)
    {
        ArgumentNullException.ThrowIfNull(source);

        switch (degrees)
        {
            case 0:
                return Clone(source);
            case 90:
                return Rotate90(source);
            case 180:
                return Rotate180(source);
            case 270:
                return Rotate270(source);
            default:
                throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Rotation must be 0, 90, 180 or 270.");
        }
    }

    public static BgraImage Flip(BgraImage source, FlipMode mode)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (mode == FlipMode.None)
        {
            return Clone(source);
        }

        bool horizontal = mode is FlipMode.Horizontal or FlipMode.Both;
        bool vertical = mode is FlipMode.Vertical or FlipMode.Both;
        var target = new BgraImage(source.Width, source.Height);

        for (int y = 0; y < source.Height; y++)
        {
            int sourceY = vertical ? source.Height - 1 - y : y;
            for (int x = 0; x < source.Width; x++)
            {
                int sourceX = horizontal ? source.Width - 1 - x : x;
                CopyPixel(source, sourceX, sourceY, target, x, y);
            }
        }

        return target;
    }

    /// <summary>Rotation followed by flip; matches how the probe exposes both flags.</summary>
    public static BgraImage RotateAndFlip(BgraImage source, int degrees, FlipMode flip)
        => Flip(RotateClockwise(source, degrees), flip);

    public static BgraImage Clone(BgraImage source)
    {
        var clone = new BgraImage(source.Width, source.Height);
        Array.Copy(source.Pixels, clone.Pixels, source.Pixels.Length);
        return clone;
    }

    private static BgraImage Rotate90(BgraImage source)
    {
        var target = new BgraImage(source.Height, source.Width); // width/height swap
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                // CW: src(x,y) -> dst(h-1-y, x)
                CopyPixel(source, x, y, target, source.Height - 1 - y, x);
            }
        }

        return target;
    }

    private static BgraImage Rotate180(BgraImage source)
    {
        var target = new BgraImage(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                CopyPixel(source, x, y, target, source.Width - 1 - x, source.Height - 1 - y);
            }
        }

        return target;
    }

    private static BgraImage Rotate270(BgraImage source)
    {
        var target = new BgraImage(source.Height, source.Width);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                // CCW: src(x,y) -> dst(y, w-1-x)
                CopyPixel(source, x, y, target, y, source.Width - 1 - x);
            }
        }

        return target;
    }

    private static void CopyPixel(BgraImage source, int sourceX, int sourceY, BgraImage target, int x, int y)
    {
        int from = (sourceY * source.Width + sourceX) * 4;
        int to = (y * target.Width + x) * 4;
        target.Pixels[to] = source.Pixels[from];
        target.Pixels[to + 1] = source.Pixels[from + 1];
        target.Pixels[to + 2] = source.Pixels[from + 2];
        target.Pixels[to + 3] = source.Pixels[from + 3];
    }
}
