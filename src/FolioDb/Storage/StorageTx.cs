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
    private readonly Action? _onDispose;
    private DbHeader _header;
    private bool _headerDirty;
    private bool _finished;

    public bool IsWritable { get; }
    public int PageSize => _pager.PageSize;

    public StorageTx(Pager pager, bool writable, Action? onDispose = null)
    {
        _pager = pager;
        IsWritable = writable;
        _onDispose = onDispose;
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
        if (pgno >= _header.PageCount) throw new CorruptDatabaseException($"Page {pgno} is out of range ({_header.PageCount} pages).");
        return _pager.ReadPage(pgno, _mark);
    }

    /// <summary>Private, mutable copy of a page owned by this transaction.</summary>
    public byte[] WritePage(uint pgno)
    {
        ThrowIfFinished();
        if (_dirty is null) throw new InvalidOperationException("Transaction is read-only.");
        if (_dirty.TryGetValue(pgno, out var d)) return d;
        var copy = (byte[])_pager.ReadPage(pgno, _mark).Clone();
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

    private void ThrowIfFinished()
    {
        if (_finished) throw new InvalidOperationException("The transaction has already completed.");
    }

    /// <summary>Disposing without commit rolls back (dirty pages are simply discarded).</summary>
    public void Dispose() => Finish();
}
