using System.Diagnostics.CodeAnalysis;
using FolioDb.Engine;
using FolioDb.Query;

namespace FolioDb;

/// <summary>
/// A handle to a named collection. Obtained from <see cref="FolioDatabase"/> (each call is its own atomic
/// auto-commit transaction), a <see cref="Transaction"/> or a <see cref="Snapshot"/> (read-only).
/// Collections are created implicitly on first write.
/// </summary>
public sealed class Collection
{
    private readonly FolioDatabase _db;
    private readonly object? _scope;

    internal Collection(FolioDatabase db, string name, object? scope)
    {
        EngineTx.ValidateCollectionName(name);
        _db = db;
        Name = name;
        _scope = scope;
    }

    public string Name { get; }

    // ------------------------------------------------------------------ plumbing

    private T Read<T>(Func<EngineTx, CollectionMeta?, T> action) => _scope switch
    {
        Transaction t => t.Run(tx => action(tx, tx.GetCollection(Name)), write: false),
        Snapshot s => action(s.Engine, s.Engine.GetCollection(Name)),
        _ => _db.Read(tx => action(tx, tx.GetCollection(Name))),
    };

    /// <summary>atomic: true when the operation validates everything before mutating (safe to continue a transaction after a duplicate-key error).</summary>
    private T Write<T>(Func<EngineTx, T> action, bool atomic) => _scope switch
    {
        Transaction t => t.Run(action, atomic),
        Snapshot => throw new InvalidOperationException("Snapshots are read-only."),
        _ => _db.Write(action),
    };

    private static Filter ParseFilter(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? Filter.All : FilterParser.Parse(Document.Parse(filter));

    // ------------------------------------------------------------------ inserts

    /// <summary>Inserts a document. If it has no <c>_id</c>, an <see cref="ObjectId"/> is generated and added to it.</summary>
    public DocValue Insert(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Write(tx => CollectionEngine.Insert(tx, tx.GetOrCreateCollection(Name), document), atomic: true);
    }

    public DocValue Insert(string json) => Insert(Document.Parse(json));

    /// <summary>Inserts all documents atomically (all or nothing when used outside an explicit transaction).</summary>
    public IReadOnlyList<DocValue> InsertMany(IEnumerable<Document> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var list = documents as IReadOnlyCollection<Document> ?? documents.ToList();
        return Write(tx =>
        {
            var meta = tx.GetOrCreateCollection(Name);
            var ids = new List<DocValue>(list.Count);
            foreach (var d in list) ids.Add(CollectionEngine.Insert(tx, meta, d));
            return ids;
        }, atomic: list.Count <= 1);
    }

    // ------------------------------------------------------------------ queries

    /// <summary>Runs a read-only aggregation pipeline in one snapshot; the result shape is untyped.</summary>
    public List<Document> Aggregate(IEnumerable<Document> pipeline)
    {
        var compiled = new Aggregation(pipeline);
        return Read(compiled.Execute);
    }

    public List<Document> Aggregate(string pipeline) => Aggregate(Aggregation.Parse(DocJson.Parse(pipeline)));

    public Document? FindById(DocValue id) => Read((tx, meta) => CollectionEngine.FindById(tx, meta, id));

    /// <summary>
    /// Non-materializing point read: looks up <paramref name="id"/> and, if found, synchronously invokes
    /// <paramref name="reader"/> with a borrowed <see cref="DocumentView"/> over the stored bytes, returning true and
    /// the callback result. Returns false without invoking the callback when the id or the collection does not exist.
    /// The view is only valid inside the callback; the scope cannot be mutated, committed, rolled back or disposed
    /// until the callback returns (such calls throw <see cref="InvalidOperationException"/>). Exceptions thrown by
    /// the callback propagate unchanged and do not doom an explicit transaction.
    /// Documents spanning multiple overflow pages still require a temporary contiguous buffer.
    /// </summary>
    public bool TryReadById<TResult>(DocValue id, Func<DocumentView, TResult> reader, [MaybeNullWhen(false)] out TResult result)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return TryReadById(id, reader, static (doc, r) => r(doc), out result);
    }

