namespace FolioDb.Storage;

/// <summary>
/// Tracks active readers in two epoch buckets (SRCU-style), so evicted page images can be reused once every reader
/// that might still hold them has finished. A reader registers in the bucket of the current epoch and confirms the
/// epoch did not move meanwhile; the reclaimer advances the epoch and later waits for the previous bucket to drain.
/// <para>
/// Why this is enough: a page is removed from the cache before it is retired, and retired pages wait for the epoch
/// that was current when they were retired to drain. A reader that could still hold such a page read it from the cache
/// before the removal, so it registered (with a full fence) before that, in that epoch's bucket or an older one; the
/// epoch only advances again after the previous bucket drained, so older readers are gone by then. A reader that
/// registers after the advance sees the removal and cannot obtain the page.
/// </para>
/// </summary>
internal sealed class ReaderEpoch
{
    // Two counters on separate cache lines.
    private const int Stride = 16;
    private readonly int[] _counts = new int[Stride * 2];
    private long _epoch;

    /// <summary>Registers a reader; returns the slot to pass to <see cref="Exit"/>.</summary>
    public int Enter()
    {
        while (true)
        {
            long epoch = Volatile.Read(ref _epoch);
            int slot = (int)(epoch & 1) * Stride;
            Interlocked.Increment(ref _counts[slot]);
            if (Volatile.Read(ref _epoch) == epoch) return slot;
            Interlocked.Decrement(ref _counts[slot]);
        }
    }

    public void Exit(int slot) => Interlocked.Decrement(ref _counts[slot]);

    public int Active => Volatile.Read(ref _counts[0]) + Volatile.Read(ref _counts[Stride]);

    public long Current => Volatile.Read(ref _epoch);

    /// <summary>Advances the epoch. Callers must only advance once the previous epoch has drained.</summary>
    public void Advance() => Interlocked.Increment(ref _epoch);

    /// <summary>True when no reader registered in <paramref name="epoch"/>'s bucket remains.</summary>
    public bool Drained(long epoch)
    {
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _counts[(int)(epoch & 1) * Stride]) == 0;
    }
}
