using System.Text;

namespace OpenPet.Devices.Protocol;

public enum HandshakeMode
{
    /// <summary>Standard init packet first, streamed firmware fallback on failure.</summary>
    Auto = 0,

    Standard = 1,

    /// <summary>No init packet; wait ~3s and read the 8-byte voluntary reply.</summary>
    Streamed = 2,
}

/// <summary>Result of one handshake attempt.</summary>
public sealed record HandshakeResult(
    HandshakeMode Mode,
    int? ProductId,
    int? SubVersion,
    string? SerialNumber,
    byte[] Response)
{
    public bool IsStandard => Mode == HandshakeMode.Standard;
}

/// <summary>Thermalright Type-2 handshake packet builder and reply parser.</summary>
public static class Handshake
{
    public const int PacketSize = 512;

    /// <summary>
    /// DA DB DC DD | 8 x 00 | 01 00 00 00 | 4 x 00, zero padded to 512 bytes.
    /// </summary>
    public static byte[] BuildRequest()
    {
        var packet = new byte[PacketSize];
        packet[0] = 0xDA;
        packet[1] = 0xDB;
        packet[2] = 0xDC;
        packet[3] = 0xDD;
        packet[12] = 0x01;
        return packet;
    }

    /// <summary>
    /// Validates magic and status byte, then reads PM = resp[5], SUB = resp[4]
    /// and the optional serial at resp[20..36].
    /// </summary>
    public static HandshakeResult Parse(ReadOnlySpan<byte> response)
    {
        if (response.Length < 16)
        {
            throw new LcdDeviceException($"Handshake reply is too short ({response.Length} bytes).");
        }

        if (!(response[0] == 0xDA && response[1] == 0xDB && response[2] == 0xDC && response[3] == 0xDD))
        {
            throw new LcdDeviceException(
                $"Handshake reply has wrong magic: {Convert.ToHexString(response[..4])}.");
        }

        if (response[12] != 0x01)
        {
            throw new LcdDeviceException(
                $"Handshake rejected: status byte is 0x{response[12]:X2}, expected 0x01. " +
                "The panel may need the streamed (bcdDevice 4.07) path.");
        }

        string? serial = response.Length >= 36 ? AsciiOrNull(response.Slice(20, 16)) : null;
        return new HandshakeResult(HandshakeMode.Standard, response[5], response[4], serial, response.ToArray());
    }

    /// <summary>
    /// Parses the short reply a streaming firmware volunteers (bcdDevice 4.07):
    /// magic + PM/SUB only, often 8 bytes, no 512-byte body.
    /// </summary>
    public static HandshakeResult ParseShort(ReadOnlySpan<byte> response)
    {
        if (response.Length < 6)
        {
            throw new LcdDeviceException($"Short handshake reply is too short ({response.Length} bytes).");
        }

        if (!(response[0] == 0xDA && response[1] == 0xDB && response[2] == 0xDC && response[3] == 0xDD))
        {
            throw new LcdDeviceException(
                $"Short handshake reply has wrong magic: {Convert.ToHexString(response[..4])}.");
        }

        return new HandshakeResult(HandshakeMode.Streamed, response[5], response[4], null, response.ToArray());
    }

    /// <summary>Human-readable annotation of the interesting reply offsets.</summary>
    public static string Annotate(ReadOnlySpan<byte> response)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"  [4]  SUB = 0x{At(response, 4):X2}{(response.Length > 4 ? $" ({response[4]})" : string.Empty)}");
        builder.AppendLine($"  [5]  PM  = 0x{At(response, 5):X2}{(response.Length > 5 ? $" ({response[5]})" : string.Empty)}");
        builder.AppendLine($"  [12] status = 0x{At(response, 12):X2} (expect 0x01)");
        if (response.Length >= 36)
        {
            string? serial = AsciiOrNull(response.Slice(20, 16));
            builder.AppendLine($"  [20..35] serial = \"{serial ?? "<not ascii>"}\"");
        }

        return builder.ToString();
    }

    private static byte At(ReadOnlySpan<byte> data, int index) => index < data.Length ? data[index] : (byte)0;

    private static string? AsciiOrNull(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length);
        foreach (byte value in bytes)
        {
            if (value is >= 0x20 and < 0x7F)
            {
                text.Append((char)value);
            }
            else if (value == 0)
            {
                continue;
            }
            else
            {
                return null;
            }
        }

        string result = text.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}
