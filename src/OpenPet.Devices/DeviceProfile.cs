using OpenPet.Core.Encoding;
using OpenPet.Core.Imaging;

namespace OpenPet.Devices;

/// <summary>Everything needed to render and push frames to one panel.</summary>
public sealed record DeviceProfile
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>PM byte from the handshake.</summary>
    public int ProductId { get; init; }

    /// <summary>SUB byte from the handshake.</summary>
    public int SubVersion { get; init; }

    public string? SerialNumber { get; init; }

    public string? BcdDevice { get; init; }

    /// <summary>Editor/theme canvas, always 320x240 for the target panel.</summary>
    public int CanvasWidth { get; init; } = 320;

    public int CanvasHeight { get; init; } = 240;

    /// <summary>Line-order width actually written to the panel (240 for the target).</summary>
    public int LineWidth { get; init; } = 240;

    public int LineHeight { get; init; } = 320;

    public FrameEncoding Encoding { get; init; } = FrameEncoding.Rgb565;

    public Rgb565ByteOrder ByteOrder { get; init; } = Rgb565ByteOrder.LittleEndian;

    /// <summary>Clockwise rotation applied to the canvas to obtain the line order: 0/90/180/270.</summary>
    public int LineRotation { get; init; } = 90;

    public FlipMode Flip { get; init; } = FlipMode.None;

    public int ChunkSize { get; init; } = 512;

    public int TargetFps { get; init; } = 30;

    public int SettleMs { get; init; } = 1;

    public DateTimeOffset? ConfirmedAt { get; init; }

    public string? Notes { get; init; }

    public int PayloadLength => LineWidth * LineHeight * (Encoding == FrameEncoding.Rgb565 ? 2 : 0);

    public override string ToString() =>
        $"PM={ProductId} SUB={SubVersion} {LineWidth}x{LineHeight} {Encoding} {ByteOrder} rot{LineRotation} chunk{ChunkSize}";
}
