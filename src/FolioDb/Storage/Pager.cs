using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace FolioDb.Storage;

/// <summary>
/// Page I/O over the main database file plus a write-ahead log (SQLite-style WAL mode).
/// <para>
/// Commits append page images ("frames") to the WAL; the last frame of a transaction carries a commit marker.
/// Every frame has a checksum chained to the previous frame, so recovery replays exactly the prefix of
/// fully written, committed transactions. Readers pin a snapshot (the number of committed frames when
/// they started) and never block the single writer.
/// </para>
/// <para>
/// Checkpoints run in two steps and never block readers. Backfill copies the latest frame of each page up to a target
/// into the main file, where the target is at most the oldest active snapshot (<see cref="ReadMarks"/>): every active
/// reader then reads those pages from the WAL, never from the part of the main file being overwritten. Once everything
/// is backfilled, new readers take mark 0 and read the main file only; when no reader uses a WAL snapshot any more, the
/// WAL restarts (new salts, frame 1). Cache keys of WAL frames are absolute (<c>_walBase</c> + frame), so a restart
/// does not have to touch the cache.
/// </para>
/// <para>
/// Group commit (<see cref="SynchronousMode.Full"/>): a commit appends its frames, becomes visible to the next writer
/// (<c>_writtenFrames</c>) and releases the write lock before its fsync. One committer then flushes every frame
/// written so far while the others wait for it (<see cref="WaitDurable"/>); readers only see durable frames
/// (<c>_committedFrames</c>). A failed fsync poisons the pager: frames past the durable mark may or may not survive,
/// so no later commit is acknowledged and the database must be reopened (recovery keeps a valid prefix).
/// </para>
/// </summary>
internal sealed class Pager : IDisposable
{
    private const int WalHeaderSize = 32;
    private const int FrameHeaderSize = 24;
    private static ReadOnlySpan<byte> WalMagic => "FOLIOWAL"u8;

    private readonly FileStream _db;
    private readonly FileStream _wal;
    private readonly SafeFileHandle _dbHandle;
    private readonly SafeFileHandle _walHandle;
    private readonly FolioOptions _options;
    private readonly PageCache _cache;
    private readonly int _frameSize;

    private readonly Lock _gate = new();
    // Page number -> its WAL frames in ascending order. Read without locks: frames are only appended (by the single
    // writer, under _gate), and a restart clears it only while no reader uses a WAL snapshot.
    private readonly ConcurrentDictionary<uint, FrameList> _walIndex = new();
    private long _committedFrames;   // durable and visible to readers
    private long _writtenFrames;     // appended to the WAL; visible to the writer
    // Frames ever appended / made durable. Monotonic (unlike frame numbers, which restart with the WAL).
    private long _appendedSeq;
    private long _durableSeq;
    private readonly object _flushGate = new();
    private bool _flushing;
    private Exception? _flushFailure;
    private ulong _chain;
    private uint _salt1, _salt2, _checkpointSeq;
    // Snapshot marks of active readers; the writer has none (it holds the write lock, which checkpoints also need).
    private readonly ReadMarks _marks = new();
    // Frames up to _backfilled are in the main file. _backfillTarget (>= _backfilled) is published before a backfill
    // looks at the read marks; a reader registers its mark and then checks the target (both sides fence), so either the
    // backfill sees the reader or the reader sees the target and retries with a newer mark.
    private long _backfilled;
    private long _backfillTarget;
    // Frames of earlier WAL generations: cache keys of frames are _walBase + frame, unique across restarts.
    private long _walBase;
    // A restart sets _checkpointing, checks the read marks, resets the WAL state, bumps _walGen and clears the flag.
    // A reader that read the state before checks both after registering, so it never keeps a mark of an old generation.
    private int _checkpointing;
    private long _walGen;
    // Auto-checkpoint backoff: after an attempt that could not finish, wait for this many appended frames.
    private long _nextAutoAttemptSeq;
    private int _autoFailures;
    // Readers also register in epochs, which tell the cache when evicted pages can be reused.
    private readonly ReaderEpoch _readers = new();
    private volatile bool _disposed;

