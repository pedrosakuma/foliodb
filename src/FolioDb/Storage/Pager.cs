using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace FolioDb.Storage;

/// <summary>
/// Page I/O over the main database file plus a write-ahead log in two alternating files (SQLite's "wal2" layout).
/// <para>
/// Commits append page images ("frames") to the active WAL file; the last frame of a transaction carries a commit
/// marker. Every frame has a checksum chained to the previous one (across files), so recovery replays exactly the prefix
/// of fully written, committed transactions. Frame numbers are absolute for the life of the process: a frame lives in
/// the file whose range holds it (<see cref="WalLayout"/>), and its cache key is its number.
/// </para>
/// <para>
/// Checkpoints never block the writer or readers. Once the active file holds
/// <see cref="FolioOptions.AutoCheckpointFrames"/> frames, the writer switches to the other (empty) file, which costs
/// one small header write, and a background thread copies the frames of the now old file into the main file
/// ("backfill") and then retires it (empties it for the next switch). A backfill copies at most up to the oldest active
/// snapshot (<see cref="ReadMarks"/>); a reader records how far the main file was backfilled when it started (its
/// floor) and reads frames past it from the WAL, so the main file never changes under it. A file is retired only when
/// no snapshot reads from it.
/// </para>
/// <para>
/// Each WAL file starts with two header slots written alternately, so a torn header write leaves the previous record
/// intact. Recovery replays the active file(s) whose predecessor is retired or replayed with them (chain seed match).
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
    private const int FrameHeaderSize = 24;
    private const int RecordSize = 56;
    private const int RecordSlotSize = 512;
    /// <summary>Bytes before the first frame of a WAL file: two header record slots.</summary>
    internal const int WalHeaderRegion = 2 * RecordSlotSize;
    private const int LegacyHeaderSize = 32;
    private const int StateActive = 1;
    private const int StateRetired = 2;
    private const int WriterSlot = 1 << 30;
    private static ReadOnlySpan<byte> WalMagic => "FOLIOWL2"u8;
    private static ReadOnlySpan<byte> LegacyWalMagic => "FOLIOWAL"u8;

    private readonly FileStream _db;
    private readonly SafeFileHandle _dbHandle;
    private readonly FileStream[] _wal;
    private readonly SafeFileHandle[] _walHandles;
    private readonly FolioOptions _options;
    private readonly PageCache _cache;
    private readonly int _frameSize;

    private readonly Lock _gate = new();
    // Page number -> its WAL frames in ascending order. Read without locks: the single writer appends (under _gate);
    // retiring a file replaces lists by copies without its frames (under _gate), which only readers that do not read
    // that file can observe, and the old lists still hold every frame of their snapshots.
    private readonly ConcurrentDictionary<uint, FrameList> _walIndex = new();
    private long _committedFrames;   // durable and visible to readers
    private long _writtenFrames;     // appended to the WAL; visible to the writer
    // Frames ever appended / made durable (group commit).
    private long _appendedSeq;
    private long _durableSeq;
    private readonly object _flushGate = new();
    private bool _flushing;
    private Exception? _flushFailure;
    private ulong _chain;
    private uint _salt1, _salt2;
    // Header records: the next slot to write in each file, its sequence number and a global record counter.
    private readonly int[] _nextRecordSlot = new int[2];
    private readonly long[] _fileSeq = new long[2];
    private long _recordEpoch;

    private WalLayout _layout = new(0, 1);
    // The inactive file is empty and its "retired" header is durable: the writer may switch to it.
    private volatile bool _otherReady;
    // Snapshot marks and WAL files of active readers. The writer registers its files separately (under _gate).
    private readonly ReadMarks _marks = new();
    private int _writerFiles;
    // The old file the checkpointer waits for: whoever stops reading from it last wakes the checkpointer.
    private int _retireWait;
    // Pulsed after each checkpointer pass, for a writer waiting for the backfill (backpressure).
    private readonly object _progress = new();
    // The last checkpointer pass could not retire the old file (readers still need it).
    private volatile bool _checkpointStalled;
    // Frames up to _backfilled are in the main file. _backfillTarget (>= _backfilled) is published before a backfill
    // looks at the read marks; a reader registers its mark and then checks the target (both sides fence), so either the
    // backfill sees the reader or the reader sees the target and retries with a newer mark.
    private long _backfilled;
    private long _backfillTarget;
    // Normal: WAL frames known to be on disk (only the checkpointing thread uses it).
    private long _walSynced;
    // Set while a file is retired: a reader that registered meanwhile retries (Dekker with its CAS on the read marks).
    private int _retiring;
    // Serializes backfills and retires (background thread, explicit checkpoints).
    private readonly Lock _checkpointLock = new();
    private Thread? _checkpointer;
    private readonly ManualResetEventSlim _wake = new();
    private volatile bool _stopping;
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

    /// <summary>Test hook: runs after a retire wrote the "retired" header record (file, offset), before it is flushed.</summary>
    internal Action<int, long>? TestAfterRetireRecord;

    /// <summary>Test hook: runs when a retire found no reader of the old file, before it drops the file's frames.</summary>
    internal Action? TestAfterRetireScan;

    /// <summary>Test hook: runs before a page is read from a WAL file (the file), after its frame was looked up.</summary>
    internal Action<int>? TestBeforeWalFrameRead;

    /// <summary>WAL frames already copied into the main file.</summary>
    internal long Backfilled => Volatile.Read(ref _backfilled);

    /// <summary>Frames appended since the database was opened (absolute number of the last frame).</summary>
    internal long WrittenFrames { get { lock (_gate) return _writtenFrames; } }

    /// <summary>Which WAL file is active (0: <c>-wal</c>, 1: <c>-wal2</c>) and whether the other one still holds frames.</summary>
    internal (int Active, bool OldPending) WalFiles
    {
        get
        {
            var layout = Volatile.Read(ref _layout);
            return (layout.Active, layout.Old >= 0);
        }
    }

    /// <summary>Test hook: drops every cached WAL frame image, as if evicted.</summary>
    internal void TestDropWalFramesFromCache()
    {
        foreach (long key in _cache.Keys) if (key >= 0) _cache.Remove(key);
    }

    private Pager(string path, FileStream db, FileStream wal0, FileStream wal1, int pageSize, FolioOptions options)
    {
        Path = path;
        _db = db;
        _dbHandle = db.SafeFileHandle;
        _wal = [wal0, wal1];
        _walHandles = [wal0.SafeFileHandle, wal1.SafeFileHandle];
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

        FileStream? wal0 = null, wal1 = null;
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

            wal0 = new FileStream(path + "-wal", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.RandomAccess);
            wal1 = new FileStream(path + "-wal2", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.RandomAccess);
            var pager = new Pager(path, db, wal0, wal1, pageSize, options);
            if (created) pager.InitializeNewDatabase();
            pager.RecoverWal();
            return pager;
        }
        catch
        {
            wal1?.Dispose();
            wal0?.Dispose();
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
    public ReadView BeginRead(out int slot)
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
            // Floor before mark: the main file never holds frames past a mark read later.
            var layout = Volatile.Read(ref _layout);
            long floor = Volatile.Read(ref _backfilled);
            long mark = Volatile.Read(ref _committedFrames);
            int files = layout.FilesFor(floor, mark);
            TestBeforeReadMark?.Invoke();
            int markSlot = _marks.Enter(mark, files);
            if (markSlot >= 0)
            {
                // A retire or switch publishes a new layout; a backfill past the mark publishes its target first.
                if (Volatile.Read(ref _retiring) == 0 && ReferenceEquals(Volatile.Read(ref _layout), layout)
                    && Volatile.Read(ref _backfillTarget) <= mark)
                {
                    slot = epochSlot | ((markSlot + 1) << 8);
                    return new ReadView(mark, floor, layout);
                }
                _marks.Exit(markSlot);
            }
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>
    /// Like <see cref="BeginRead"/>, but the writer also sees committed frames that are not yet durable.
    /// <paramref name="seenSeq"/> is what the writer must wait for (<see cref="WaitDurable"/>) before it completes,
    /// even without changes of its own, so nothing it read can vanish after it returns. Caller must hold the write lock.
    /// </summary>
    public ReadView BeginWrite(out long seenSeq, out int slot)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFlushFailed();
            var layout = _layout;
            long floor = Volatile.Read(ref _backfilled);
            long mark = _writtenFrames;
            // Retires check this under _gate. A backfill never passes the committed frames, so it cannot pass the mark.
            _writerFiles = layout.FilesFor(floor, mark);
            slot = _readers.Enter() | WriterSlot;
            seenSeq = _appendedSeq;
            return new ReadView(mark, floor, layout);
        }
    }

    public void EndRead(int slot)
    {
        int released = 0;
        if ((slot & WriterSlot) != 0) released = Interlocked.Exchange(ref _writerFiles, 0);
        else
        {
            int markSlot = (slot >> 8) - 1;
            if (markSlot >= 0) released = _marks.Exit(markSlot);
        }
        _readers.Exit(slot & 0xFF);
        // Pairs with the checkpointer, which sets _retireWait before it looks for readers of the old file.
        if ((released & Volatile.Read(ref _retireWait)) != 0 && Interlocked.Exchange(ref _retireWait, 0) != 0) _wake.Set();
    }

    /// <summary>Frames in WAL files that are not retired yet (0 after a complete checkpoint).</summary>
    public long WalFrameCount
    {
        get
        {
            lock (_gate)
            {
                var layout = _layout;
                return _writtenFrames - (layout.Old >= 0 ? layout.OldStart : layout.ActiveStart) + 1;
            }
        }
    }

    /// <summary>Returns the immutable image of <paramref name="pgno"/> as of snapshot <paramref name="view"/>. Callers must not mutate it.</summary>
    public byte[] ReadPage(uint pgno, in ReadView view) => ReadPage(pgno, view, admit: true, out _);

    /// <summary>
    /// Transactions that miss more than this many pages are treated as scans: their further misses are not added to
    /// the cache, so one large scan cannot evict the working set of everyone else.
    /// </summary>
    internal int CachedPages => _cache.Count;

    public int ScanMissThreshold => Math.Max(64, _options.CacheSizePages / 8);

    /// <summary>
    /// Like <see cref="ReadPage(uint, in ReadView)"/>. <paramref name="missed"/> reports a cache miss; with
    /// <paramref name="admit"/> false a missed page is returned without being cached.
    /// </summary>
    public byte[] ReadPage(uint pgno, in ReadView view, bool admit, out bool missed)
    {
        missed = false;
        long frame = 0;
        // Frames up to the floor were in the main file when the snapshot started, and no backfill since then could
        // write a page whose latest frame (up to the mark) is at or below the floor: the main file still holds it.
        if (view.Mark > view.Floor && _walIndex.TryGetValue(pgno, out var frames))
        {
            frame = frames.LatestAtOrBefore(view.Mark);
            if (frame <= view.Floor) frame = 0;
        }

        long cacheKey = frame > 0 ? frame : MainFileKey(pgno);
        if (_cache.TryGet(cacheKey, out var data)) return data;

        missed = true;
        data = _cache.RentPage();
        if (frame > 0)
        {
            int file = view.Layout.FileOf(frame);
            TestBeforeWalFrameRead?.Invoke(file);
            int n = RandomAccess.Read(_walHandles[file], data, FrameOffset(view.Layout, file, frame) + FrameHeaderSize);
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

    private long FrameOffset(WalLayout layout, int file, long frame) =>
        WalHeaderRegion + (frame - layout.StartOf(file)) * _frameSize;

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
        private long[] _items;
        private int _count;

        public FrameList() => _items = new long[2];

        private FrameList(ReadOnlySpan<long> frames)
        {
            _items = new long[Math.Max(2, (int)BitOperations.RoundUpToPowerOf2((uint)frames.Length))];
            frames.CopyTo(_items);
            _count = frames.Length;
        }

        public long First => _items[0];

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

        /// <summary>A new list with the frames after <paramref name="frame"/>, or null if there are none. Caller holds the append lock.</summary>
        public FrameList? After(long frame)
        {
            int i = 0;
            while (i < _count && _items[i] <= frame) i++;
            return i == _count ? null : new FrameList(_items.AsSpan(i, _count - i));
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

    // ---------------------------------------------------------------- commit

    /// <summary>
    /// Appends a transaction's dirty pages to the WAL. Must only be called by the single writer. Returns a sequence
    /// number the caller must pass to <see cref="WaitDurable"/> after releasing the write lock (0: nothing to wait for).
    /// </summary>
    public long Commit(IReadOnlyList<KeyValuePair<uint, byte[]>> pages, uint dbPageCount)
    {
        if (pages.Count == 0) return 0;
        long firstFrame;
        WalLayout layout;
        lock (_gate)
        {
            ThrowIfFlushFailed();
            firstFrame = _writtenFrames + 1;
            layout = _layout;
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

        var handle = _walHandles[layout.Active];
        long offset = FrameOffset(layout, layout.Active, firstFrame);
        if (TestTornWriteBytes is int torn)
        {
            TestTornWriteBytes = null;
            var flat = new byte[pages.Count * _frameSize];
            int pos = 0;
            foreach (var s in segments) { s.Span.CopyTo(flat.AsSpan(pos)); pos += s.Length; }
            RandomAccess.Write(handle, flat.AsSpan(0, Math.Min(torn, flat.Length)), offset);
            throw new IOException("Simulated torn write.");
        }
        RandomAccess.Write(handle, segments, offset);
        bool groupCommit = _options.Synchronous == SynchronousMode.Full;

        lock (_gate)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                uint pgno = pages[i].Key;
                AppendFrame(pgno, firstFrame + i);
                _cache.Add(firstFrame + i, pages[i].Value);
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
    /// the flusher and covers every frame written so far, so concurrent commits share one fsync. If the writer switched
    /// WAL files since the last flush, the old file is flushed too.
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
            // Frames appended before the fsync starts are covered by it, including other writers' commits. Files
            // holding non-durable frames cannot be retired meanwhile: backfills stop at the committed frames.
            WalLayout layout;
            lock (_gate) { upToSeq = _appendedSeq; upToFrame = _writtenFrames; layout = _layout; }
            if (layout.Old >= 0 && layout.OldEnd > Volatile.Read(ref _committedFrames)) _wal[layout.Old].Flush(flushToDisk: true);
            _wal[layout.Active].Flush(flushToDisk: true);
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

    private void Poison(Exception e)
    {
        lock (_flushGate) _flushFailure ??= e;
        lock (_progress) Monitor.PulseAll(_progress);
    }

    /// <summary>Chained frame checksum: XxHash64 of (frame header after the checksum, page), seeded with the previous one.</summary>
    private static ulong ChainHash(ulong previous, ReadOnlySpan<byte> header, ReadOnlySpan<byte> page)
    {
        var hasher = new XxHash64(unchecked((long)previous));
        hasher.Append(header);
        hasher.Append(page);
        return hasher.GetCurrentHashAsUInt64();
    }

    // ---------------------------------------------------------------- checkpoint

    /// <summary>
    /// Switches WAL files once the active one holds <see cref="FolioOptions.AutoCheckpointFrames"/> frames and the other
    /// one is empty, and lets the background checkpointer copy the old file into the main file. Costs the writer one
    /// small header write; while the old file is still being checkpointed, the active one simply keeps growing.
    /// Caller must hold the write lock.
    /// </summary>
    public void MaybeAutoCheckpoint()
    {
        int frames = _options.AutoCheckpointFrames;
        if (frames <= 0 || _disposed || Volatile.Read(ref _flushFailure) is not null) return;
        var layout = Volatile.Read(ref _layout);
        long active = _writtenFrames - layout.ActiveStart + 1;
        if (active < frames) return;
        if (!SwitchReady)
        {
            StartCheckpointer();
            Throttle(active, frames);
            if (!SwitchReady) return;
        }
        // A failure here poisons the pager; a commit reports it from WaitDurable, a rollback doesn't.
        try { Switch(); }
        catch (FolioException) { return; }
        StartCheckpointer();
    }

    // Backpressure limits, in files of AutoCheckpointFrames frames, and the longest wait of a throttled commit.
    private const int ThrottleSoftFiles = 1, ThrottleHardFiles = 6, ThrottleMaxDelayMs = 4;

    /// <summary>
    /// Backpressure, so the writer cannot outrun the checkpointer (the WAL would grow without bound): past a soft limit
    /// each commit waits a little, growing with the excess, for the old file to be retired; at a hard limit it waits
    /// until it is. Not when readers hold the checkpoint back (the last pass could not retire the file).
    /// </summary>
    private void Throttle(long active, int frames)
    {
        long soft = (long)frames * ThrottleSoftFiles, hard = (long)frames * ThrottleHardFiles;
        if (active < soft) return;
        bool block = active >= hard;
        int delay = block ? 50 : 1 + (int)((active - soft) * (ThrottleMaxDelayMs - 1) / (hard - soft));
        lock (_progress)
        {
            while (!SwitchReady && !_checkpointStalled && !_stopping && Volatile.Read(ref _flushFailure) is null)
            {
                Monitor.Wait(_progress, delay);
                if (!block) return;
            }
        }
    }

    /// <summary>The old file is retired and the other file is empty and ready to become the active one.</summary>
    private bool SwitchReady => _otherReady && Volatile.Read(ref _layout).Old < 0;

    private void StartCheckpointer()
    {
        if (_checkpointer is null)
        {
            _checkpointer = new Thread(CheckpointerLoop) { IsBackground = true, Name = "FolioDb checkpointer" };
            _checkpointer.Start();
        }
        _wake.Set();
    }

    /// <summary>
    /// Makes the empty inactive file the active one: the frames written so far become the old file. The header is
    /// written before the switch is published (without fsync: frames written after it are flushed with it, and until
    /// then recovery ignores them). Caller must hold the write lock.
    /// </summary>
    private void Switch()
    {
        var layout = _layout;
        int next = 1 - layout.Active;
        Span<byte> salts = stackalloc byte[8];
        RandomNumberGenerator.Fill(salts);
        uint salt1 = BinaryPrimitives.ReadUInt32LittleEndian(salts);
        uint salt2 = BinaryPrimitives.ReadUInt32LittleEndian(salts[4..]);
        try
        {
            WriteRecord(next, StateActive, _fileSeq[layout.Active] + 1, salt1, salt2, _chain);
        }
        catch (Exception e)
        {
            Poison(e);
            throw new FolioException("Switching WAL files failed; reopen the database.", e);
        }
        lock (_gate)
        {
            _salt1 = salt1;
            _salt2 = salt2;
            _otherReady = false;
            _checkpointStalled = false;
            Volatile.Write(ref _layout, new WalLayout(next, _writtenFrames + 1, layout.Active, layout.ActiveStart, _writtenFrames));
        }
    }

    private void CheckpointerLoop()
    {
        int backoff = 0;
        while (true)
        {
            _wake.Wait(backoff == 0 ? Timeout.Infinite : 1 << backoff);
            _wake.Reset();
            if (_stopping) return;
            bool done;
            try
            {
                // Never waits for readers while holding the lock: an explicit checkpoint may be waiting for it.
                lock (_checkpointLock)
                {
                    if (_stopping) return;
                    done = CheckpointOldFile();
                }
            }
            catch (Exception e)
            {
                Poison(e);
                return;
            }
            backoff = done ? 0 : Math.Min(backoff + 1, 3);
        }
    }

    /// <summary>Backfills and retires the old WAL file. Returns false if readers still need part of it. Caller holds the checkpoint lock.</summary>
    private bool CheckpointOldFile()
    {
        var layout = Volatile.Read(ref _layout);
        if (layout.Old < 0) return true;
        Interlocked.Exchange(ref _retireWait, 1 << layout.Old);
        if (Volatile.Read(ref _backfilled) < layout.OldEnd) Backfill(layout.OldEnd);
        bool retired = TryRetireOld();
        if (retired) Volatile.Write(ref _retireWait, 0);
        // Only snapshots stall it: the writer's own pin is gone by the time it throttles.
        _checkpointStalled = !retired && (Volatile.Read(ref _backfilled) < layout.OldEnd || _marks.AnyReading(1 << layout.Old));
        lock (_progress) Monitor.PulseAll(_progress);
        return retired;
    }

    /// <summary>
    /// Copies every committed frame into the main file and retires both WAL files' frames, switching files if needed.
    /// Never waits for readers: returns false if a reader's snapshot still needs the WAL (frames it allows are still
    /// copied). Caller must hold the write lock.
    /// </summary>
    public bool TryCheckpoint()
    {
        lock (_checkpointLock)
        {
            ThrowIfFlushFailed();
            if (_options.Synchronous == SynchronousMode.Full && _writtenFrames > Volatile.Read(ref _backfilled)) FlushWritten();
            Backfill(long.MaxValue);
            TryRetireOld();
            var layout = _layout;
            if (layout.Old < 0 && _writtenFrames >= layout.ActiveStart && Volatile.Read(ref _backfilled) == _writtenFrames && _otherReady)
            {
                Switch();
                TryRetireOld();
            }
            layout = _layout;
            return layout.Old < 0 && _writtenFrames < layout.ActiveStart;
        }
    }

    /// <summary>
    /// Copies the latest frame (up to <paramref name="limit"/>, the committed frames and the oldest active snapshot) of
    /// every page into the main file. Readers keep running: each active one has a mark at or past the target, so it
    /// reads these pages from the WAL (they have frames past its floor), and none of them reads or caches their
    /// main-file images meanwhile. The writer only appends frames past the target. Caller holds the checkpoint lock.
    /// </summary>
    private void Backfill(long limit)
    {
        long prev = _backfilled;
        long target = Math.Min(limit, Volatile.Read(ref _committedFrames));
        if (target <= prev) return;
        while (true)
        {
            Interlocked.Exchange(ref _backfillTarget, target);
            long min = _marks.MinActive();
            if (min >= target) break;
            // Readers of older snapshots limit how far it goes; marks at or below prev need nothing more.
            long lowered = Math.Max(min, prev);
            if (lowered == prev)
            {
                Interlocked.Exchange(ref _backfillTarget, prev);
                return;
            }
            target = lowered;
        }

        // Every frame up to the target is in one of these files (only this thread retires files).
        WalLayout layout;
        long written;
        lock (_gate) { layout = _layout; written = _writtenFrames; }

        // The main file must never hold a page whose WAL frame could still be lost (Full: committed frames are durable).
        if (_options.Synchronous == SynchronousMode.Normal && target > _walSynced)
        {
            if (layout.Old >= 0 && layout.OldEnd > _walSynced) _wal[layout.Old].Flush(flushToDisk: true);
            if (target >= layout.ActiveStart) _wal[layout.Active].Flush(flushToDisk: true);
            _walSynced = target >= layout.ActiveStart ? written : layout.OldEnd;
        }

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
            // readers whose floor covers them. Cached arrays may be recycled once evicted, so copy them while
            // registered in an epoch.
            int epochSlot = _readers.Enter();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var (pgno, frame) = pages[start + i];
                    if (_cache.TryGet(frame, out var cached))
                    {
                        var copy = new byte[PageSize];
                        cached.CopyTo(copy, 0);
                        images[i] = copy;
                    }
                    else
                    {
                        var image = buffer.AsMemory(i * PageSize, PageSize);
                        int file = layout.FileOf(frame);
                        if (RandomAccess.Read(_walHandles[file], image.Span, FrameOffset(layout, file, frame) + FrameHeaderSize) != PageSize)
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
    }

    /// <summary>
    /// Retires the old WAL file once it is fully backfilled and no snapshot (nor the writer) reads from it: drops its
    /// frames from the index, publishes a layout without it, then persists a "retired" header and empties the file,
    /// which makes it ready for the next switch. Caller holds the checkpoint lock.
    /// </summary>
    private bool TryRetireOld()
    {
        var layout = Volatile.Read(ref _layout);
        if (layout.Old < 0) return true;
        if (Volatile.Read(ref _backfilled) < layout.OldEnd) return false;
        int file = layout.Old;
        int bit = 1 << file;
        lock (_gate)
        {
            Interlocked.Exchange(ref _retiring, 1);
            try
            {
                if ((_writerFiles & bit) != 0 || _marks.AnyReading(bit)) return false;
                TestAfterRetireScan?.Invoke();
                foreach (var (pgno, list) in _walIndex)
                {
                    if (list.First > layout.OldEnd) continue;
                    var rest = list.After(layout.OldEnd);
                    if (rest is null) _walIndex.TryRemove(pgno, out _);
                    else _walIndex[pgno] = rest;
                }
                Volatile.Write(ref _layout, new WalLayout(layout.Active, layout.ActiveStart));
            }
            finally
            {
                Volatile.Write(ref _retiring, 0);
            }
        }

        // Nobody reads the file any more. The header is written to the slot that does not hold its "active" record, so
        // a torn write leaves that one (and the frames, until the truncation) for recovery.
        try
        {
            long offset = WriteRecord(file, StateRetired, _fileSeq[file], 0, 0, 0);
            TestAfterRetireRecord?.Invoke(file, offset);
            if (_options.Synchronous != SynchronousMode.Off) _wal[file].Flush(flushToDisk: true);
            _wal[file].SetLength(WalHeaderRegion);
        }
        catch (Exception e)
        {
            Poison(e);
            throw new FolioException("Retiring a WAL file failed; reopen the database.", e);
        }
        _otherReady = true;
        return true;
    }

    // ---------------------------------------------------------------- WAL file headers

    private readonly record struct WalRecord(int State, long Epoch, long Seq, uint Salt1, uint Salt2, ulong Seed);

    /// <summary>
    /// Header record: magic, page size, state, epoch (global write counter: the newer slot wins), sequence (consecutive
    /// active files have consecutive numbers), salts and chain seed of the frames, checksum.
    /// </summary>
    private long WriteRecord(int file, int state, long seq, uint salt1, uint salt2, ulong seed)
    {
        Span<byte> r = stackalloc byte[RecordSize];
        WalMagic.CopyTo(r);
        BinaryPrimitives.WriteInt32LittleEndian(r[8..], PageSize);
        BinaryPrimitives.WriteInt32LittleEndian(r[12..], state);
        BinaryPrimitives.WriteInt64LittleEndian(r[16..], Interlocked.Increment(ref _recordEpoch));
        BinaryPrimitives.WriteInt64LittleEndian(r[24..], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(r[32..], salt1);
        BinaryPrimitives.WriteUInt32LittleEndian(r[36..], salt2);
        BinaryPrimitives.WriteUInt64LittleEndian(r[40..], seed);
        BinaryPrimitives.WriteUInt64LittleEndian(r[48..], XxHash64.HashToUInt64(r[..48]));
        int slot = _nextRecordSlot[file];
        RandomAccess.Write(_walHandles[file], r, (long)slot * RecordSlotSize);
        _nextRecordSlot[file] = 1 - slot;
        _fileSeq[file] = seq;
        return (long)slot * RecordSlotSize;
    }

    /// <summary>The newest valid header record of a WAL file; the next write goes to the other slot.</summary>
    private WalRecord? ReadRecord(int file)
    {
        Span<byte> r = stackalloc byte[RecordSize];
        WalRecord? best = null;
        int bestSlot = -1;
        for (int slot = 0; slot < 2; slot++)
        {
            if (RandomAccess.Read(_walHandles[file], r, (long)slot * RecordSlotSize) != RecordSize) continue;
            if (!r[..8].SequenceEqual(WalMagic)
                || BinaryPrimitives.ReadInt32LittleEndian(r[8..]) != PageSize
                || BinaryPrimitives.ReadUInt64LittleEndian(r[48..]) != XxHash64.HashToUInt64(r[..48]))
                continue;
            int state = BinaryPrimitives.ReadInt32LittleEndian(r[12..]);
            if (state is not (StateActive or StateRetired)) continue;
            var record = new WalRecord(
                state,
                BinaryPrimitives.ReadInt64LittleEndian(r[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(r[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[32..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[36..]),
                BinaryPrimitives.ReadUInt64LittleEndian(r[40..]));
            if (best is null || record.Epoch > best.Value.Epoch)
            {
                best = record;
                bestSlot = slot;
            }
        }
        _nextRecordSlot[file] = bestSlot < 0 ? 0 : 1 - bestSlot;
        return best;
    }

    // ---------------------------------------------------------------- recovery

    /// <summary>
    /// Replays committed frames into the main file and starts with empty WAL files. An active file is replayed when its
    /// predecessor (sequence - 1) is accounted for: retired (so it was fully backfilled), or active and replayed first
    /// with a chain that ends exactly at this file's seed. Otherwise its frames may depend on lost ones and are dropped
    /// (Normal: a file whose frames reached the main file was flushed first; Full: commits are flushed in order).
    /// </summary>
    private void RecoverWal()
    {
        var r0 = ReadRecord(0);
        var r1 = ReadRecord(1);
        _recordEpoch = Math.Max(r0?.Epoch ?? 0, r1?.Epoch ?? 0);
        long maxSeq = Math.Max(r0?.Seq ?? 0, r1?.Seq ?? 0);

        long legacySeq = RecoverLegacyWal();
        if (legacySeq >= 0)
        {
            ResetWal(Math.Max(maxSeq, legacySeq));
            return;
        }

        var replay = new List<int>(2);
        bool a0 = r0 is { State: StateActive }, a1 = r1 is { State: StateActive };
        if (a0 && a1)
        {
            int lower = r0!.Value.Seq <= r1!.Value.Seq ? 0 : 1;
            replay.Add(lower);
            replay.Add(1 - lower);
        }
        else if (a0 || a1)
        {
            int y = a0 ? 0 : 1;
            var other = y == 0 ? r1 : r0;
            var record = (y == 0 ? r0 : r1)!.Value;
            // Only its actual predecessor: a newer retired record means this file was superseded (e.g. a crash between
            // the two header writes of a reset), and an older one means its predecessor's header was lost.
            if (other is { State: StateRetired } o && o.Seq + 1 == record.Seq) replay.Add(y);
        }

        long next = 1;
        ulong chain = 0;
        int oldFile = -1, activeFile = 0;
        long oldStart = 0, oldEnd = 0, activeStart = 1;
        for (int i = 0; i < replay.Count; i++)
        {
            int file = replay[i];
            var record = (file == 0 ? r0 : r1)!.Value;
            if (i > 0)
            {
                var previous = (replay[0] == 0 ? r0 : r1)!.Value;
                if (record.Seq != previous.Seq + 1 || record.Seed != chain) break;
                oldFile = activeFile;
                oldStart = activeStart;
                oldEnd = next - 1;
            }
            activeFile = file;
            activeStart = next;
            chain = record.Seed;
            next += ReplayFile(file, record, next, ref chain);
        }

        _layout = oldFile >= 0
            ? new WalLayout(activeFile, activeStart, oldFile, oldStart, oldEnd)
            : new WalLayout(activeFile, activeStart);
        _committedFrames = _writtenFrames = next - 1;

        if (_writtenFrames == 0 && replay.Count > 0 && oldFile < 0 && (activeFile == 0 ? r1 : r0) is { State: StateRetired }
            && _wal[0].Length <= WalHeaderRegion && _wal[1].Length <= WalHeaderRegion)
        {
            // Nothing to replay and the files are already a clean pair: keep them (no header writes on a clean open).
            // Not with frames that failed validation: new commits with the same salts could reconnect them.
            var record = (activeFile == 0 ? r0 : r1)!.Value;
            _fileSeq[activeFile] = record.Seq;
            _fileSeq[1 - activeFile] = (activeFile == 0 ? r1 : r0)!.Value.Seq;
            _salt1 = record.Salt1;
            _salt2 = record.Salt2;
            _chain = record.Seed;
            _otherReady = true;
            return;
        }

        if (_writtenFrames > 0) Backfill(long.MaxValue);
        ResetWal(maxSeq);
    }

    /// <summary>Replays one WAL file's committed frames as frames <paramref name="first"/>...; returns how many.</summary>
    private long ReplayFile(int file, WalRecord record, long first, ref ulong chain)
    {
        var frame = new byte[_frameSize];
        var pending = new List<(uint Pgno, long Frame)>();
        long length = _wal[file].Length;
        long committed = 0;
        ulong running = chain;
        for (long i = 0; WalHeaderRegion + (i + 1) * _frameSize <= length; i++)
        {
            if (RandomAccess.Read(_walHandles[file], frame, WalHeaderRegion + i * _frameSize) != _frameSize) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)) != record.Salt1
                || BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)) != record.Salt2) break;
            ulong expected = XxHash64.HashToUInt64(frame.AsSpan(8), unchecked((long)running));
            if (BinaryPrimitives.ReadUInt64LittleEndian(frame) != expected) break;
            running = expected;
            pending.Add((BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)), first + i));
            if (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)) != 0)
            {
                foreach (var (pgno, f) in pending) AppendFrame(pgno, f);
                pending.Clear();
                committed = i + 1;
                chain = running;
            }
        }
        return committed;
    }

    /// <summary>
    /// A WAL written by the single-file format (v1 header in <c>-wal</c>): copies its committed frames into the main
    /// file directly. Returns its checkpoint sequence, or -1 if there is none. Until <see cref="ResetWal"/> overwrites
    /// the header, a crash simply replays it again.
    /// </summary>
    private long RecoverLegacyWal()
    {
        Span<byte> h = stackalloc byte[LegacyHeaderSize];
        if (_wal[0].Length < LegacyHeaderSize
            || RandomAccess.Read(_walHandles[0], h, 0) != LegacyHeaderSize
            || !h[..8].SequenceEqual(LegacyWalMagic)
            || BinaryPrimitives.ReadInt32LittleEndian(h[8..]) != 1
            || BinaryPrimitives.ReadInt32LittleEndian(h[12..]) != PageSize
            || BinaryPrimitives.ReadUInt32LittleEndian(h[28..]) != (uint)XxHash64.HashToUInt64(h[..28]))
            return -1;

        uint salt1 = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        uint salt2 = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        long seq = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
        ulong chain = ((ulong)salt1 << 32) | salt2;
        var frame = new byte[_frameSize];
        var pending = new List<(uint Pgno, long Offset)>();
        var latest = new Dictionary<uint, long>();
        long length = _wal[0].Length;
        for (long offset = LegacyHeaderSize; offset + _frameSize <= length; offset += _frameSize)
        {
            if (RandomAccess.Read(_walHandles[0], frame, offset) != _frameSize) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)) != salt1
                || BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)) != salt2) break;
            ulong expected = XxHash64.HashToUInt64(frame.AsSpan(8), unchecked((long)chain));
            if (BinaryPrimitives.ReadUInt64LittleEndian(frame) != expected) break;
            chain = expected;
            pending.Add((BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)), offset));
            if (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)) != 0)
            {
                foreach (var (pgno, o) in pending) latest[pgno] = o;
                pending.Clear();
            }
        }
        if (latest.Count > 0)
        {
            var page = new byte[PageSize];
            foreach (var (pgno, offset) in latest)
            {
                if (RandomAccess.Read(_walHandles[0], page, offset + FrameHeaderSize) != PageSize)
                    throw new CorruptDatabaseException("Short read of a legacy WAL frame.");
                RandomAccess.Write(_dbHandle, page, (long)pgno * PageSize);
            }
            if (_options.Synchronous != SynchronousMode.Off) _db.Flush(flushToDisk: true);
        }
        // The next write to -wal goes to slot 0, over the legacy header.
        _nextRecordSlot[0] = 0;
        return seq;
    }

    /// <summary>
    /// Starts over with an empty active <c>-wal</c> and a retired <c>-wal2</c>, once the main file holds everything.
    /// The retired record goes first: until the active one is written, recovery finds nothing (or the same frames) to
    /// replay.
    /// </summary>
    private void ResetWal(long maxSeq)
    {
        _walIndex.Clear();
        _committedFrames = _writtenFrames = 0;
        _backfilled = _backfillTarget = _walSynced = 0;

        Span<byte> salts = stackalloc byte[8];
        RandomNumberGenerator.Fill(salts);
        _salt1 = BinaryPrimitives.ReadUInt32LittleEndian(salts);
        _salt2 = BinaryPrimitives.ReadUInt32LittleEndian(salts[4..]);
        _chain = ((ulong)_salt1 << 32) | _salt2;
        bool sync = _options.Synchronous != SynchronousMode.Off;
        WriteRecord(1, StateRetired, maxSeq + 1, 0, 0, 0);
        if (sync) _wal[1].Flush(flushToDisk: true);
        WriteRecord(0, StateActive, maxSeq + 2, _salt1, _salt2, _chain);
        if (sync) _wal[0].Flush(flushToDisk: true);
        _wal[0].SetLength(WalHeaderRegion);
        _wal[1].SetLength(WalHeaderRegion);
        _layout = new WalLayout(0, 1);
        _otherReady = true;
    }

    public long DatabaseFileLength => _db.Length;
    public long WalFileLength => _wal[0].Length + _wal[1].Length;

    private void StopCheckpointer()
    {
        _stopping = true;
        _wake.Set();
        lock (_progress) Monitor.PulseAll(_progress);
        _checkpointer?.Join();
    }

    /// <summary>Test hook: drop file handles without checkpointing, as if the process had crashed.</summary>
    internal void SimulateCrash()
    {
        lock (_gate) _disposed = true;
        StopCheckpointer();
        _wal[0].Dispose();
        _wal[1].Dispose();
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
            StopCheckpointer();
            if (Volatile.Read(ref _flushFailure) is null && !TryCheckpoint() && _options.Synchronous != SynchronousMode.Off)
            {
                // Full: also waits for an in-flight group flush and publishes durability, so committers still
                // waiting take the fast path instead of flushing a disposed stream.
                if (_options.Synchronous == SynchronousMode.Full) FlushWritten();
                else
                {
                    _wal[0].Flush(flushToDisk: true);
                    _wal[1].Flush(flushToDisk: true);
                }
            }
        }
        finally
        {
            lock (_gate) _disposed = true;
            _wal[0].Dispose();
            _wal[1].Dispose();
            _db.Dispose();
            // _wake is not disposed: a snapshot ending late may still set it (it owns no kernel handle).
        }
    }
}

