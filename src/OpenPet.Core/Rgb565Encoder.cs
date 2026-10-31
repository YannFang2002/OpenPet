using System.Buffers.Binary;
using OpenPet.Core.Imaging;

namespace OpenPet.Core.Encoding;

/// <summary>
/// BGRA to RGB565 conversion, both byte orders. Optional ordered dithering hides
/// the banding a 5-6-5 gradient otherwise shows on the panel.
/// </summary>
public static class Rgb565Encoder
{
    // 4x4 Bayer matrix, values 0..15.
    private static readonly int[,] Bayer4 =
    {
        { 0, 8, 2, 10 },
        { 12, 4, 14, 6 },
        { 3, 11, 1, 9 },
        { 15, 7, 13, 5 },
    };

    public static byte[] Encode(BgraImage image, Rgb565ByteOrder byteOrder, bool dither = true)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] pixels = image.Pixels;
        var result = new byte[image.Width * image.Height * 2];
        bool bigEndian = byteOrder == Rgb565ByteOrder.BigEndian;
        int width = image.Width;
        int height = image.Height;
        int write = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int read = (y * width + x) * 4;
                int r = pixels[read + 2];
                int g = pixels[read + 1];
                int b = pixels[read];

                if (dither)
                {
                    // Shift by up to half a quantisation step, biased by the Bayer cell.
                    double offset = (Bayer4[y & 3, x & 3] + 0.5) / 16.0 - 0.5;
                    r = Math.Clamp(r + (int)Math.Round(offset * 8), 0, 255);
                    g = Math.Clamp(g + (int)Math.Round(offset * 4), 0, 255);
                    b = Math.Clamp(b + (int)Math.Round(offset * 8), 0, 255);
                }

                ushort value = Pack((byte)r, (byte)g, (byte)b);
                if (bigEndian)
                {
                    result[write] = (byte)(value >> 8);
                    result[write + 1] = (byte)value;
                }
                else
                {
                    result[write] = (byte)value;
                    result[write + 1] = (byte)(value >> 8);
                }

                write += 2;
            }
        }

        return result;
    }

    /// <summary>Packs 8-bit RGB into 5-6-5 (no dithering).</summary>
    public static ushort Pack(byte r, byte g, byte b)
        => (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));

    public static (byte R, byte G, byte B) Unpack(ushort value)
        => (
            (byte)(((value >> 11) & 0x1F) << 3),
            (byte)(((value >> 5) & 0x3F) << 2),
            (byte)((value & 0x1F) << 3));

    public static ushort ReadPixel(ReadOnlySpan<byte> payload, int pixelIndex, Rgb565ByteOrder byteOrder)
    {
        int offset = pixelIndex * 2;
        return byteOrder == Rgb565ByteOrder.BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));
    }
}
