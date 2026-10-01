using System.Buffers.Binary;

namespace FolioDb.Storage;

/// <summary>
/// Page-level transaction. Reads see a consistent snapshot; writes are buffered as private page copies
/// (copy-on-write) and become visible atomically at <see cref="Commit"/>.
/// </summary>
internal sealed class StorageTx : IDisposable
{
    private readonly Pager _pager;
    private readonly long _mark;
    private readonly Dictionary<uint, byte[]>? _dirty;
    // Clean page images already resolved at _mark. They are immutable and fixed for the snapshot, so repeated
    // descents (root, interior pages) skip the pager's WAL index and LRU locks. Started only after a few pager
    // reads, so short transactions allocate nothing, and bounded so long scans don't pin evicted pages.
    private Dictionary<uint, byte[]>? _clean;
    private int _pagerReads;
    private const int CleanMemoAfterReads = 8;
    private const int CleanMemoCapacity = 256;
    private readonly Action? _onDispose;
    private DbHeader _header;
    private bool _headerDirty;
    private bool _finished;

    public bool IsWritable { get; }
    public int PageSize => _pager.PageSize;
    public BTreeDeleteRebalanceMode DeleteRebalance { get; }

    public StorageTx(
        Pager pager,
        bool writable,
        Action? onDispose = null,
        BTreeDeleteRebalanceMode deleteRebalance = BTreeDeleteRebalanceMode.None)
    {
        _pager = pager;
        IsWritable = writable;
        _onDispose = onDispose;
        DeleteRebalance = deleteRebalance;
        _mark = pager.BeginRead();
        try
        {
            _header = DbHeader.Read(pager.ReadPage(0, _mark));
        }
        catch
        {
            pager.EndRead();
            throw;
        }
        if (writable) _dirty = new Dictionary<uint, byte[]>();
    }

    public uint CatalogRoot => _header.CatalogRoot;
    public uint PageCount => _header.PageCount;
    public uint FreePageCount => _header.FreePageCount;

    /// <summary>Page image for reading. Must not be mutated.</summary>
    public byte[] ReadPage(uint pgno)
    {
        ThrowIfFinished();
        if (_dirty is not null && _dirty.TryGetValue(pgno, out var d)) return d;
        return ReadClean(pgno);
    }

    private byte[] ReadClean(uint pgno)
    {
        if (_clean is not null && _clean.TryGetValue(pgno, out var c)) return c;
        if (pgno >= _header.PageCount) throw new CorruptDatabaseException($"Page {pgno} is out of range ({_header.PageCount} pages).");
        var page = _pager.ReadPage(pgno, _mark);
        if (_clean is not null)
        {
            if (_clean.Count < CleanMemoCapacity) _clean[pgno] = page;
        }
        else if (++_pagerReads >= CleanMemoAfterReads) _clean = new Dictionary<uint, byte[]>(32);
        return page;
    }

    /// <summary>Private, mutable copy of a page owned by this transaction.</summary>
    public byte[] WritePage(uint pgno)
    {
        ThrowIfFinished();
        if (_dirty is null) throw new InvalidOperationException("Transaction is read-only.");
        if (_dirty.TryGetValue(pgno, out var d)) return d;
        var copy = (byte[])(_clean is not null && _clean.TryGetValue(pgno, out var c) ? c : _pager.ReadPage(pgno, _mark)).Clone();
        _dirty[pgno] = copy;
        return copy;
    }

    public uint AllocatePage()
    {
        if (_dirty is null) throw new InvalidOperationException("Transaction is read-only.");
        _headerDirty = true;
        uint pgno;
        if (_header.FreeListHead != 0)
        {
            pgno = _header.FreeListHead;
            var page = WritePage(pgno);
            _header.FreeListHead = BinaryPrimitives.ReadUInt32LittleEndian(page);
            _header.FreePageCount--;
            Array.Clear(page);
            return pgno;
        }
        pgno = _header.PageCount++;
        _dirty[pgno] = new byte[PageSize];
        return pgno;
    }

    public void FreePage(uint pgno)
    {
        if (pgno == 0) throw new InvalidOperationException("Cannot free the header page.");
        var page = WritePage(pgno);
        Array.Clear(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page, _header.FreeListHead);
        _header.FreeListHead = pgno;
        _header.FreePageCount++;
        _headerDirty = true;
    }

    public bool HasChanges => _dirty is { Count: > 0 } || _headerDirty;

    public void Commit()
    {
        ThrowIfFinished();
        if (_dirty is null) throw new InvalidOperationException("Transaction is read-only.");
        try
        {
            if (HasChanges)
            {
                _header.ChangeCounter++;
                _header.Write(WritePage(0));
                var pages = _dirty.OrderBy(static kv => kv.Key).ToList();
                _pager.Commit(pages, _header.PageCount);
            }
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        _clean = null;
        _pager.EndRead();
        try
        {
            if (IsWritable) _pager.MaybeAutoCheckpoint();
        }
        finally
        {
            _onDispose?.Invoke();
        }
    }

    internal void ThrowIfFinished()
    {
        if (_finished) throw new InvalidOperationException("The transaction has already completed.");
    }

    /// <summary>Disposing without commit rolls back (dirty pages are simply discarded).</summary>
    public void Dispose() => Finish();
}
