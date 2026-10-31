namespace OpenPet.Devices;

/// <summary>
/// Byte-level transport to the panel. Deliberately synchronous: one dedicated
/// USB writer thread owns it, so there is no value in async/await hop per 512B chunk.
/// The WPF layer (M4) wraps it with <see cref="Task.Run(Func{Task})"/> on that thread.
/// </summary>
public interface ILcdTransport : IDisposable
{
    string Description { get; }

    /// <summary>Writes the whole span as a single bulk transfer.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Blocks until every queued write has completed. Transports that submit
    /// synchronously implement this as a no-op; the pipelined HID transport uses
    /// it to drain the in-flight window.
    /// </summary>
    void Flush() { }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes; throws on timeout.</summary>
    int Read(Span<byte> buffer, TimeSpan timeout);
}
