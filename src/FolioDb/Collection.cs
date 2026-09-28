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
        Transaction t => t.Run(tx => action(tx, tx.GetCollection(Name))),
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

    public Document? FindById(DocValue id) => Read((tx, meta) => CollectionEngine.FindById(tx, meta, id));

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

    private static FindOptions WithLimitOne(FindOptions? o) =>
        new() { Sort = o?.Sort, Projection = o?.Projection, Skip = o?.Skip ?? 0, Limit = 1 };

    public long Count(Document? filter = null)
    {
        var f = FilterParser.Parse(filter);
        return Read((tx, meta) => CollectionEngine.Count(tx, meta, f));
    }

    public long Count(string? filter) => Count(string.IsNullOrWhiteSpace(filter) ? null : Document.Parse(filter));

    /// <summary>Describes how the query would be executed (full scan, primary key or index).</summary>
    public string Explain(Document? filter = null)
    {
        var f = FilterParser.Parse(filter);
        return Read((_, meta) => meta is null ? "EMPTY (collection does not exist)" : QueryPlanner.Plan(meta, f).ToString());
    }

    public string Explain(string? filter) => Explain(string.IsNullOrWhiteSpace(filter) ? null : Document.Parse(filter));

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
                var updated = UpdateApplier.Apply(Document.FromBytes(bytes), update);
                if (CollectionEngine.Replace(tx, meta, idKey, bytes, updated)) modified++;
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

    public bool DropIndex(string nameOrField) => Write(tx =>
    {
        var meta = tx.GetCollection(Name);
        return meta is not null && CollectionEngine.DropIndex(tx, meta, nameOrField);
    }, atomic: true);

    public IReadOnlyList<IndexInfo> GetIndexes() => Read((_, meta) =>
    {
        var list = new List<IndexInfo> { new("_id_", "_id", true, false) };
        if (meta is not null) list.AddRange(meta.Indexes.Select(i => new IndexInfo(i.Name, i.Field, i.Unique, i.MultiKey)));
        return (IReadOnlyList<IndexInfo>)list;
    });

    public bool Drop() => Write(tx => tx.DropCollection(Name), atomic: true);
}