    public int PageSize { get; }
    public string Path { get; }

    /// <summary>Test hook: when set, the next commit writes only this many WAL bytes and then fails (torn write).</summary>
    internal int? TestTornWriteBytes;

    /// <summary>Transactions appended to the WAL (diagnostics and tests).</summary>
    internal long AppendedCommits { get { lock (_gate) return _appendedCommits; } }
    private long _appendedCommits;

    /// <summary>Test hook: runs on the flushing thread before it decides which frames its fsync covers.</summary>
    internal Action? TestBeforeFlush;

    /// <summary>Test hook: runs after a backfill chose its target, before it writes anything.</summary>
    internal Action? TestBeforeBackfillWrites;

    /// <summary>Test hook: runs in <see cref="BeginRead"/> after the reader chose its mark, before it registers it.</summary>
    internal Action? TestBeforeReadMark;

    /// <summary>Test hook: runs after a backfill wrote its pages into the main file, before it fsyncs and publishes them.</summary>
    internal Action? TestDuringBackfill;

    /// <summary>WAL frames already copied into the main file.</summary>
    internal long Backfilled => Volatile.Read(ref _backfilled);

    /// <summary>Test hook: drops every cached WAL frame image, as if evicted.</summary>
    internal void TestDropWalFramesFromCache()
    {
        foreach (long key in _cache.Keys) if (key >= 0) _cache.Remove(key);
    }

    private Pager(string path, FileStream db, FileStream wal, int pageSize, FolioOptions options)
    {
        Path = path;
        _db = db;
        _wal = wal;
        _dbHandle = db.SafeFileHandle;
        _walHandle = wal.SafeFileHandle;
        PageSize = pageSize;
        _options = options;
        _frameSize = FrameHeaderSize + pageSize;
        _cache = new PageCache(options.CacheSizePages, _readers, pageSize);
    }

    public static Pager Open(string path, FolioOptions options)
    {
        FileStream db;
        try
        {
            db = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.RandomAccess);
        }
        catch (IOException e)
        {
            throw new FolioException($"Cannot open '{path}': the database is locked by another process or is inaccessible.", e);
        }

