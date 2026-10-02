namespace FolioDb.Storage;

/// <summary>
/// Snapshot marks of active readers (SQLite's "read marks"): a few slots, each packing a mark (the last frame a reader
/// may read), the set of WAL files its snapshot reads from and the number of readers using it into one word. A reader
/// registers with a single CAS; a checkpoint scans the slots for the oldest mark (how far it may copy frames into the
/// main file) and for readers of a WAL file it wants to retire. A slot changes its mark only while its count is zero
/// (a CAS from the observed free value), so a reader joining a slot can never end up counted under another mark.
/// </summary>
internal sealed class ReadMarks
{
    public const int Slots = 16;
    private const int Stride = 8; // one slot per cache line
    private const int CountBits = 16;
    private const int MaskBits = 2;
    private const int MarkShift = CountBits + MaskBits;
    private const long CountMask = (1L << CountBits) - 1;
    private const long FileMask = ((1L << MaskBits) - 1) << CountBits;
    public const long MaxMark = long.MaxValue >> MarkShift;

    private readonly long[] _slots = new long[Slots * Stride];

    private static long Mark(long v) => v >> MarkShift;
    private static int Files(long v) => (int)((v & FileMask) >> CountBits);

    /// <summary>
    /// Registers a reader of snapshot <paramref name="mark"/> that reads the WAL files in <paramref name="files"/>
    /// (bit per file) and returns its slot, or -1 when no slot can take it (retry with a fresh snapshot). Prefers a free
    /// slot or one with the same mark that already covers its files; when every slot is taken, it joins the newest slot
    /// at or below its mark and adds its files to it. That only protects more than the reader needs: a lower mark makes
    /// checkpoints copy less, more files keep more of them around. The CAS is a full fence, so state read after this
    /// call is ordered after the registration.
    /// </summary>
    public int Enter(long mark, int files)
    {
        // Threads start at different slots, so readers of the same mark rarely contend on one word.
        int start = Environment.CurrentManagedThreadId;
        long own = (mark << MarkShift) | ((long)files << CountBits);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int best = -1;
            long bestValue = 0;
            for (int n = 0; n < Slots; n++)
            {
                int i = ((start + n) & (Slots - 1)) * Stride;
                long v = Volatile.Read(ref _slots[i]);
                long count = v & CountMask;
                if (count == 0)
                {
                    if (Interlocked.CompareExchange(ref _slots[i], own | 1, v) == v) return i;
                    continue;
                }
                if (count == CountMask) continue;
                long m = Mark(v);
                if (m == mark && (files & ~Files(v)) == 0)
                {
                    if (Interlocked.CompareExchange(ref _slots[i], v + 1, v) == v) return i;
                    continue;
                }
                if (m <= mark && (best < 0 || m > Mark(bestValue)))
                {
                    best = i;
                    bestValue = v;
                }
            }
            if (best >= 0 && Interlocked.CompareExchange(ref _slots[best], (bestValue | ((long)files << CountBits)) + 1, bestValue) == bestValue)
                return best;
            Thread.SpinWait(8 << attempt);
        }
        return -1;
    }

    /// <summary>Leaves the slot; returns the WAL files it released (its last reader left), 0 if others still use it.</summary>
    public int Exit(int slot)
    {
        long v = Interlocked.Decrement(ref _slots[slot]);
        return (v & CountMask) == 0 ? Files(v) : 0;
    }

    /// <summary>Smallest mark with active readers, or <see cref="long.MaxValue"/> when there are none.</summary>
    public long MinActive()
    {
        long min = long.MaxValue;
        for (int i = 0; i < _slots.Length; i += Stride)
        {
            long v = Volatile.Read(ref _slots[i]);
            if ((v & CountMask) != 0 && Mark(v) < min) min = Mark(v);
        }
        return min;
    }

    /// <summary>True when some active reader reads from a WAL file in <paramref name="files"/>.</summary>
    public bool AnyReading(int files)
    {
        for (int i = 0; i < _slots.Length; i += Stride)
        {
            long v = Volatile.Read(ref _slots[i]);
            if ((v & CountMask) != 0 && (Files(v) & files) != 0) return true;
        }
        return false;
    }
}
