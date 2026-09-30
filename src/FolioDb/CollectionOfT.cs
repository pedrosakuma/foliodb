using System.Diagnostics.CodeAnalysis;

namespace FolioDb;

/// <summary>Strongly-typed view over a <see cref="Collection"/> using a source-generated mapper.</summary>
public sealed class Collection<T> where T : IFolioDocument<T>
{
    internal Collection(Collection untyped) => Untyped = untyped;

    public Collection Untyped { get; }
    public string Name => Untyped.Name;

    /// <summary>Inserts the entity and returns the stored <c>_id</c> (generated if the entity had none).</summary>
    public DocValue Insert(T entity) => Untyped.Insert(T.ToDocument(entity));

    public IReadOnlyList<DocValue> InsertMany(IEnumerable<T> entities) => Untyped.InsertMany(entities.Select(T.ToDocument));

    public T? FindById(DocValue id) => Untyped.FindById(id) is { } d ? T.FromDocument(d) : default;

    /// <inheritdoc cref="Collection.TryReadById{TResult}(DocValue, Func{DocumentView, TResult}, out TResult)"/>
    public bool TryReadById<TResult>(DocValue id, Func<DocumentView, TResult> reader, [MaybeNullWhen(false)] out TResult result) =>
        Untyped.TryReadById(id, reader, out result);

    /// <inheritdoc cref="Collection.TryReadById{TState, TResult}(DocValue, TState, Func{DocumentView, TState, TResult}, out TResult)"/>
    public bool TryReadById<TState, TResult>(DocValue id, TState state, Func<DocumentView, TState, TResult> reader,
        [MaybeNullWhen(false)] out TResult result)
        where TState : allows ref struct =>
        Untyped.TryReadById(id, state, reader, out result);

    public List<T> Find(Document? filter = null, FindOptions? options = null) => Map(Untyped.Find(filter, options));
    public List<T> Find(string? filter, FindOptions? options = null) => Map(Untyped.Find(filter, options));

    public T? FindOne(Document? filter = null, FindOptions? options = null) =>
        Untyped.FindOne(filter, options) is { } d ? T.FromDocument(d) : default;

    public T? FindOne(string? filter, FindOptions? options = null) =>
        Untyped.FindOne(filter, options) is { } d ? T.FromDocument(d) : default;

    public long Count(Document? filter = null) => Untyped.Count(filter);
    public long Count(string? filter) => Untyped.Count(filter);

    /// <summary>Replaces the stored document with the same <c>_id</c>; inserts it when <paramref name="upsert"/> is set.</summary>
    public bool Update(T entity, bool upsert = false)
    {
        var doc = T.ToDocument(entity);
        if (!doc.TryGetValue("_id", out var id) || id.IsNull)
            throw new FolioException("Entity has no _id.");
        var r = Untyped.ReplaceOne(new Document { ["_id"] = id }, doc, upsert);
        return r.MatchedCount > 0 || r.UpsertedId is not null;
    }

    public UpdateResult UpdateOne(Document filter, Document update, bool upsert = false) => Untyped.UpdateOne(filter, update, upsert);
    public UpdateResult UpdateMany(Document filter, Document update, bool upsert = false) => Untyped.UpdateMany(filter, update, upsert);
    public UpdateResult UpdateOne(string filter, string update, bool upsert = false) => Untyped.UpdateOne(filter, update, upsert);
    public UpdateResult UpdateMany(string filter, string update, bool upsert = false) => Untyped.UpdateMany(filter, update, upsert);

    public bool DeleteById(DocValue id) => Untyped.DeleteById(id);
    public long DeleteOne(Document filter) => Untyped.DeleteOne(filter);
    public long DeleteOne(string filter) => Untyped.DeleteOne(filter);
    public long DeleteMany(Document? filter = null) => Untyped.DeleteMany(filter);
    public long DeleteMany(string? filter) => Untyped.DeleteMany(filter);

    public string CreateIndex(string field, bool unique = false) => Untyped.CreateIndex(field, unique);
    public List<Document> Aggregate(IEnumerable<Document> pipeline) => Untyped.Aggregate(pipeline);
    public List<Document> Aggregate(string pipeline) => Untyped.Aggregate(pipeline);
    public string CreateIndex(Document keys, bool unique = false) => Untyped.CreateIndex(keys, unique);
    public bool DropIndex(string nameOrField) => Untyped.DropIndex(nameOrField);
    public bool DropIndex(Document keys) => Untyped.DropIndex(keys);
    public IReadOnlyList<IndexInfo> GetIndexes() => Untyped.GetIndexes();

    private static List<T> Map(List<Document> docs)
    {
        var list = new List<T>(docs.Count);
        foreach (var d in docs) list.Add(T.FromDocument(d));
        return list;
    }
}