        FileStream? wal = null;
        try
        {
            int pageSize;
            bool created = false;
            if (db.Length == 0)
            {
                pageSize = options.PageSize;
                created = true;
            }
            else
            {
                Span<byte> head = stackalloc byte[64];
                if (RandomAccess.Read(db.SafeFileHandle, head, 0) < 64) throw new CorruptDatabaseException("Database file is truncated.");
                pageSize = DbHeader.ReadPageSize(head);
                if (pageSize < 1024 || pageSize > 32768 || (pageSize & (pageSize - 1)) != 0)
                    throw new CorruptDatabaseException($"Invalid page size {pageSize}.");
            }

            wal = new FileStream(path + "-wal", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.RandomAccess);
            var pager = new Pager(path, db, wal, pageSize, options);
            if (created) pager.InitializeNewDatabase();
            pager.RecoverWal();
            return pager;
        }
        catch
        {
            wal?.Dispose();
            db.Dispose();
            throw;
        }
    }

    private void InitializeNewDatabase()
    {
        // Page 0: header. Page 1: empty catalog B+Tree leaf.
        var buf = new byte[PageSize * 2];
        new DbHeader { PageSize = PageSize, PageCount = 2, CatalogRoot = 1 }.Write(buf);
        BTree.InitEmptyLeaf(buf.AsSpan(PageSize, PageSize), PageSize);
        RandomAccess.Write(_dbHandle, buf, 0);
        _db.Flush(flushToDisk: true);
    }

    // ---------------------------------------------------------------- readers / snapshots

    /// <summary>Registers a snapshot reader; pass <paramref name="slot"/> to <see cref="EndRead"/>.</summary>
    public long BeginRead(out int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int epochSlot = _readers.Enter();
        if (_disposed)
        {
            _readers.Exit(epochSlot);
            throw new ObjectDisposedException(GetType().FullName);
        }
        var spin = new SpinWait();
        while (true)
        {
            long gen = Volatile.Read(ref _walGen);
            long committed = Volatile.Read(ref _committedFrames);
            long backfilled = Volatile.Read(ref _backfilled);
            // Everything committed is in the main file: read it directly, so the WAL can restart under this reader.
            long mark = committed == backfilled ? 0 : committed;
            TestBeforeReadMark?.Invoke();
            int markSlot = _marks.Enter(mark, out long joined);
            long effective = joined == 0 ? backfilled : joined;
            if (Volatile.Read(ref _checkpointing) == 0 && Volatile.Read(ref _walGen) == gen
                && Volatile.Read(ref _backfillTarget) <= effective)
            {
                slot = epochSlot | ((markSlot + 1) << 8);
                return mark;
            }
            _marks.Exit(markSlot);
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>
    /// Like <see cref="BeginRead"/>, but the writer also sees committed frames that are not yet durable.
    /// <paramref name="seenSeq"/> is what the writer must wait for (<see cref="WaitDurable"/>) before it completes,
    /// even without changes of its own, so nothing it read can vanish after it returns. Caller must hold the write lock.
    /// </summary>
    public long BeginWrite(out long seenSeq, out int slot)
    {
        // A fully backfilled WAL restarts as soon as no reader uses it, before this writer appends to it.
        if (_writtenFrames > 0 && Volatile.Read(ref _backfilled) == _writtenFrames && Volatile.Read(ref _flushFailure) is null)
            TryRestart(waitTicks: 0);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFlushFailed();
            slot = _readers.Enter();
            seenSeq = _appendedSeq;
            return _writtenFrames;
        }
    }

    public void EndRead(int slot)
    {
        int markSlot = (slot >> 8) - 1;
        if (markSlot >= 0) _marks.Exit(markSlot);
        _readers.Exit(slot & 0xFF);
    }

    public long WalFrameCount
    {
        get { lock (_gate) return _writtenFrames; }
    }

    /// <summary>Returns the immutable image of <paramref name="pgno"/> as of snapshot <paramref name="mark"/>. Callers must not mutate it.</summary>
    public byte[] ReadPage(uint pgno, long mark) => ReadPage(pgno, mark, admit: true, out _);

    /// <summary>
    /// Transactions that miss more than this many pages are treated as scans: their further misses are not added to
    /// the cache, so one large scan cannot evict the working set of everyone else.
    /// </summary>
    internal int CachedPages => _cache.Count;

    public int ScanMissThreshold => Math.Max(64, _options.CacheSizePages / 8);

    /// <summary>
    /// Like <see cref="ReadPage(uint, long)"/>. <paramref name="missed"/> reports a cache miss; with
    /// <paramref name="admit"/> false a missed page is returned without being cached.
    /// </summary>
    public byte[] ReadPage(uint pgno, long mark, bool admit, out bool missed)
    {
        missed = false;
        long frame = 0;
        // Frame numbers start at 1, so an empty snapshot (mark 0) reads straight from the main file.
        if (mark > 0 && _walIndex.TryGetValue(pgno, out var frames)) frame = frames.LatestAtOrBefore(mark);

        long cacheKey = frame > 0 ? FrameKey(frame) : MainFileKey(pgno);
        if (_cache.TryGet(cacheKey, out var data)) return data;

        missed = true;
        data = _cache.RentPage();
        if (frame > 0)
        {
            int n = RandomAccess.Read(_walHandle, data, FrameOffset(frame) + FrameHeaderSize);
            if (n != PageSize) throw new CorruptDatabaseException($"Short read of WAL frame {frame}.");
        }
        else
        {
            int n = RandomAccess.Read(_dbHandle, data, (long)pgno * PageSize);
            if (n < PageSize) data.AsSpan(Math.Max(n, 0)).Clear(); // pages past EOF read as zeros
        }
        if (admit) _cache.Add(cacheKey, data);
        return data;
    }

    private static long MainFileKey(uint pgno) => -(long)pgno - 1;

    private long FrameKey(long frame) => Volatile.Read(ref _walBase) + frame;

    private void AppendFrame(uint pgno, long frame)
    {
        if (!_walIndex.TryGetValue(pgno, out var list)) _walIndex[pgno] = list = new FrameList();
        list.Append(frame);
    }

    /// <summary>
    /// Ascending WAL frame numbers of one page. Single writer appends; readers search without locks. The writer
    /// publishes a grown array before the count, and a reader loads the count before the array, so the array it
    /// sees always holds at least that many frames. Frames past a reader's mark are ignored by the search.
    /// </summary>
    private sealed class FrameList
    {
        private long[] _items = new long[2];
        private int _count;

        public void Append(long frame)
        {
            var items = _items;
            if (_count == items.Length)
            {
                Array.Resize(ref items, items.Length * 2);
                Volatile.Write(ref _items, items);
            }
            items[_count] = frame;
            Volatile.Write(ref _count, _count + 1);
        }

        public long LatestAtOrBefore(long mark)
        {
            int count = Volatile.Read(ref _count);
            var items = Volatile.Read(ref _items);
            int lo = 0, hi = count - 1;
            long result = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (items[mid] <= mark)
                {
                    result = items[mid];
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return result;
        }
    }

    private long FrameOffset(long frame) => WalHeaderSize + (frame - 1) * _frameSize;

    // ---------------------------------------------------------------- commit

    /// <summary>
    /// Appends a transaction's dirty pages to the WAL. Must only be called by the single writer. Returns a sequence
    /// number the caller must pass to <see cref="WaitDurable"/> after releasing the write lock (0: nothing to wait for).
    /// </summary>
    public long Commit(IReadOnlyList<KeyValuePair<uint, byte[]>> pages, uint dbPageCount)
    {
        if (pages.Count == 0) return 0;
        long firstFrame;
        lock (_gate)
        {
            ThrowIfFlushFailed();
            firstFrame = _writtenFrames + 1;
        }
        if (firstFrame + pages.Count > ReadMarks.MaxMark) throw new FolioException("The WAL is too large; checkpoint the database.");

        // Frame headers live in one small buffer; page images are written in place (gather write),
        // so a commit never copies its pages into a large contiguous frame buffer.
        var headers = new byte[pages.Count * FrameHeaderSize];
        var segments = new ReadOnlyMemory<byte>[pages.Count * 2];
        ulong chain = _chain;
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i].Value;
            if (page.Length != PageSize) throw new InvalidOperationException("Dirty page has the wrong size.");
            var header = headers.AsSpan(i * FrameHeaderSize, FrameHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], pages[i].Key);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], i == pages.Count - 1 ? dbPageCount : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], _salt1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], _salt2);
            chain = ChainHash(chain, header[8..], page);
            BinaryPrimitives.WriteUInt64LittleEndian(header, chain);
            segments[2 * i] = headers.AsMemory(i * FrameHeaderSize, FrameHeaderSize);
            segments[2 * i + 1] = page;
        }

        long offset = FrameOffset(firstFrame);
        if (TestTornWriteBytes is int torn)
        {
            TestTornWriteBytes = null;
            var flat = new byte[pages.Count * _frameSize];
            int pos = 0;
            foreach (var s in segments) { s.Span.CopyTo(flat.AsSpan(pos)); pos += s.Length; }
            RandomAccess.Write(_walHandle, flat.AsSpan(0, Math.Min(torn, flat.Length)), offset);
            throw new IOException("Simulated torn write.");
        }
        RandomAccess.Write(_walHandle, segments, offset);
        bool groupCommit = _options.Synchronous == SynchronousMode.Full;

        lock (_gate)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                uint pgno = pages[i].Key;
                AppendFrame(pgno, firstFrame + i);
                _cache.Add(FrameKey(firstFrame + i), pages[i].Value);
            }
            _writtenFrames = firstFrame + pages.Count - 1;
            _appendedSeq += pages.Count;
            _appendedCommits++;
            if (!groupCommit)
            {
                // Normal/Off: durability is not per commit, so the frames are published right away.
                Volatile.Write(ref _committedFrames, _writtenFrames);
                Volatile.Write(ref _durableSeq, _appendedSeq);
            }
            _chain = chain;
            return groupCommit ? _appendedSeq : 0;
        }
    }

    /// <summary>
    /// Returns once the commit at <paramref name="seq"/> is durable and visible to readers. The first waiter becomes
    /// the flusher and covers every frame written so far, so concurrent commits share one fsync. A checkpoint flushes
    /// first and never overlaps a flush, so frame numbers do not restart under a flusher.
    /// </summary>
    public void WaitDurable(long seq)
    {
        if (seq <= 0 || Volatile.Read(ref _durableSeq) >= seq) return;
        long upToSeq = 0, upToFrame = 0;
        lock (_flushGate)
        {
            while (true)
            {
                // Durable first: a commit covered by an earlier successful flush must not report a later failure.
                if (_durableSeq >= seq) return;
                if (_flushFailure is not null) throw new FolioException("A previous WAL flush failed; reopen the database.", _flushFailure);
                if (!_flushing) break;
                Monitor.Wait(_flushGate);
            }
            _flushing = true;
        }

        Exception? failure = null;
        try
        {
            TestBeforeFlush?.Invoke();
            // Frames appended before the fsync starts are covered by it, including other writers' commits.
            lock (_gate) { upToSeq = _appendedSeq; upToFrame = _writtenFrames; }
            _wal.Flush(flushToDisk: true);
        }
        catch (Exception e)
        {
            failure = e;
        }

        lock (_flushGate)
        {
            _flushing = false;
            if (failure is null)
            {
                lock (_gate) Volatile.Write(ref _committedFrames, upToFrame);
                Volatile.Write(ref _durableSeq, upToSeq);
            }
            else _flushFailure = failure;
            Monitor.PulseAll(_flushGate);
        }
        if (failure is not null) throw new FolioException("WAL flush failed; reopen the database.", failure);
    }

    /// <summary>Makes every written frame durable. Caller must hold the write lock (no new frames).</summary>
    private void FlushWritten()
    {
        long appended;
        lock (_gate) appended = _appendedSeq;
        WaitDurable(appended);
    }

    private void ThrowIfFlushFailed()
    {
        var failure = Volatile.Read(ref _flushFailure);
        if (failure is not null) throw new FolioException("A previous WAL flush failed; reopen the database.", failure);
    }

    /// <summary>Chained frame checksum: XxHash64 of (frame header after the checksum, page), seeded with the previous one.</summary>
    private static ulong ChainHash(ulong previous, ReadOnlySpan<byte> header, ReadOnlySpan<byte> page)
    {
        var hasher = new XxHash64(unchecked((long)previous));
        hasher.Append(header);
        hasher.Append(page);
        return hasher.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// Runs a checkpoint when the WAL holds enough frames that are not in the main file yet. Waits briefly for readers
    /// of older snapshots to finish (they are never blocked); after an attempt that could not finish, the next one waits
    /// for another <see cref="FolioOptions.AutoCheckpointFrames"/> frames and may wait a little longer. Caller must hold
    /// the write lock.
    /// </summary>
    public void MaybeAutoCheckpoint()
    {
        int frames = _options.AutoCheckpointFrames;
        if (frames <= 0 || Volatile.Read(ref _flushFailure) is not null || _writtenFrames - Volatile.Read(ref _backfilled) < frames)
            return;
        long appended = Volatile.Read(ref _appendedSeq);
        if (appended < _nextAutoAttemptSeq) return;
        long waitTicks = Math.Min(Stopwatch.Frequency / 1000 << _autoFailures, Stopwatch.Frequency * MaxAutoWaitMs / 1000);
        // A flush failure here is recorded (poisoning the pager); a commit reports it from WaitDurable, a rollback doesn't.
        try
        {
            if (Checkpoint(waitTicks)) _autoFailures = 0;
            else
            {
                _autoFailures = Math.Min(_autoFailures + 1, 16);
                _nextAutoAttemptSeq = appended + frames;
            }
        }
        catch (FolioException) when (Volatile.Read(ref _flushFailure) is not null) { }
    }

    /// <summary>Longest an automatic checkpoint waits for readers of older snapshots (it doubles from 1 ms per failure).</summary>
    internal const int MaxAutoWaitMs = 128;

    // ---------------------------------------------------------------- checkpoint

    /// <summary>
    /// Copies committed WAL frames into the main file and restarts the WAL. Never waits for readers: returns false if a
    /// reader's snapshot still needs the WAL (frames it allows are still copied). Caller must hold the write lock.
    /// </summary>
    public bool TryCheckpoint() => Checkpoint(waitTicks: 0);

    private bool Checkpoint(long waitTicks)
    {
        long deadline = Stopwatch.GetTimestamp() + waitTicks;
        if (_options.Synchronous == SynchronousMode.Full && _writtenFrames > Volatile.Read(ref _backfilled))
        {
            // A reader of the main file (mark 0) keeps anything from being copied: don't fsync under the write lock
            // for nothing. Otherwise make every written frame durable and visible, so the backfill can cover them all.
            var spin = new SpinWait();
            while (_marks.MinActive() == 0)
            {
                if (Stopwatch.GetTimestamp() >= deadline) return false;
                spin.SpinOnce();
            }
            FlushWritten();
        }
        if (!Backfill(deadline)) return false;
        return TryRestart(Math.Max(0, deadline - Stopwatch.GetTimestamp()));
    }

    /// <summary>
    /// Copies the latest frame (up to the oldest active snapshot) of every page into the main file. Returns true when
    /// everything committed is in the main file. Readers keep running: each active one has a mark at or past the target,
    /// so it reads these pages from the WAL, and none of them reads or caches their main-file images meanwhile.
    /// </summary>
    private bool Backfill(long deadline)
    {
        long prev = _backfilled;
        long committed = Volatile.Read(ref _committedFrames);
        if (committed <= prev) return true;

        long target = committed;
        var spin = new SpinWait();
        while (true)
        {
            Interlocked.Exchange(ref _backfillTarget, target);
            long min = _marks.MinActive();
            if (min >= target) break;
            // Readers of older snapshots usually finish quickly, and new ones start at the latest mark.
            if (target == committed && Stopwatch.GetTimestamp() < deadline)
            {
                spin.SpinOnce();
                continue;
            }
            // Mark 0 (main file only) and marks below prev belong to readers that need nothing past prev.
            long lowered = Math.Max(min, prev);
            if (lowered == prev)
            {
                Interlocked.Exchange(ref _backfillTarget, prev);
                return false;
            }
            target = lowered;
        }

        // The main file must never hold a page whose WAL frame could still be lost (Full: committed frames are durable).
        if (_options.Synchronous == SynchronousMode.Normal) _wal.Flush(flushToDisk: true);

        TestBeforeBackfillWrites?.Invoke();
        var pages = new List<(uint Pgno, long Frame)>();
        foreach (var (pgno, frames) in _walIndex)
        {
            long frame = frames.LatestAtOrBefore(target);
            if (frame > prev) pages.Add((pgno, frame));
        }
        pages.Sort(static (a, b) => a.Pgno.CompareTo(b.Pgno));

        const int Chunk = 256;
        var buffer = new byte[Math.Min(pages.Count, Chunk) * PageSize];
        var run = new List<ReadOnlyMemory<byte>>();
        for (int start = 0; start < pages.Count; start += Chunk)
        {
            int count = Math.Min(Chunk, pages.Count - start);
            var images = new ReadOnlyMemory<byte>[count];
            // Pages cached under their frame are hot: they get a private copy cached under their main-file key, for
            // readers of mark 0. Cached arrays may be recycled once evicted, so copy them while registered in an epoch.
            int epochSlot = _readers.Enter();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var (pgno, frame) = pages[start + i];
                    if (_cache.TryGet(FrameKey(frame), out var cached))
                    {
                        var copy = new byte[PageSize];
                        cached.CopyTo(copy, 0);
                        images[i] = copy;
                    }
                    else
                    {
                        var image = buffer.AsMemory(i * PageSize, PageSize);
                        if (RandomAccess.Read(_walHandle, image.Span, FrameOffset(frame) + FrameHeaderSize) != PageSize)
                            throw new CorruptDatabaseException($"Short read of WAL frame {frame} during checkpoint.");
                        images[i] = image;
                    }
                }
            }
            finally
            {
                _readers.Exit(epochSlot);
            }

            uint runStart = 0;
            for (int i = 0; i < count; i++)
            {
                uint pgno = pages[start + i].Pgno;
                if (run.Count > 0 && pgno != runStart + (uint)run.Count)
                {
                    RandomAccess.Write(_dbHandle, run, (long)runStart * PageSize);
                    run.Clear();
                }
                if (run.Count == 0) runStart = pgno;
                run.Add(images[i]);
            }
            if (run.Count > 0) RandomAccess.Write(_dbHandle, run, (long)runStart * PageSize);
            run.Clear();

            // Any main-file image cached earlier is stale now. No active reader looks these keys up (see above).
            for (int i = 0; i < count; i++)
            {
                long key = MainFileKey(pages[start + i].Pgno);
                if (MemoryMarshal.TryGetArray(images[i], out var segment) && segment.Array != buffer) _cache.Add(key, segment.Array!);
                else _cache.Remove(key);
            }
        }

        TestDuringBackfill?.Invoke();
        if (_options.Synchronous != SynchronousMode.Off) _db.Flush(flushToDisk: true);
        Volatile.Write(ref _backfilled, target);
        return target == committed;
    }

    /// <summary>
    /// Restarts a fully backfilled WAL once no reader uses a WAL snapshot (new readers already read the main file only).
    /// Waits up to <paramref name="waitTicks"/> for such readers to finish. Caller must hold the write lock.
    /// </summary>
    private bool TryRestart(long waitTicks)
    {
        if (_writtenFrames == 0) return true;
        if (Volatile.Read(ref _backfilled) != _writtenFrames) return false;
        if (_marks.AnyWalReaders() && waitTicks > 0)
        {
            long deadline = Stopwatch.GetTimestamp() + waitTicks;
            var spin = new SpinWait();
            while (_marks.AnyWalReaders() && Stopwatch.GetTimestamp() < deadline) spin.SpinOnce();
        }

        lock (_gate)
        {
            // Full: a flusher publishing frames of this generation must have finished.
            if (_durableSeq != _appendedSeq) return false;
            Interlocked.Exchange(ref _checkpointing, 1);
            try
            {
                if (_marks.AnyWalReaders()) return false;
                _walBase += _writtenFrames;
                _walIndex.Clear();
                _committedFrames = _writtenFrames = 0;
                _backfilled = _backfillTarget = 0;
                Interlocked.Increment(ref _walGen);
            }
            finally
            {
                Volatile.Write(ref _checkpointing, 0);
            }
        }

        // Readers no longer touch the WAL file, and the writer lock keeps commits out until the new header is in place.
        // If that fails, the in-memory state no longer matches the file: refuse further writes until reopened.
        try
        {
            _checkpointSeq++;
            WriteNewWalHeader();
        }
        catch (Exception e)
        {
            lock (_flushGate) _flushFailure ??= e;
            throw new FolioException("Restarting the WAL failed; reopen the database.", e);
        }
        return true;
    }

    private void WriteNewWalHeader()
    {
        Span<byte> salts = stackalloc byte[8];
        RandomNumberGenerator.Fill(salts);
        _salt1 = BinaryPrimitives.ReadUInt32LittleEndian(salts);
        _salt2 = BinaryPrimitives.ReadUInt32LittleEndian(salts[4..]);

        Span<byte> h = stackalloc byte[WalHeaderSize];
        WalMagic.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], PageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], _salt1);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], _salt2);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], _checkpointSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)XxHash64.HashToUInt64(h[..28]));

        _wal.SetLength(0);
        RandomAccess.Write(_walHandle, h, 0);
        if (_options.Synchronous != SynchronousMode.Off) _wal.Flush(flushToDisk: true);
        _chain = SeedChain();
    }

    private ulong SeedChain() => ((ulong)_salt1 << 32) | _salt2;

    // ---------------------------------------------------------------- recovery

    private void RecoverWal()
    {
        Span<byte> h = stackalloc byte[WalHeaderSize];
        bool valid = _wal.Length >= WalHeaderSize
            && RandomAccess.Read(_walHandle, h, 0) == WalHeaderSize
            && h[..8].SequenceEqual(WalMagic)
            && BinaryPrimitives.ReadInt32LittleEndian(h[12..]) == PageSize
            && BinaryPrimitives.ReadUInt32LittleEndian(h[28..]) == (uint)XxHash64.HashToUInt64(h[..28]);

        if (!valid)
        {
            WriteNewWalHeader();
            return;
        }

        _salt1 = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        _salt2 = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        _checkpointSeq = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
        ulong chain = SeedChain();

        var frame = new byte[_frameSize];
        var pending = new List<(uint Pgno, long Frame)>();
        long walLength = _wal.Length;
        for (long f = 1; WalHeaderSize + f * _frameSize <= walLength; f++)
        {
            if (RandomAccess.Read(_walHandle, frame, FrameOffset(f)) != _frameSize) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)) != _salt1
                || BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)) != _salt2) break;
            ulong expected = XxHash64.HashToUInt64(frame.AsSpan(8), unchecked((long)chain));
            if (BinaryPrimitives.ReadUInt64LittleEndian(frame) != expected) break;
            chain = expected;
            pending.Add((BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)), f));
            if (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)) != 0)
            {
                foreach (var (pgno, fr) in pending)
                {
                    AppendFrame(pgno, fr);
                }
                pending.Clear();
                _committedFrames = _writtenFrames = f;
                _chain = chain;
            }
        }

        if (_committedFrames == 0) _chain = SeedChain();
        // Move recovered transactions into the main file and start with a clean WAL.
        TryCheckpoint();
        if (_committedFrames == 0 && _wal.Length > WalHeaderSize) WriteNewWalHeader();
    }

    public long DatabaseFileLength => _db.Length;
    public long WalFileLength => _wal.Length;

    /// <summary>Test hook: drop file handles without checkpointing, as if the process had crashed.</summary>
    internal void SimulateCrash()
    {
        lock (_gate) _disposed = true;
        _wal.Dispose();
        _db.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        try
        {
            if (Volatile.Read(ref _flushFailure) is null && !TryCheckpoint() && _options.Synchronous != SynchronousMode.Off)
            {
                // Full: also waits for an in-flight group flush and publishes durability, so committers still
                // waiting take the fast path instead of flushing a disposed stream.
                if (_options.Synchronous == SynchronousMode.Full) FlushWritten();
                else _wal.Flush(flushToDisk: true);
            }
        }
        finally
        {
            lock (_gate) _disposed = true;
            _wal.Dispose();
            _db.Dispose();
        }
    }
}
