using System.Buffers.Binary;

namespace OpenPet.Core.Encoding;

/// <summary>
/// Builds the 20-byte Thermalright Type-2 frame header and pads the frame to a
/// multiple of the chunk size.
/// </summary>
/// <remarks>
/// Layout (target 320x240 panel, RGB565):
/// <code>
/// 0..3   DA DB DC DD
/// 4..5   02 00
/// 6..7   01 00 (RGB565) / 00 00 (JPEG)
/// 8..11  240x320 line order, little-endian (JPEG uses the real size)
/// 12..15 02 00 00 00
/// 16..19 payload length, little-endian
/// </code>
/// </remarks>
public static class FrameAssembler
{
    public const int HeaderSize = 20;

    public static byte[] BuildHeader(int lineWidth, int lineHeight, FrameEncoding encoding, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadLength);

        var header = new byte[HeaderSize];
        header[0] = 0xDA;
        header[1] = 0xDB;
        header[2] = 0xDC;
        header[3] = 0xDD;
        header[4] = 0x02;
        header[5] = 0x00;
        header[6] = encoding == FrameEncoding.Rgb565 ? (byte)0x01 : (byte)0x00;
        header[7] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)lineWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)lineHeight);
        header[12] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)payloadLength);
        return header;
    }

    /// <summary>Header + payload, zero padded to <paramref name="chunkSize"/>.</summary>
    public static byte[] Assemble(
        ReadOnlySpan<byte> payload,
        int lineWidth,
        int lineHeight,
        FrameEncoding encoding,
        int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);

        byte[] header = BuildHeader(lineWidth, lineHeight, encoding, payload.Length);
        int total = HeaderSize + payload.Length;
        int padded = RoundUp(total, chunkSize);
        var frame = new byte[padded];

        header.CopyTo(frame, 0);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static int RoundUp(int value, int chunkSize)
        => (value + chunkSize - 1) / chunkSize * chunkSize;
}
