using System.Buffers.Binary;
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
    private readonly Dictionary<uint, List<long>> _walIndex = new();
    private long _committedFrames;
    private ulong _chain;
    private uint _salt1, _salt2, _checkpointSeq;
    private int _activeReaders;
    private bool _disposed;

    public int PageSize { get; }
    public string Path { get; }

    /// <summary>Test hook: when set, the next commit writes only this many WAL bytes and then fails (torn write).</summary>
    internal int? TestTornWriteBytes;

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

    public void EndRead()
    {
        lock (_gate) _activeReaders--;
    }

    public long WalFrameCount
    {
        get { lock (_gate) return _committedFrames; }
    }

    /// <summary>Returns the immutable image of <paramref name="pgno"/> as of snapshot <paramref name="mark"/>. Callers must not mutate it.</summary>
    public byte[] ReadPage(uint pgno, long mark)
    {
        long frame = 0;
        lock (_gate)
        {
            if (_walIndex.TryGetValue(pgno, out var frames)) frame = LatestFrameAtOrBefore(frames, mark);
        }

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

    private static long LatestFrameAtOrBefore(List<long> frames, long mark)
    {
        int lo = 0, hi = frames.Count - 1;
        long result = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (frames[mid] <= mark)
            {
                result = frames[mid];
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return result;
    }

    private long FrameOffset(long frame) => WalHeaderSize + (frame - 1) * _frameSize;

    // ---------------------------------------------------------------- commit

    /// <summary>Appends a transaction's dirty pages to the WAL. Must only be called by the single writer.</summary>
    public void Commit(IReadOnlyList<KeyValuePair<uint, byte[]>> pages, uint dbPageCount)
    {
        if (pages.Count == 0) return;
        long firstFrame;
        lock (_gate) firstFrame = _committedFrames + 1;

        var buf = new byte[pages.Count * _frameSize];
        ulong chain = _chain;
        for (int i = 0; i < pages.Count; i++)
        {
            var frame = buf.AsSpan(i * _frameSize, _frameSize);
            BinaryPrimitives.WriteUInt32LittleEndian(frame[8..], pages[i].Key);
            BinaryPrimitives.WriteUInt32LittleEndian(frame[12..], i == pages.Count - 1 ? dbPageCount : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(frame[16..], _salt1);
            BinaryPrimitives.WriteUInt32LittleEndian(frame[20..], _salt2);
            pages[i].Value.CopyTo(frame[FrameHeaderSize..]);
            chain = XxHash64.HashToUInt64(frame[8..], unchecked((long)chain));
            BinaryPrimitives.WriteUInt64LittleEndian(frame, chain);
        }

        long offset = FrameOffset(firstFrame);
        if (TestTornWriteBytes is int torn)
        {
            TestTornWriteBytes = null;
            RandomAccess.Write(_walHandle, buf.AsSpan(0, Math.Min(torn, buf.Length)), offset);
            throw new IOException("Simulated torn write.");
        }
        RandomAccess.Write(_walHandle, buf, offset);
        if (_options.Synchronous == SynchronousMode.Full) _wal.Flush(flushToDisk: true);

        lock (_gate)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                uint pgno = pages[i].Key;
                if (!_walIndex.TryGetValue(pgno, out var list)) _walIndex[pgno] = list = new List<long>(2);
                list.Add(firstFrame + i);
                _cache.Add(firstFrame + i, pages[i].Value);
            }
            _committedFrames = firstFrame + pages.Count - 1;
            _chain = chain;
        }

    }

    /// <summary>Runs a checkpoint when the WAL exceeds the configured size. Caller must hold the write lock.</summary>
    public void MaybeAutoCheckpoint()
    {
        if (_options.AutoCheckpointFrames > 0 && WalFrameCount >= _options.AutoCheckpointFrames) TryCheckpoint();
    }

    // ---------------------------------------------------------------- checkpoint

    /// <summary>
    /// Copies committed WAL frames into the main file and resets the WAL. Returns false if readers are active.
    /// Caller must hold the write lock (no concurrent WAL appends).
    /// </summary>
    public bool TryCheckpoint()
    {
        lock (_gate)
        {
            if (_activeReaders > 0) return false;
            if (_committedFrames == 0) return true;

            var page = new byte[PageSize];
            foreach (var (pgno, frames) in _walIndex.OrderBy(static kv => kv.Key))
            {
                long frame = frames[^1];
                if (RandomAccess.Read(_walHandle, page, FrameOffset(frame) + FrameHeaderSize) != PageSize)
                    throw new CorruptDatabaseException($"Short read of WAL frame {frame} during checkpoint.");
                RandomAccess.Write(_dbHandle, page, (long)pgno * PageSize);
            }

            if (_options.Synchronous != SynchronousMode.Off) _db.Flush(flushToDisk: true);

            _checkpointSeq++;
            WriteNewWalHeader();
            _walIndex.Clear();
            _committedFrames = 0;
            _cache.Clear();
            return true;
        }
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
                    if (!_walIndex.TryGetValue(pgno, out var list)) _walIndex[pgno] = list = new List<long>(2);
                    list.Add(fr);
                }
                pending.Clear();
                _committedFrames = f;
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
            if (!TryCheckpoint() && _options.Synchronous != SynchronousMode.Off) _wal.Flush(flushToDisk: true);
        }
        finally
        {
            lock (_gate) _disposed = true;
            _wal.Dispose();
            _db.Dispose();
        }
    }
}
