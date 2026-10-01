namespace FolioDb;

/// <summary>Durability level for commits (mirrors SQLite's <c>PRAGMA synchronous</c> in WAL mode).</summary>
public enum SynchronousMode
{
    /// <summary>
    /// A commit returns only after its WAL frames are fsynced. Survives power loss. Concurrent commits share one fsync
    /// (group commit): the writer lock is released before the fsync, and readers only see durable commits.
    /// </summary>
    Full,
    /// <summary>fsync only at checkpoints. Survives process crashes; the last commits may be lost on power loss.</summary>
    Normal,
    /// <summary>Never fsync. Fastest; only for disposable data.</summary>
    Off,
}

/// <summary>Admission policy for writers, explicit checkpoints and database disposal.</summary>
public enum WriterAdmissionMode
{
    /// <summary>Semaphore-based admission without ordering guarantees; uses no spinning, but late arrivals may barge.</summary>
    Unordered,
    /// <summary>
    /// Admit queued callers in FIFO order, handing the lock directly to the next one. The next two queued callers
    /// spin briefly instead of blocking (at most two spinning threads per database), trading some CPU while
    /// contended for even progress at throughput comparable to <see cref="Unordered"/>. This is the default.
    /// </summary>
    Fifo,
}

internal enum BTreeDeleteRebalanceMode
{
    None,
    LeafByteOccupancy,
}

public sealed class FolioOptions
{
    /// <summary>Page size for new databases (power of two, 1024..32768). Existing databases keep their page size.</summary>
    public int PageSize { get; init; } = 4096;

    /// <summary>Maximum number of pages kept in the in-memory page cache.</summary>
    public int CacheSizePages { get; init; } = 4096;

    /// <summary>Automatically checkpoint when the WAL holds at least this many frames (0 disables).</summary>
    public int AutoCheckpointFrames { get; init; } = 1000;

    public SynchronousMode Synchronous { get; init; } = SynchronousMode.Full;

    /// <summary>Writer admission order. FIFO by default; <see cref="WriterAdmissionMode.Unordered"/> uses a plain semaphore.</summary>
    public WriterAdmissionMode WriterAdmission { get; init; } = WriterAdmissionMode.Fifo;

    /// <summary>Admission timeout shared by writers, checkpoints and disposal. Zero tries immediately; -1 ms waits indefinitely.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal BTreeDeleteRebalanceMode DeleteRebalance { get; init; }

    internal void Validate()
    {
        if (PageSize < 1024 || PageSize > 32768 || (PageSize & (PageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(PageSize), "Page size must be a power of two between 1024 and 32768.");
        if (CacheSizePages < 16) throw new ArgumentOutOfRangeException(nameof(CacheSizePages), "Cache must hold at least 16 pages.");
        if (AutoCheckpointFrames < 0) throw new ArgumentOutOfRangeException(nameof(AutoCheckpointFrames));
        if (WriterAdmission is not (WriterAdmissionMode.Unordered or WriterAdmissionMode.Fifo))
            throw new ArgumentOutOfRangeException(nameof(WriterAdmission));
        if (DeleteRebalance is not (BTreeDeleteRebalanceMode.None or BTreeDeleteRebalanceMode.LeafByteOccupancy))
            throw new ArgumentOutOfRangeException(nameof(DeleteRebalance));
        if (BusyTimeout != Timeout.InfiniteTimeSpan && (BusyTimeout < TimeSpan.Zero || BusyTimeout.TotalMilliseconds > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(BusyTimeout));
    }
}