    /// <summary>
    /// Stateful overload of <see cref="TryReadById{TResult}(DocValue, Func{DocumentView, TResult}, out TResult)"/>:
    /// pass values through <paramref name="state"/> (which may itself be a <c>ref struct</c> such as a span) and use a
    /// <c>static</c> lambda to avoid closure allocations.
    /// </summary>
    public bool TryReadById<TState, TResult>(DocValue id, TState state, Func<DocumentView, TState, TResult> reader,
        [MaybeNullWhen(false)] out TResult result)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(reader);
        switch (_scope)
        {
            case Transaction t:
                return t.ReadBorrowed(Name, id, state, reader, out result);
            case Snapshot s:
                return s.ReadBorrowed(Name, id, state, reader, out result);
            default:
                // Committed page images are immutable, so the implicit read transaction only needs to outlive the callback.
                using (var tx = _db.BeginRead())
                {
                    if (!CollectionEngine.TryBorrowById(tx, tx.GetCollection(Name), id, out var view))
                    {
                        result = default;
                        return false;
                    }
                    result = reader(view, state);
                    return true;
                }
        }
    }

    /// <summary>
    /// Visits matching documents in one snapshot without materializing them. Return false from
    /// <paramref name="visitor"/> to stop. Returns the number of callbacks invoked, including the one that stopped.
    /// Views are valid only during their callback, with the same borrowing rules as TryReadById.
    /// Order is chosen by the query planner; sorting and projection are not supported.
    /// A missing collection or no matches returns zero. Callback exceptions propagate without dooming an
    /// explicit transaction; query execution failures doom it like Find. Multi-page values still require a buffer.
    /// </summary>
    public long Visit(Document? filter, Func<DocumentView, bool> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        return Visit(FilterParser.Parse(filter), visitor);
    }

    /// <inheritdoc cref="Visit(Document, Func{DocumentView, bool})"/>
    public long Visit(string? filter, Func<DocumentView, bool> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        return Visit(ParseFilter(filter), visitor);
    }

    private long Visit(Filter filter, Func<DocumentView, bool> visitor) => _scope switch
    {
        Transaction t => t.VisitBorrowed(Name, filter, visitor),
        Snapshot s => s.VisitBorrowed(Name, filter, visitor),
        _ => _db.Read(tx => CollectionEngine.Visit(tx, tx.GetCollection(Name), filter, visitor)),
    };

    /// <inheritdoc cref="Visit(Document, Func{DocumentView, bool})"/>
    public long Visit(PreparedFilter filter, Func<DocumentView, bool> visitor)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(visitor);
        return Visit(filter.Compiled, visitor);
    }

    /// <summary>Runs a previously prepared filter, selecting a plan for the current snapshot.</summary>
    public List<Document> Find(PreparedFilter filter, FindOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Read((tx, meta) => CollectionEngine.Find(tx, meta, filter.Compiled, options));
    }

    public List<Document> Find(Document? filter = null, FindOptions? options = null)
    {
        var f = FilterParser.Parse(filter);
        return Read((tx, meta) => CollectionEngine.Find(tx, meta, f, options));
    }

    public List<Document> Find(string? filter, FindOptions? options = null)
    {
        var f = ParseFilter(filter);
        return Read((tx, meta) => CollectionEngine.Find(tx, meta, f, options));
    }

    public Document? FindOne(Document? filter = null, FindOptions? options = null) =>
        Find(filter, WithLimitOne(options)).FirstOrDefault();

    public Document? FindOne(string? filter, FindOptions? options = null) =>
        Find(filter, WithLimitOne(options)).FirstOrDefault();

    public Document? FindOne(PreparedFilter filter, FindOptions? options = null) =>
        Find(filter, WithLimitOne(options)).FirstOrDefault();

    private static FindOptions WithLimitOne(FindOptions? o) =>
        new() { Sort = o?.Sort, Projection = o?.Projection, Skip = o?.Skip ?? 0, Limit = 1 };

    public long Count(Document? filter = null)
    {
        var f = FilterParser.Parse(filter);
        return Read((tx, meta) => CollectionEngine.Count(tx, meta, f));
    }

    public long Count(string? filter) => Count(string.IsNullOrWhiteSpace(filter) ? null : Document.Parse(filter));

    public long Count(PreparedFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Read((tx, meta) => CollectionEngine.Count(tx, meta, filter.Compiled));
    }

    /// <summary>Describes how the query would be executed (full scan, primary key or index).</summary>
    public string Explain(Document? filter = null)
    {
        var f = FilterParser.Parse(filter);
        return Read((_, meta) => meta is null ? "EMPTY (collection does not exist)" : QueryPlanner.Plan(meta, f).ToString());
    }

    public string Explain(string? filter) => Explain(string.IsNullOrWhiteSpace(filter) ? null : Document.Parse(filter));

    public string Explain(PreparedFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Read((_, meta) => meta is null ? "EMPTY (collection does not exist)" : QueryPlanner.Plan(meta, filter.Compiled).ToString());
    }

    // ------------------------------------------------------------------ updates

    public UpdateResult UpdateOne(Document filter, Document update, bool upsert = false) => Update(filter, update, upsert, many: false);
    public UpdateResult UpdateMany(Document filter, Document update, bool upsert = false) => Update(filter, update, upsert, many: true);
    public UpdateResult UpdateOne(string filter, string update, bool upsert = false) => UpdateOne(Document.Parse(filter), Document.Parse(update), upsert);
    public UpdateResult UpdateMany(string filter, string update, bool upsert = false) => UpdateMany(Document.Parse(filter), Document.Parse(update), upsert);

    /// <summary>Replaces the first matching document entirely (the <c>_id</c> is kept).</summary>
    public UpdateResult ReplaceOne(Document filter, Document replacement, bool upsert = false)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (UpdateApplier.IsOperatorUpdate(replacement) && replacement.Count > 0)
            throw new FolioException("Replacement document cannot contain update operators.");
        return Update(filter, replacement, upsert, many: false);
    }

    private UpdateResult Update(Document filter, Document update, bool upsert, bool many)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(update);
        UpdateApplier.Validate(update);
        var f = FilterParser.Parse(filter);
        return Write(tx =>
        {
            var meta = upsert ? tx.GetOrCreateCollection(Name) : tx.GetCollection(Name);
            if (meta is null) return new UpdateResult(0, 0, null);
            var matches = CollectionEngine.Match(tx, meta, f, many ? int.MaxValue : 1);
            if (matches.Count == 0)
            {
                if (!upsert) return new UpdateResult(0, 0, null);
                var doc = BuildUpsert(filter, update);
                return new UpdateResult(0, 0, CollectionEngine.Insert(tx, meta, doc));
            }
            long modified = 0;
            foreach (var (idKey, bytes) in matches)
            {
                bool changed = UpdateApplier.TryPatch(bytes, update) is { } patched
                    ? CollectionEngine.Replace(tx, meta, idKey, bytes, patched)
                    : CollectionEngine.Replace(tx, meta, idKey, bytes, UpdateApplier.Apply(Document.FromBytes(bytes), update));
                if (changed) modified++;
            }
            return new UpdateResult(matches.Count, modified, null);
        }, atomic: !many);
    }

    private static Document BuildUpsert(Document filter, Document update)
    {
        var seed = new Document();
        SeedFromFilter(filter, seed);
        if (!UpdateApplier.IsOperatorUpdate(update))
        {
            var replacement = update.Clone();
            if (!replacement.ContainsKey("_id") && seed.TryGetValue("_id", out var sid)) replacement.InsertFirst("_id", sid);
            return replacement;
        }
        if (!seed.ContainsKey("_id")) seed.InsertFirst("_id", ObjectId.NewObjectId());
        return UpdateApplier.Apply(seed, update);
    }

    /// <summary>Copies equality conditions (<c>{a: 1}</c>, <c>{a: {$eq: 1}}</c>, <c>$and</c>) into the upserted document.</summary>
    private static void SeedFromFilter(Document filter, Document seed)
    {
        foreach (var (key, value) in filter)
        {
            if (key == "$and" && value.Type == DocType.Array)
            {
                foreach (var part in value.AsArray)
                    if (part.Type == DocType.Document) SeedFromFilter(part.AsDocument, seed);
                continue;
            }
            if (key.StartsWith('$')) continue;
            if (value.Type == DocType.Document)
            {
                var d = value.AsDocument;
                if (d.Count > 0 && d.Keys.First().StartsWith('$'))
                {
                    if (d.TryGetValue("$eq", out var eq)) seed.SetPath(key, eq.DeepClone());
                    continue;
                }
            }
            seed.SetPath(key, value.DeepClone());
        }
    }

    // ------------------------------------------------------------------ deletes

    public long DeleteOne(Document filter) => Delete(filter, many: false);
    public long DeleteMany(Document? filter = null) => Delete(filter, many: true);
    public long DeleteOne(string filter) => DeleteOne(Document.Parse(filter));
    public long DeleteMany(string? filter) => DeleteMany(string.IsNullOrWhiteSpace(filter) ? null : Document.Parse(filter));

    public bool DeleteById(DocValue id) => DeleteOne(new Document { ["_id"] = id }) > 0;

    private long Delete(Document? filter, bool many)
    {
        var f = FilterParser.Parse(filter);
        return Write(tx =>
        {
            var meta = tx.GetCollection(Name);
            if (meta is null) return 0L;
            var matches = CollectionEngine.Match(tx, meta, f, many ? int.MaxValue : 1);
            foreach (var (idKey, bytes) in matches) CollectionEngine.Delete(tx, meta, idKey, bytes);
            return (long)matches.Count;
        }, atomic: true);
    }

    // ------------------------------------------------------------------ indexes

    /// <summary>Creates (or returns the existing) secondary index on a field path such as <c>"address.city"</c>.</summary>
    public string CreateIndex(string field, bool unique = false) =>
        Write(tx => CollectionEngine.CreateIndex(tx, tx.GetOrCreateCollection(Name), field, unique), atomic: false);

    /// <summary>Creates an ordered index key pattern, e.g. <c>{ customer: 1, total: -1 }</c>.</summary>
    public string CreateIndex(Document keys, bool unique = false) =>
        Write(tx => CollectionEngine.CreateIndex(tx, tx.GetOrCreateCollection(Name), keys, unique), atomic: false);

    public bool DropIndex(string nameOrField) => Write(tx =>
    {
        var meta = tx.GetCollection(Name);
        return meta is not null && CollectionEngine.DropIndex(tx, meta, nameOrField);
    }, atomic: true);

    public bool DropIndex(Document keys) => Write(tx =>
    {
        var meta = tx.GetCollection(Name);
        return meta is not null && CollectionEngine.DropIndex(tx, meta, keys);
    }, atomic: true);

    /// <summary>
    /// Rebuilds an existing secondary index in key order inside a write transaction. Returns false if absent;
    /// the primary _id index cannot be rebuilt. Readers retain their previous snapshot until it closes.
    /// </summary>
    public bool RebuildIndex(string nameOrField) => Write(tx =>
    {
        var meta = tx.GetCollection(Name);
        return meta is not null && CollectionEngine.RebuildIndex(tx, meta, nameOrField);
    }, atomic: false);

    /// <summary>Rebuilds the secondary index matching the ordered key pattern; returns false if absent.</summary>
    public bool RebuildIndex(Document keys) => Write(tx =>
    {
        var meta = tx.GetCollection(Name);
        return meta is not null && CollectionEngine.RebuildIndex(tx, meta, keys);
    }, atomic: false);

    public IReadOnlyList<IndexInfo> GetIndexes() => Read((_, meta) =>
    {
        var list = new List<IndexInfo> { new("_id_", "_id", true, false) };
        if (meta is not null) list.AddRange(meta.Indexes.Select(i => new IndexInfo(i.Name, i.Field, i.Unique, i.MultiKey) { Keys = i.KeyPattern() }));
        return (IReadOnlyList<IndexInfo>)list;
    });

    public bool Drop() => Write(tx => tx.DropCollection(Name), atomic: true);
}
