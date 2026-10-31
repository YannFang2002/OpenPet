using OpenPet.Core.Encoding;
using OpenPet.Devices.Protocol;
using HandshakeCodec = OpenPet.Devices.Protocol.Handshake;

namespace OpenPet.Devices;

/// <summary>Thermalright HID Type-2 protocol driver: handshake plus chunked frame writes.</summary>
public sealed class ThermalrightType2 : IDisposable
{
    private readonly ILcdTransport _transport;
    private bool _disposed;

    public ThermalrightType2(ILcdTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
    }

    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Milliseconds to wait after a frame before the next one (firmware settle).</summary>
    public int SettleMs { get; set; } = 1;

    /// <summary>Runs the handshake, trying standard first and the streamed path on failure.</summary>
    public HandshakeResult Handshake(HandshakeMode mode = HandshakeMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (mode is HandshakeMode.Standard or HandshakeMode.Auto)
        {
            try
            {
                return StandardHandshake();
            }
            catch (LcdDeviceException standardFailure) when (mode == HandshakeMode.Auto)
            {
                try
                {
                    return StreamedHandshake();
                }
                catch (LcdDeviceException streamedFailure)
                {
                    throw new LcdDeviceException(
                        $"Both handshake paths failed. standard: {standardFailure.Message} " +
                        $"streamed: {streamedFailure.Message}",
                        new AggregateException(standardFailure, streamedFailure));
                }
            }
        }

        return StreamedHandshake();
    }

    public HandshakeResult StandardHandshake()
    {
        _transport.Write(HandshakeCodec.BuildRequest());

        // WinUSB returns the whole 512-byte reply in one read; HID delivers it as
        // a sequence of small input reports, so accumulate until the packet is full.
        var buffer = new byte[HandshakeCodec.PacketSize];
        int total = 0;
        DateTime deadline = DateTime.UtcNow + ReadTimeout;

        while (total < buffer.Length && DateTime.UtcNow < deadline)
        {
            try
            {
                int read = _transport.Read(buffer.AsSpan(total), ReadTimeout);
                if (read <= 0)
                {
                    break;
                }

                total += read;

                // This firmware answers with a short (37-byte) Type-2 reply; once
                // the magic and status byte are in, don't wait the full timeout for
                // the remaining 475 bytes that will never arrive.
                if (total >= 20
                    && buffer[0] == 0xDA && buffer[1] == 0xDB && buffer[2] == 0xDC && buffer[3] == 0xDD
                    && buffer[12] == 0x01)
                {
                    break;
                }
            }
            catch (LcdTimeoutException)
            {
                break;
            }
        }

        if (total < 16)
        {
            throw new LcdDeviceException($"Handshake reply too short ({total} bytes).");
        }

        return HandshakeCodec.Parse(buffer.AsSpan(0, total));
    }

    /// <summary>Skip init, wait for the firmware to volunteer its short reply (bcdDevice 4.07 path).</summary>
    public HandshakeResult StreamedHandshake()
    {
        var shortReply = new byte[512];
        Thread.Sleep(TimeSpan.FromSeconds(3));
        int read = _transport.Read(shortReply, ReadTimeout);
        if (read == 0)
        {
            throw new LcdDeviceException("Streamed handshake returned no bytes.");
        }

        // A short reply still names the panel (magic + PM/SUB at [5]/[4]).
        if (read >= 6 && shortReply[0] == 0xDA && shortReply[1] == 0xDB && shortReply[2] == 0xDC && shortReply[3] == 0xDD)
        {
            return HandshakeCodec.ParseShort(shortReply.AsSpan(0, read));
        }

        return new HandshakeResult(
            HandshakeMode.Streamed,
            null,
            null,
            null,
            read == shortReply.Length ? shortReply : shortReply[..read]);
    }

    /// <summary>
    /// Writes a pre-assembled frame in fixed-size chunks. A short write is raised
    /// immediately so the probe fails fast instead of showing a half frame.
    /// </summary>
    public void SendFrame(ReadOnlySpan<byte> frame, int chunkSize, int? settleMs = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (chunkSize <= 0)
        {
            // "full": probe the firmware's largest accepted transfer.
            _transport.Write(frame);
        }
        else
        {
            foreach (FrameChunker.Chunk chunk in FrameChunker.Split(frame.Length, chunkSize))
            {
                _transport.Write(frame.Slice(chunk.Offset, chunk.Length));
            }
        }

        // Drain any pipelined window before the settle delay, so the settle
        // measures a fully-delivered frame rather than an in-flight one.
        _transport.Flush();

        int settle = settleMs ?? SettleMs;
        if (settle > 0)
        {
            Thread.Sleep(settle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.Dispose();
    }
}
