namespace OpenPet.Core.Encoding;

/// <summary>Splits an assembled frame into the fixed-size transfers the firmware expects.</summary>
public static class FrameChunker
{
    public readonly record struct Chunk(int Offset, int Length);

    public static IReadOnlyList<Chunk> Split(int totalLength, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);

        var chunks = new List<Chunk>(Count(totalLength, chunkSize));
        for (int offset = 0; offset < totalLength; offset += chunkSize)
        {
            chunks.Add(new Chunk(offset, Math.Min(chunkSize, totalLength - offset)));
        }

        return chunks;
    }

    public static int Count(int totalLength, int chunkSize)
        => (totalLength + chunkSize - 1) / chunkSize;
}
