namespace OpenPet.Core.Rendering;

/// <summary>
/// Decides whether a frame must go to the panel. The firmware falls back to its
/// boot logo a couple of seconds after the last frame, so a static theme still
/// needs a periodic keep-alive even when nothing changed.
/// </summary>
public sealed class FramePump
{
    private readonly TimeSpan _keepAlive;
    private readonly TimeSpan _minimumInterval;
    private ulong _lastHash;
    private bool _hasLast;
    private DateTimeOffset _lastSent;

    public FramePump(TimeSpan? keepAlive = null, TimeSpan? minimumInterval = null)
    {
        _keepAlive = keepAlive ?? TimeSpan.FromSeconds(1.5);
        _minimumInterval = minimumInterval ?? TimeSpan.Zero;
    }

    public long Sent { get; private set; }

    public long Skipped { get; private set; }

    public bool ShouldSend(ReadOnlySpan<byte> frame, DateTimeOffset now)
    {
        ulong hash = Hash(frame);

        if (_hasLast && hash == _lastHash)
        {
            if (now - _lastSent < _keepAlive)
            {
                Skipped++;
                return false;
            }
        }
        else if (now - _lastSent < _minimumInterval)
        {
            Skipped++;
            return false;
        }

        _lastHash = hash;
        _hasLast = true;
        _lastSent = now;
        Sent++;
        return true;
    }

    public void Reset()
    {
        _hasLast = false;
        _lastHash = 0;
        _lastSent = default;
        Sent = 0;
        Skipped = 0;
    }

    /// <summary>FNV-1a 64: cheap, allocation-free content hash for skip decisions.</summary>
    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offset;
        foreach (byte value in data)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }
}
