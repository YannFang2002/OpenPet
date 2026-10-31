using OpenPet.Core.Encoding;

namespace OpenPet.Devices;

/// <summary>
/// PM (product id) to panel geometry/encoding mapping, taken from public
/// Thermalright reverse-engineering notes. Only the 320x240 rows are relevant to M1.
/// </summary>
public static class DeviceCatalog
{
    private sealed record Entry(
        int CanvasWidth,
        int CanvasHeight,
        FrameEncoding Encoding,
        Rgb565ByteOrder ByteOrder,
        int Rotation);

    private static readonly Dictionary<int, Entry> Table = new()
    {
        // 2.8" IPS 320x240 (target panel); PM 49 uses big-endian RGB565.
        [49] = new(320, 240, FrameEncoding.Rgb565, Rgb565ByteOrder.BigEndian, 90),
        [50] = new(320, 240, FrameEncoding.Rgb565, Rgb565ByteOrder.LittleEndian, 90),
        [51] = new(320, 240, FrameEncoding.Rgb565, Rgb565ByteOrder.LittleEndian, 90),
        [52] = new(320, 240, FrameEncoding.Rgb565, Rgb565ByteOrder.LittleEndian, 90),
        [53] = new(320, 240, FrameEncoding.Rgb565, Rgb565ByteOrder.LittleEndian, 90),
        [58] = new(320, 240, FrameEncoding.Rgb565, Rgb565ByteOrder.LittleEndian, 90),

        // Square panels (AS120 Vision and friends).
        [36] = new(240, 240, FrameEncoding.Rgb565, Rgb565ByteOrder.LittleEndian, 0),
        [37] = new(240, 240, FrameEncoding.Rgb565, Rgb565ByteOrder.LittleEndian, 0),

        [54] = new(360, 360, FrameEncoding.Jpeg, Rgb565ByteOrder.LittleEndian, 0),
        [100] = new(320, 320, FrameEncoding.Rgb565, Rgb565ByteOrder.BigEndian, 0),
        [101] = new(320, 320, FrameEncoding.Rgb565, Rgb565ByteOrder.BigEndian, 0),
        [102] = new(320, 320, FrameEncoding.Rgb565, Rgb565ByteOrder.BigEndian, 0),
        [114] = new(1600, 720, FrameEncoding.Jpeg, Rgb565ByteOrder.LittleEndian, 90),
        [128] = new(1280, 480, FrameEncoding.Jpeg, Rgb565ByteOrder.LittleEndian, 90),
        [192] = new(1920, 462, FrameEncoding.Jpeg, Rgb565ByteOrder.LittleEndian, 90),
        [224] = new(854, 480, FrameEncoding.Jpeg, Rgb565ByteOrder.LittleEndian, 0),
        [59] = new(640, 172, FrameEncoding.Rgb565, Rgb565ByteOrder.LittleEndian, 0),
    };

    public static bool TryResolve(int productId, out DeviceProfile profile)
    {
        profile = new DeviceProfile();

        if (!Table.TryGetValue(productId, out Entry? entry))
        {
            return false;
        }

        bool rotated = entry.Rotation is 90 or 270;
        profile = new DeviceProfile
        {
            ProductId = productId,
            CanvasWidth = entry.CanvasWidth,
            CanvasHeight = entry.CanvasHeight,
            LineWidth = rotated ? entry.CanvasHeight : entry.CanvasWidth,
            LineHeight = rotated ? entry.CanvasWidth : entry.CanvasHeight,
            Encoding = entry.Encoding,
            ByteOrder = entry.ByteOrder,
            LineRotation = entry.Rotation,
        };
        return true;
    }

    /// <summary>Default profile for the known 320x240 rows; falls back to a sane default for unknown PMs.</summary>
    public static DeviceProfile ResolveOrDefault(int productId, int subVersion) =>
        TryResolve(productId, out DeviceProfile profile)
            ? profile with { SubVersion = subVersion }
            : new DeviceProfile { ProductId = productId, SubVersion = subVersion };
}
