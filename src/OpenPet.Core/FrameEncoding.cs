namespace OpenPet.Core.Encoding;

/// <summary>Wire encoding used for the frame payload.</summary>
public enum FrameEncoding
{
    Rgb565 = 0,
    Jpeg = 1,
}

/// <summary>RGB565 byte order. PM 49 panels are big-endian, most others little-endian.</summary>
public enum Rgb565ByteOrder
{
    LittleEndian = 0,
    BigEndian = 1,
}