/// <summary>
/// Which WAL file holds which frames: the active file (frames from <see cref="ActiveStart"/> on) and, until it is
/// retired, the old file (<see cref="OldStart"/>..<see cref="OldEnd"/>). Immutable; a switch or retire publishes a new one.
/// </summary>
internal sealed class WalLayout(int active, long activeStart, int old = -1, long oldStart = 0, long oldEnd = 0)
{
    public int Active { get; } = active;
    public long ActiveStart { get; } = activeStart;
    public int Old { get; } = old;
    public long OldStart { get; } = oldStart;
    public long OldEnd { get; } = oldEnd;

    public int FileOf(long frame) => Old >= 0 && frame <= OldEnd ? Old : Active;

    public long StartOf(int file) => file == Old ? OldStart : ActiveStart;

    /// <summary>Files (bit per file) holding frames in (<paramref name="floor"/>, <paramref name="mark"/>].</summary>
    public int FilesFor(long floor, long mark)
    {
        if (mark <= floor) return 0;
        int files = 0;
        if (Old >= 0 && floor < OldEnd && mark >= OldStart) files |= 1 << Old;
        if (mark >= ActiveStart) files |= 1 << Active;
        return files;
    }
}

/// <summary>A snapshot: the last visible frame, the frames already in the main file when it started, and the WAL layout.</summary>
internal readonly struct ReadView(long mark, long floor, WalLayout layout)
{
    public long Mark { get; } = mark;
    public long Floor { get; } = floor;
    public WalLayout Layout { get; } = layout;
}
