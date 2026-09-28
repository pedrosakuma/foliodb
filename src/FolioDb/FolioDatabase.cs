using FolioDb.Engine;
using FolioDb.Storage;

namespace FolioDb;

/// <summary>
/// An embedded, single-file document database. Thread-safe: any number of concurrent readers (snapshot
/// isolation) and one writer at a time. The file is exclusively locked by the opening process.
/// </summary>
public sealed class FolioDatabase : IDisposable
{
    private readonly Pager _pager;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FolioOptions _options;
    private int _disposed;

    private FolioDatabase(Pager pager, FolioOptions options)
    {
        _pager = pager;
        _options = options;
    }

    public string Path => _pager.Path;

    public static FolioDatabase Open(string path, FolioOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new FolioOptions();
        options.Validate();
        return new FolioDatabase(Pager.Open(System.IO.Path.GetFullPath(path), options), options);
    }

    internal Pager Pager => _pager;

    internal EngineTx BeginRead()
    {
        ThrowIfDisposed();
        return new EngineTx(new StorageTx(_pager, writable: false));
    }

    internal EngineTx BeginWrite()
    {
        ThrowIfDisposed();
        if (!_writeLock.Wait(_options.BusyTimeout))
            throw new FolioException("The database is busy (timed out waiting for the write lock).");
        try
        {
            return new EngineTx(new StorageTx(_pager, writable: true, onDispose: () => _writeLock.Release()));
        }
        catch
        {
            _writeLock.Release();
            throw;
        }
    }

    internal T Read<T>(Func<EngineTx, T> action)
    {
        using var tx = BeginRead();
        return action(tx);
    }

    internal T Write<T>(Func<EngineTx, T> action)
    {
        using var tx = BeginWrite();
        var result = action(tx);
        tx.Commit();
        return result;
    }

    /// <summary>Starts an explicit read-write transaction (serializable: only one writer at a time).</summary>
    public Transaction BeginTransaction() => new(this, BeginWrite());

    /// <summary>Starts a read-only snapshot. All reads through it see the database as of this call.</summary>
    public Snapshot BeginSnapshot() => new(this, BeginRead());

    public Collection GetCollection(string name)
    {
        EngineTx.ValidateCollectionName(name);
        return new Collection(this, name, null);
    }

    public Collection<T> GetCollection<T>(string name) where T : IFolioDocument<T> => new(GetCollection(name));

    public IReadOnlyList<string> GetCollectionNames() => Read(tx => tx.ListCollections());

    public bool DropCollection(string name) => Write(tx => tx.DropCollection(name));

    /// <summary>Copies the WAL into the main file. Returns false if readers are currently active.</summary>
    public bool Checkpoint()
    {
        ThrowIfDisposed();
        if (!_writeLock.Wait(_options.BusyTimeout)) return false;
        try
        {
            return _pager.TryCheckpoint();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public DatabaseStats GetStats() => Read(tx => new DatabaseStats(
        _pager.PageSize,
        tx.Storage.PageCount,
        tx.Storage.FreePageCount,
        _pager.WalFrameCount,
        _pager.DatabaseFileLength,
        _pager.WalFileLength,
        tx.ListCollections()));

    /// <summary>Verifies B+Tree invariants of every collection and index; throws <see cref="CorruptDatabaseException"/> on failure.</summary>
    public void CheckIntegrity() => Read(tx =>
    {
        new BTree(tx.Storage, tx.Storage.CatalogRoot).Verify();
        foreach (var name in tx.ListCollections())
        {
            var meta = tx.GetCollection(name)!;
            long docs = new BTree(tx.Storage, meta.PrimaryRoot).Verify();
            foreach (var index in meta.Indexes)
            {
                new BTree(tx.Storage, index.Root).Verify();
                long withField = 0;
                var cur = new BTree(tx.Storage, meta.PrimaryRoot).CreateCursor();
                long expectedEntries = 0;
                for (bool ok = cur.SeekFirst(); ok; ok = cur.MoveNext())
                {
                    int n = CollectionEngine.ExtractIndexKeys(cur.Value, index.Field, out _).Count;
                    expectedEntries += n;
                    if (n > 0) withField++;
                }
                long actual = new BTree(tx.Storage, index.Root).Verify();
                if (actual != expectedEntries)
                    throw new CorruptDatabaseException($"Index {name}.{index.Name} has {actual} entries, expected {expectedEntries} ({docs} documents).");
            }
        }
        return true;
    });

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>Test hook: abandon the database as if the process crashed (no checkpoint, no flush).</summary>
    internal void SimulateCrash()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pager.SimulateCrash();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_writeLock.Wait(_options.BusyTimeout))
        {
            Volatile.Write(ref _disposed, 0);
            throw new FolioException("database is busy: cannot close while a write transaction is active.");
        }
        try
        {
            _pager.Dispose();
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>Explicit read-write transaction. Disposing without <see cref="Commit"/> rolls back.</summary>
public sealed class Transaction : IDisposable
{
    private readonly FolioDatabase _db;
    private bool _completed;
    private bool _doomed;

    internal Transaction(FolioDatabase db, EngineTx engine)
    {
        _db = db;
        Engine = engine;
    }

    internal EngineTx Engine { get; }

    public Collection GetCollection(string name)
    {
        EngineTx.ValidateCollectionName(name);
        return new Collection(_db, name, this);
    }

    public Collection<T> GetCollection<T>(string name) where T : IFolioDocument<T> => new(GetCollection(name));

    public bool DropCollection(string name) => Run(tx => tx.DropCollection(name), atomic: false);

    /// <summary>
    /// atomic: true when the operation raises <see cref="DuplicateKeyException"/> only before mutating anything, so the
    /// transaction remains usable afterwards. Any other failure dooms the transaction (it can only be rolled back).
    /// </summary>
    internal T Run<T>(Func<EngineTx, T> action, bool atomic = false)
    {
        if (_completed) throw new InvalidOperationException("The transaction has already completed.");
        if (_doomed) throw new FolioException("The transaction failed earlier and must be rolled back.");
        try
        {
            return action(Engine);
        }
        catch (DuplicateKeyException) when (atomic)
        {
            throw;
        }
        catch
        {
            _doomed = true;
            throw;
        }
    }

    public void Commit()
    {
        if (_completed) throw new InvalidOperationException("The transaction has already completed.");
        if (_doomed) throw new FolioException("The transaction failed earlier and must be rolled back.");
        _completed = true;
        Engine.Commit();
    }

    public void Rollback()
    {
        if (_completed) return;
        _completed = true;
        Engine.Dispose();
    }

    public void Dispose() => Rollback();
}

/// <summary>Read-only snapshot: a consistent point-in-time view across multiple reads.</summary>
public sealed class Snapshot : IDisposable
{
    private readonly FolioDatabase _db;

    internal Snapshot(FolioDatabase db, EngineTx engine)
    {
        _db = db;
        Engine = engine;
    }

    internal EngineTx Engine { get; }

    public Collection GetCollection(string name) => new(_db, name, this);
    public Collection<T> GetCollection<T>(string name) where T : IFolioDocument<T> => new(GetCollection(name));
    public IReadOnlyList<string> GetCollectionNames() => Engine.ListCollections();
    public void Dispose() => Engine.Dispose();
}
