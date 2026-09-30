namespace FolioDb;

/// <summary>Durability level for commits (mirrors SQLite's <c>PRAGMA synchronous</c> in WAL mode).</summary>
public enum SynchronousMode
{
    /// <summary>fsync the WAL on every commit. Survives power loss.</summary>
    Full,
    /// <summary>fsync only at checkpoints. Survives process crashes; the last commits may be lost on power loss.</summary>
    Normal,
    /// <summary>Never fsync. Fastest; only for disposable data.</summary>
    Off,
}

/// <summary>Admission policy for writers, explicit checkpoints and database disposal.</summary>
public enum WriterAdmissionMode
{
    /// <summary>Existing semaphore-based admission; ordering is not guaranteed.</summary>
    Default,
    /// <summary>Admit queued callers in FIFO order. May trade throughput for more even progress.</summary>
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

    /// <summary>Writer admission order. Default preserves semaphore behavior; FIFO is opt-in.</summary>
    public WriterAdmissionMode WriterAdmission { get; init; } = WriterAdmissionMode.Default;

    /// <summary>Admission timeout shared by writers, checkpoints and disposal. Zero tries immediately; -1 ms waits indefinitely.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal BTreeDeleteRebalanceMode DeleteRebalance { get; init; }

    internal void Validate()
    {
        if (PageSize < 1024 || PageSize > 32768 || (PageSize & (PageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(PageSize), "Page size must be a power of two between 1024 and 32768.");
        if (CacheSizePages < 16) throw new ArgumentOutOfRangeException(nameof(CacheSizePages), "Cache must hold at least 16 pages.");
        if (AutoCheckpointFrames < 0) throw new ArgumentOutOfRangeException(nameof(AutoCheckpointFrames));
        if (WriterAdmission is not (WriterAdmissionMode.Default or WriterAdmissionMode.Fifo))
            throw new ArgumentOutOfRangeException(nameof(WriterAdmission));
        if (DeleteRebalance is not (BTreeDeleteRebalanceMode.None or BTreeDeleteRebalanceMode.LeafByteOccupancy))
            throw new ArgumentOutOfRangeException(nameof(DeleteRebalance));
        if (BusyTimeout != Timeout.InfiniteTimeSpan && (BusyTimeout < TimeSpan.Zero || BusyTimeout.TotalMilliseconds > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(BusyTimeout));
    }
}
