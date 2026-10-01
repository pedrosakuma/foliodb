using System.Buffers.Binary;
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
/// they started) and never block the single writer. Checkpoints copy the latest frames back into the main
/// file and reset the WAL; they only run when no reader holds a snapshot.
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
    // writer, under _gate) between checkpoints, and a checkpoint clears it only while no reader holds a snapshot.
    private readonly ConcurrentDictionary<uint, FrameList> _walIndex = new();
    private long _committedFrames;   // durable and visible to readers
    private long _writtenFrames;     // appended to the WAL; visible to the writer
    // Frames ever appended / made durable. Monotonic (unlike frame numbers, which restart at each checkpoint).
    private long _appendedSeq;
    private long _durableSeq;
    private readonly object _flushGate = new();
    private bool _flushing;
    private Exception? _flushFailure;
    private ulong _chain;
    private uint _salt1, _salt2, _checkpointSeq;
    private int _activeReaders;
    private bool _disposed;

    public int PageSize { get; }
    public string Path { get; }

    /// <summary>Test hook: when set, the next commit writes only this many WAL bytes and then fails (torn write).</summary>
    internal int? TestTornWriteBytes;

    /// <summary>Transactions appended to the WAL (diagnostics and tests).</summary>
    internal long AppendedCommits { get { lock (_gate) return _appendedCommits; } }
    private long _appendedCommits;

    /// <summary>Test hook: runs on the flushing thread before it decides which frames its fsync covers.</summary>
    internal Action? TestBeforeFlush;

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
        _cache = new PageCache(options.CacheSizePages);
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

    public long BeginRead()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeReaders++;
            return _committedFrames;
        }
    }

    /// <summary>
    /// Like <see cref="BeginRead"/>, but the writer also sees committed frames that are not yet durable.
    /// <paramref name="seenSeq"/> is what the writer must wait for (<see cref="WaitDurable"/>) before it completes,
    /// even without changes of its own, so nothing it read can vanish after it returns.
    /// </summary>
    public long BeginWrite(out long seenSeq)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFlushFailed();
            _activeReaders++;
            seenSeq = _appendedSeq;
            return _writtenFrames;
        }
    }

    public void EndRead()
    {
        lock (_gate) _activeReaders--;
    }

    public long WalFrameCount
    {
        get { lock (_gate) return _writtenFrames; }
    }

    /// <summary>Returns the immutable image of <paramref name="pgno"/> as of snapshot <paramref name="mark"/>. Callers must not mutate it.</summary>
    public byte[] ReadPage(uint pgno, long mark)
    {
        long frame = 0;
        // Frame numbers start at 1, so an empty snapshot (mark 0) reads straight from the main file.
        if (mark > 0 && _walIndex.TryGetValue(pgno, out var frames)) frame = frames.LatestAtOrBefore(mark);

        long cacheKey = frame > 0 ? frame : -(long)pgno - 1;
        if (_cache.TryGet(cacheKey, out var data)) return data;

        data = new byte[PageSize];
        if (frame > 0)
        {
            int n = RandomAccess.Read(_walHandle, data, FrameOffset(frame) + FrameHeaderSize);
            if (n != PageSize) throw new CorruptDatabaseException($"Short read of WAL frame {frame}.");
        }
        else
        {
            RandomAccess.Read(_dbHandle, data, (long)pgno * PageSize); // pages past EOF read as zeros
        }
        _cache.Add(cacheKey, data);
        return data;
    }

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

        public long Last => _items[_count - 1];

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
                _cache.Add(firstFrame + i, pages[i].Value);
            }
            _writtenFrames = firstFrame + pages.Count - 1;
            _appendedSeq += pages.Count;
            _appendedCommits++;
            if (!groupCommit)
            {
                // Normal/Off: durability is not per commit, so the frames are published right away.
                _committedFrames = _writtenFrames;
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
                lock (_gate) _committedFrames = upToFrame;
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

    /// <summary>Runs a checkpoint when the WAL exceeds the configured size. Caller must hold the write lock.</summary>
    public void MaybeAutoCheckpoint()
    {
        if (_options.AutoCheckpointFrames <= 0 || Volatile.Read(ref _flushFailure) is not null || WalFrameCount < _options.AutoCheckpointFrames)
            return;
        // A flush failure here is recorded (poisoning the pager); a commit reports it from WaitDurable, a rollback doesn't.
        try { TryCheckpoint(); }
        catch (FolioException) when (Volatile.Read(ref _flushFailure) is not null) { }
    }

    // ---------------------------------------------------------------- checkpoint

    /// <summary>
    /// Copies committed WAL frames into the main file and resets the WAL. Returns false if readers are active.
    /// Caller must hold the write lock (no concurrent WAL appends).
    /// </summary>
    public bool TryCheckpoint()
    {
        // Readers block a checkpoint anyway: don't fsync under the write lock for nothing (it would serialize commits).
        lock (_gate) if (_activeReaders > 0) return false;
        FlushWritten();
        lock (_gate)
        {
            if (_activeReaders > 0) return false;
            if (_committedFrames == 0) return true;

            var pgnos = _walIndex.Keys.ToArray();
            Array.Sort(pgnos);

            // Latest image of each page, taken from the cache when still resident; runs of
            // consecutive page numbers go to the main file in a single gather write.
            var run = new List<ReadOnlyMemory<byte>>();
            uint runStart = 0;
            foreach (uint pgno in pgnos)
            {
                if (run.Count > 0 && pgno != runStart + (uint)run.Count)
                {
                    RandomAccess.Write(_dbHandle, run, (long)runStart * PageSize);
                    run.Clear();
                }
                if (run.Count == 0) runStart = pgno;
                run.Add(LatestCommittedImage(_walIndex[pgno].Last));
            }
            if (run.Count > 0) RandomAccess.Write(_dbHandle, run, (long)runStart * PageSize);

            if (_options.Synchronous != SynchronousMode.Off) _db.Flush(flushToDisk: true);

            _checkpointSeq++;
            WriteNewWalHeader();
            _walIndex.Clear();
            _committedFrames = 0;
            _writtenFrames = 0;
            _cache.Clear();
            return true;
        }
    }

    private byte[] LatestCommittedImage(long frame)
    {
        if (_cache.TryGet(frame, out var cached)) return cached;
        var page = new byte[PageSize];
        if (RandomAccess.Read(_walHandle, page, FrameOffset(frame) + FrameHeaderSize) != PageSize)
            throw new CorruptDatabaseException($"Short read of WAL frame {frame} during checkpoint.");
        return page;
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
