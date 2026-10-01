namespace FolioDb.Storage;

/// <summary>
/// Snapshot marks of active readers (SQLite's "read marks"): a few slots, each packing a WAL mark and the number of
/// readers using it into one word, so a reader registers with a single CAS and a checkpoint finds the oldest snapshot by
/// scanning the slots. A slot changes its mark only while its count is zero (a CAS from the observed free value), so a
/// reader joining a slot with an observed (mark, count) can never end up counted under another mark.
/// <para>
/// Mark 0 means "reads only the main file" (everything committed was already copied there).
/// </para>
/// </summary>
internal sealed class ReadMarks
{
    public const int Slots = 16;
    private const int Stride = 8; // one slot per cache line
    private const int CountBits = 24;
    private const long CountMask = (1L << CountBits) - 1;
    public const long MaxMark = long.MaxValue >> CountBits;

    private readonly long[] _slots = new long[Slots * Stride];

    /// <summary>
    /// Registers a reader at <paramref name="mark"/> and returns its slot. If every slot is taken by other marks, joins
    /// the newest WAL mark below it instead: <paramref name="joined"/> is the mark actually registered. It only has to
    /// protect the reader's snapshot, so a lower WAL mark is conservative (a checkpoint copies less); mark 0 is joined
    /// only by mark-0 readers, and a reader never registers above its own mark. The CAS is a full fence, so state read
    /// after this call is ordered after the registration.
    /// </summary>
    public int Enter(long mark, out long joined)
    {
        // Threads start at different slots, so readers of the same mark rarely contend on one word.
        int start = Environment.CurrentManagedThreadId;
        var spin = new SpinWait();
        while (true)
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
                    if (Interlocked.CompareExchange(ref _slots[i], (mark << CountBits) | 1, v) == v)
                    {
                        joined = mark;
                        return i;
                    }
                    continue;
                }
                if (count == CountMask) continue;
                if (v >> CountBits == mark)
                {
                    if (Interlocked.CompareExchange(ref _slots[i], v + 1, v) == v)
                    {
                        joined = mark;
                        return i;
                    }
                    continue;
                }
                long m = v >> CountBits;
                if (m != 0 && m < mark && (best < 0 || m > bestValue >> CountBits))
                {
                    best = i;
                    bestValue = v;
                }
            }
            if (best >= 0 && Interlocked.CompareExchange(ref _slots[best], bestValue + 1, bestValue) == bestValue)
            {
                joined = bestValue >> CountBits;
                return best;
            }
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    public void Exit(int slot) => Interlocked.Decrement(ref _slots[slot]);

    /// <summary>Smallest mark with active readers, or <see cref="long.MaxValue"/> when there are none.</summary>
    public long MinActive()
    {
        long min = long.MaxValue;
        for (int i = 0; i < _slots.Length; i += Stride)
        {
            long v = Volatile.Read(ref _slots[i]);
            if ((v & CountMask) != 0 && v >> CountBits < min) min = v >> CountBits;
        }
        return min;
    }

    /// <summary>True when some active reader uses a WAL snapshot (a mark above 0).</summary>
    public bool AnyWalReaders()
    {
        for (int i = 0; i < _slots.Length; i += Stride)
        {
            long v = Volatile.Read(ref _slots[i]);
            if ((v & CountMask) != 0 && v >> CountBits != 0) return true;
        }
        return false;
    }
}
