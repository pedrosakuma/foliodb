using System.Diagnostics.CodeAnalysis;
using FolioDb.Engine;
using FolioDb.Query;
using FolioDb.Storage;

namespace FolioDb;

/// <summary>
/// An embedded, single-file document database. Thread-safe: any number of concurrent readers (snapshot
/// isolation) and one writer at a time. The file is exclusively locked by the opening process.
/// </summary>
public sealed class FolioDatabase : IDisposable
{
    private readonly Pager _pager;
    private readonly WriterLock _writeLock;
    private readonly FolioOptions _options;
    private readonly CatalogCache _catalog = new();
    internal CatalogCache Catalog => _catalog;
    internal Action? TestBeforeReadSnapshot;
    private int _disposed;

    private FolioDatabase(Pager pager, FolioOptions options)
    {
        _pager = pager;
        _options = options;
        _writeLock = new WriterLock(options.WriterAdmission);
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
    internal FolioOptions Options => _options;
    internal int WaitingWriters => _writeLock.WaitingCount;
    internal Action<VacuumStage>? TestVacuumStage;

    internal EngineTx BeginRead()
    {
        ThrowIfDisposed();
        long generation = _catalog.Generation;
        TestBeforeReadSnapshot?.Invoke();
        var storage = new StorageTx(_pager, writable: false, deleteRebalance: _options.DeleteRebalance);
        return new EngineTx(storage, _catalog, _catalog.Validate(generation));
    }

    internal EngineTx BeginWrite()
    {
        ThrowIfDisposed();
        if (!_writeLock.Wait(_options.BusyTimeout))
            throw new FolioException("The database is busy (timed out waiting for the write lock).");
        try
        {
            ThrowIfDisposed();
            return new EngineTx(new StorageTx(
                _pager,
                writable: true,
                onDispose: () => _writeLock.Release(),
                deleteRebalance: _options.DeleteRebalance), _catalog);
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

    internal T Write<T>(Func<EngineTx, T> action) => Write(action, static (tx, a) => a(tx));

    /// <summary>State-passing <see cref="Write{T}(Func{EngineTx, T})"/>, so hot paths can use static (non-allocating) lambdas.</summary>
    internal T Write<TState, T>(TState state, Func<EngineTx, TState, T> action)
    {
        using var tx = BeginWrite();
        var result = action(tx, state);
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

    /// <summary>Copies the WAL into the main file and restarts it. Returns false if a reader still needs part of the WAL or writer admission times out.</summary>
    public bool Checkpoint()
    {
        ThrowIfDisposed();
        if (!_writeLock.Wait(_options.BusyTimeout)) return false;
        try
        {
            ThrowIfDisposed();
            return _pager.TryCheckpoint();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Rebuilds this database into a compact, new file at <paramref name="destination"/> without changing this database.
    /// The destination must not exist. Reads use one fixed snapshot while concurrent writers may continue on the source.
    /// </summary>
    /// <remarks>
    /// The result is first built and checkpointed in a private staging file in the destination directory, then published
    /// with a non-overwriting move. If the operation fails or is canceled, the source is unchanged and no destination is
    /// published. A staging file left after an I/O failure is incomplete and is never named as the requested destination.
    /// Close this database yourself before replacing its files with the result; this method never swaps the source or WAL.
    /// </remarks>
    public void VacuumInto(string destination, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Vacuum.Into(this, destination, cancellationToken);
    }

    public DatabaseStats GetStats() => Read(tx => new DatabaseStats(
        _pager.PageSize,
        tx.Storage.PageCount,
        tx.Storage.FreePageCount,
        _pager.WalFrameCount,
        _pager.DatabaseFileLength,
        _pager.WalFileLength,
        tx.ListCollections()));

    internal DatabaseStorageDiagnostics GetStorageDiagnostics() => Read(tx =>
    {
        var trees = new List<TreeStorageDiagnostics>
        {
            new("$catalog", StorageTreeKind.Catalog, null, null, false,
                new BTree(tx.Storage, tx.Storage.CatalogRoot).Diagnose()),
        };
        foreach (var name in tx.ListCollections())
        {
            var meta = tx.GetCollection(name)!;
            trees.Add(new TreeStorageDiagnostics(
                $"{name}/$primary",
                StorageTreeKind.Primary,
                name,
                null,
                false,
                new BTree(tx.Storage, meta.PrimaryRoot).Diagnose()));
            foreach (var index in meta.Indexes)
                trees.Add(new TreeStorageDiagnostics(
                    $"{name}/{index.Name}",
                    StorageTreeKind.Secondary,
                    name,
                    index.Name,
                    index.MultiKey,
                    new BTree(tx.Storage, index.Root).Diagnose()));
        }
        return new DatabaseStorageDiagnostics(
            new DatabaseStats(
                _pager.PageSize,
                tx.Storage.PageCount,
                tx.Storage.FreePageCount,
                _pager.WalFrameCount,
                _pager.DatabaseFileLength,
                _pager.WalFileLength,
                tx.ListCollections()),
            trees);
    });

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
                var entries = new BTree(tx.Storage, index.Root).CreateCursor();
                for (bool ok = cur.SeekFirst(); ok; ok = cur.MoveNext())
                {
                    var keys = CollectionEngine.ExtractIndexKeys(cur.Value, index, out _);
                    var idHint = IndexHint.ForId(cur.Value);
                    foreach (var (k, hint) in keys)
                    {
                        if (!entries.SeekExact([.. k, .. cur.Key]))
                            throw new CorruptDatabaseException($"Index {name}.{index.Name} is missing an entry.");
                        // Legacy entries carry no hint; otherwise the hint must match the stored types.
                        if (!entries.Value.IsEmpty && !entries.Value.SequenceEqual(IndexHint.Entry(idHint, hint)))
                            throw new CorruptDatabaseException($"Index {name}.{index.Name} has a stale type hint.");
                    }
                    expectedEntries += keys.Count;
                    if (keys.Count > 0) withField++;
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
        bool acquired;
        try { acquired = _writeLock.Wait(_options.BusyTimeout); }
        catch
        {
            Volatile.Write(ref _disposed, 0);
            throw;
        }
        if (!acquired)
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

/// <summary>
/// Explicit read-write transaction. Disposing without <see cref="Commit"/> rolls back. Not thread-safe: use it from
/// one thread at a time.
/// </summary>
public sealed class Transaction : IDisposable
{
    private readonly FolioDatabase _db;
    private bool _completed;
    private bool _doomed;
    // Active borrowed reads. Their views may alias this transaction's private (mutable) pages.
    private int _borrows;

    internal Transaction(FolioDatabase db, EngineTx engine)
    {
        _db = db;
        _engine = engine;
    }

    private readonly EngineTx _engine;

    // Every operation starts by fetching the engine, so this is where scan detection restarts (see StorageTx).
    internal EngineTx Engine { get { _engine.Storage.BeginOperation(); return _engine; } }

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
    /// write: false for read-only operations, which remain allowed while a borrowed view is active.
    /// </summary>
    internal T Run<T>(Func<EngineTx, T> action, bool atomic = false, bool write = true) =>
        Run(action, static (tx, a) => a(tx), atomic, write);

    internal T Run<TState, T>(TState state, Func<EngineTx, TState, T> action, bool atomic = false, bool write = true)
    {
        ThrowIfUnusable();
        if (write) ThrowIfBorrowed("modified");
        try
        {
            return action(Engine, state);
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

    /// <summary>Lookup failures doom the transaction like any read; callback exceptions do not (they cannot have mutated it).</summary>
    internal bool ReadBorrowed<TState, TResult>(string collection, DocValue id, TState state,
        Func<DocumentView, TState, TResult> reader, [MaybeNullWhen(false)] out TResult result)
        where TState : allows ref struct
    {
        ThrowIfUnusable();
        bool found;
        DocumentView view;
        byte[]? rented;
        try
        {
            found = CollectionEngine.TryBorrowById(Engine, Engine.GetCollection(collection), id, out view, out rented);
        }
        catch
        {
            _doomed = true;
            throw;
        }
        if (!found)
        {
            result = default;
            return false;
        }
        _borrows++;
        try
        {
            result = reader(view, state);
        }
        finally
        {
            _borrows--;
            if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
        return true;
    }

    internal long VisitBorrowed(string collection, Filter filter, Func<DocumentView, bool> visitor)
    {
        ThrowIfUnusable();
        bool callbackFailed = false;
        _borrows++;
        try
        {
            return CollectionEngine.Visit(Engine, Engine.GetCollection(collection), filter, view =>
            {
                try { return visitor(view); }
                catch
                {
                    callbackFailed = true;
                    throw;
                }
            });
        }
        catch when (!callbackFailed)
        {
            _doomed = true;
            throw;
        }
        finally
        {
            _borrows--;
        }
    }

    private void ThrowIfUnusable()
    {
        if (_completed) throw new InvalidOperationException("The transaction has already completed.");
        if (_doomed) throw new FolioException("The transaction failed earlier and must be rolled back.");
    }

    private void ThrowIfBorrowed(string action)
    {
        if (_borrows > 0)
            throw new InvalidOperationException($"The transaction cannot be {action} while a borrowed read callback is running.");
    }

    public void Commit()
    {
        ThrowIfUnusable();
        ThrowIfBorrowed("committed");
        _completed = true;
        Engine.Commit();
    }

    public void Rollback()
    {
        if (_completed) return;
        ThrowIfBorrowed("rolled back or disposed");
        _completed = true;
        Engine.Dispose();
    }

    /// <summary>Rolls back if not committed. Throws <see cref="InvalidOperationException"/> while a borrowed read callback is running.</summary>
    public void Dispose() => Rollback();
}

/// <summary>Read-only snapshot: a consistent point-in-time view across multiple reads. Not thread-safe.</summary>
public sealed class Snapshot : IDisposable
{
    private readonly FolioDatabase _db;
    private int _borrows;

    internal Snapshot(FolioDatabase db, EngineTx engine)
    {
        _db = db;
        _engine = engine;
    }

    private readonly EngineTx _engine;

    // Every operation starts by fetching the engine, so this is where scan detection restarts (see StorageTx).
    internal EngineTx Engine { get { _engine.Storage.BeginOperation(); return _engine; } }

    public Collection GetCollection(string name) => new(_db, name, this);
    public Collection<T> GetCollection<T>(string name) where T : IFolioDocument<T> => new(GetCollection(name));
    public IReadOnlyList<string> GetCollectionNames() => Engine.ListCollections();

    internal bool ReadBorrowed<TState, TResult>(string collection, DocValue id, TState state,
        Func<DocumentView, TState, TResult> reader, [MaybeNullWhen(false)] out TResult result)
        where TState : allows ref struct
    {
        if (!CollectionEngine.TryBorrowById(Engine, Engine.GetCollection(collection), id, out var view, out var rented))
        {
            result = default;
            return false;
        }
        _borrows++;
        try
        {
            result = reader(view, state);
        }
        finally
        {
            _borrows--;
            if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
        return true;
    }

    internal long VisitBorrowed(string collection, Filter filter, Func<DocumentView, bool> visitor)
    {
        _borrows++;
        try
        {
            return CollectionEngine.Visit(Engine, Engine.GetCollection(collection), filter, visitor);
        }
        finally
        {
            _borrows--;
        }
    }

    /// <summary>Releases the snapshot. Throws <see cref="InvalidOperationException"/> while a borrowed read callback is running.</summary>
    public void Dispose()
    {
        if (_borrows > 0)
            throw new InvalidOperationException("The snapshot cannot be disposed while a borrowed read callback is running.");
        Engine.Dispose();
    }
}
