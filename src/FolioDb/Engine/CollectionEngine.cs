using System.Text;
using FolioDb.Query;
using FolioDb.Storage;

namespace FolioDb.Engine;

/// <summary>Document-level operations on a collection inside an <see cref="EngineTx"/>.</summary>
internal static class CollectionEngine
{
    public const int MaxDocumentSize = 16 * 1024 * 1024;

    // ------------------------------------------------------------------ index key extraction

    /// <summary>Distinct encoded index values for <paramref name="field"/> (arrays expand to their elements). Missing fields produce no keys.</summary>
    internal static List<byte[]> ExtractIndexKeys(ReadOnlySpan<byte> doc, string field, out bool multiKey)
    {
        var segments = field.Split('.').Select(Encoding.UTF8.GetBytes).ToArray();
        var keys = new List<byte[]>(1);
        multiKey = false;
        using var buf = new ByteBuffer(64);
        Collect(new RawValue(DocType.Document, new RawDocument(doc).Data), segments, 0, keys, buf, ref multiKey);
        if (keys.Count > 1)
        {
            keys.Sort(ByteArrayComparer.Instance);
            int w = 1;
            for (int r = 1; r < keys.Count; r++)
                if (!keys[r].AsSpan().SequenceEqual(keys[w - 1])) keys[w++] = keys[r];
            keys.RemoveRange(w, keys.Count - w);
        }
        return keys;
    }

    private static void Collect(RawValue v, byte[][] segments, int seg, List<byte[]> keys, ByteBuffer buf, ref bool multiKey)
    {
        if (seg == segments.Length)
        {
            if (v.Type == DocType.Array)
            {
                multiKey = true;
                bool any = false;
                foreach (var item in v.AsArray)
                {
                    any = true;
                    keys.Add(Encode(buf, item));
                }
                if (!any) keys.Add(Encode(buf, v)); // empty array is indexed as []
            }
            else keys.Add(Encode(buf, v));
            return;
        }
        if (v.Type == DocType.Document)
        {
            if (v.AsDocument.TryGetField(segments[seg], out var next)) Collect(next, segments, seg + 1, keys, buf, ref multiKey);
        }
        else if (v.Type == DocType.Array)
        {
            multiKey = true;
            foreach (var item in v.AsArray)
                if (item.Type == DocType.Document) Collect(item, segments, seg, keys, buf, ref multiKey);
        }
    }

    private static byte[] Encode(ByteBuffer buf, RawValue v)
    {
        buf.Clear();
        KeyEncoder.Encode(buf, v);
        return buf.ToArray();
    }

    private static byte[] Concat(byte[] a, ReadOnlySpan<byte> b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r.AsSpan(a.Length));
        return r;
    }

    private static bool ConflictsInUniqueIndex(BTree index, byte[] valueKey, ReadOnlySpan<byte> idKey)
    {
        var cur = index.CreateCursor();
        for (bool ok = cur.Seek(valueKey); ok && cur.Key.StartsWith(valueKey); ok = cur.MoveNext())
            if (!cur.Key[valueKey.Length..].SequenceEqual(idKey)) return true;
        return false;
    }

    private static void CheckKeySize(EngineTx tx, IndexMeta? index, int size)
    {
        int max = BTree.MaxKeySize(tx.Storage.PageSize);
        if (size > max)
            throw new FolioException(index is null
                ? $"_id value is too large ({size} bytes encoded, max {max})."
                : $"Value for indexed field '{index.Field}' is too large ({size} bytes encoded, max {max}).");
    }

    // ------------------------------------------------------------------ writes

    public static DocValue Insert(EngineTx tx, CollectionMeta meta, Document doc)
    {
        if (!doc.TryGetValue("_id", out var id))
        {
            id = ObjectId.NewObjectId();
            doc.InsertFirst("_id", id);
        }
        if (id.Type == DocType.Array) throw new FolioException("_id cannot be an array.");

        var idKey = KeyEncoder.Encode(id);
        CheckKeySize(tx, null, idKey.Length);
        var bytes = DocumentSerializer.Serialize(doc);
        if (bytes.Length > MaxDocumentSize) throw new FolioException($"Document exceeds the maximum size of {MaxDocumentSize} bytes.");

        var primary = new BTree(tx.Storage, meta.PrimaryRoot);
        if (primary.ContainsKey(idKey))
            throw new DuplicateKeyException($"Duplicate key in collection '{meta.Name}': _id {DocJson.WriteValue(id)}.");

        // Validate every index before mutating anything, so a failed insert leaves the transaction untouched.
        var perIndex = new List<(IndexMeta Index, List<byte[]> Keys, bool Multi)>(meta.Indexes.Count);
        foreach (var index in meta.Indexes)
        {
            var keys = ExtractIndexKeys(bytes, index.Field, out bool multi);
            foreach (var k in keys)
            {
                CheckKeySize(tx, index, k.Length + idKey.Length);
                if (index.Unique && ConflictsInUniqueIndex(new BTree(tx.Storage, index.Root), k, idKey))
                    throw new DuplicateKeyException($"Duplicate key in unique index '{index.Name}' of '{meta.Name}': {DocJson.WriteValue(KeyEncoder.Decode(k, out _))}.");
            }
            perIndex.Add((index, keys, multi));
        }

        primary.Insert(idKey, bytes, overwrite: false);
        bool metaChanged = false;
        foreach (var (index, keys, multi) in perIndex)
        {
            var tree = new BTree(tx.Storage, index.Root);
            foreach (var k in keys) tree.Insert(Concat(k, idKey), [], overwrite: true);
            if (multi && !index.MultiKey)
            {
                index.MultiKey = true;
                metaChanged = true;
            }
        }
        if (metaChanged) tx.SaveCollection(meta);
        return id;
    }

    public static void Delete(EngineTx tx, CollectionMeta meta, ReadOnlySpan<byte> idKey, ReadOnlySpan<byte> docBytes)
    {
        foreach (var index in meta.Indexes)
        {
            var tree = new BTree(tx.Storage, index.Root);
            foreach (var k in ExtractIndexKeys(docBytes, index.Field, out _)) tree.Delete(Concat(k, idKey));
        }
        new BTree(tx.Storage, meta.PrimaryRoot).Delete(idKey);
    }

    /// <summary>Replaces a stored document. Returns false if the new version is byte-identical (not modified).</summary>
    public static bool Replace(EngineTx tx, CollectionMeta meta, byte[] idKey, byte[] oldBytes, Document newDoc) =>
        Replace(tx, meta, idKey, oldBytes, DocumentSerializer.Serialize(newDoc));

    /// <summary>Replaces a stored document with already serialized bytes. Returns false if they are identical.</summary>
    public static bool Replace(EngineTx tx, CollectionMeta meta, byte[] idKey, byte[] oldBytes, byte[] newBytes)
    {
        if (newBytes.AsSpan().SequenceEqual(oldBytes)) return false;
        if (newBytes.Length > MaxDocumentSize) throw new FolioException($"Document exceeds the maximum size of {MaxDocumentSize} bytes.");

        var changes = new List<(IndexMeta Index, List<byte[]> Removed, List<byte[]> Added, bool Multi)>();
        foreach (var index in meta.Indexes)
        {
            var oldKeys = ExtractIndexKeys(oldBytes, index.Field, out _);
            var newKeys = ExtractIndexKeys(newBytes, index.Field, out bool multi);
            var removed = oldKeys.Where(k => !newKeys.Contains(k, ByteArrayComparer.Instance)).ToList();
            var added = newKeys.Where(k => !oldKeys.Contains(k, ByteArrayComparer.Instance)).ToList();
            foreach (var k in added)
            {
                CheckKeySize(tx, index, k.Length + idKey.Length);
                if (index.Unique && ConflictsInUniqueIndex(new BTree(tx.Storage, index.Root), k, idKey))
                    throw new DuplicateKeyException($"Duplicate key in unique index '{index.Name}' of '{meta.Name}': {DocJson.WriteValue(KeyEncoder.Decode(k, out _))}.");
            }
            changes.Add((index, removed, added, multi));
        }

        bool metaChanged = false;
        foreach (var (index, removed, added, multi) in changes)
        {
            var tree = new BTree(tx.Storage, index.Root);
            foreach (var k in removed) tree.Delete(Concat(k, idKey));
            foreach (var k in added) tree.Insert(Concat(k, idKey), [], overwrite: true);
            if (multi && !index.MultiKey)
            {
                index.MultiKey = true;
                metaChanged = true;
            }
        }
        new BTree(tx.Storage, meta.PrimaryRoot).Update(idKey, newBytes);
        if (metaChanged) tx.SaveCollection(meta);
        return true;
    }

    // ------------------------------------------------------------------ reads

    public static Document? FindById(EngineTx tx, CollectionMeta? meta, DocValue id)
    {
        if (meta is null) return null;
        return new BTree(tx.Storage, meta.PrimaryRoot).TryGet(KeyEncoder.Encode(id), out var bytes)
            ? DocumentSerializer.Deserialize(bytes)
            : null;
    }

    /// <summary>Collects (idKey, document bytes) of matching documents (copies, safe to use while mutating).</summary>
    public static List<(byte[] IdKey, byte[] Bytes)> Match(EngineTx tx, CollectionMeta meta, Filter filter, int limit = int.MaxValue)
    {
        var result = new List<(byte[], byte[])>();
        if (limit <= 0) return result;
        var plan = QueryPlanner.Plan(meta, filter);
        QueryPlanner.Execute(tx.Storage, meta, plan, (id, doc) =>
        {
            if (filter.Matches(new RawDocument(doc))) result.Add((id.ToArray(), doc.ToArray()));
            return result.Count < limit;
        });
        return result;
    }

    public static long Count(EngineTx tx, CollectionMeta? meta, Filter filter)
    {
        if (meta is null) return 0;
        long n = 0;
        var plan = QueryPlanner.Plan(meta, filter);
        QueryPlanner.Execute(tx.Storage, meta, plan, (_, doc) =>
        {
            if (filter.Matches(new RawDocument(doc))) n++;
            return true;
        });
        return n;
    }

    public static List<Document> Find(EngineTx tx, CollectionMeta? meta, Filter filter, FindOptions? options)
    {
        var result = new List<Document>();
        if (meta is null) return result;
        int skip = options?.Skip ?? 0;
        int limit = options?.Limit is int l and > 0 ? l : int.MaxValue;
        var sort = SortSpec.Parse(options?.Sort);
        var plan = QueryPlanner.Plan(meta, filter);

        if (sort is null)
        {
            int skipped = 0;
            QueryPlanner.Execute(tx.Storage, meta, plan, (_, doc) =>
            {
                var raw = new RawDocument(doc);
                if (!filter.Matches(raw)) return true;
                if (skipped < skip)
                {
                    skipped++;
                    return true;
                }
                result.Add(raw.ToDocument());
                return result.Count < limit;
            });
        }
        else
        {
            var rows = new List<(byte[][] Keys, int Seq, Document Doc)>();
            QueryPlanner.Execute(tx.Storage, meta, plan, (_, doc) =>
            {
                var raw = new RawDocument(doc);
                if (filter.Matches(raw)) rows.Add((sort.KeysFor(raw), rows.Count, raw.ToDocument()));
                return true;
            });
            // Stable: ties keep scan order.
            rows.Sort((a, b) => sort.Compare(a.Keys, b.Keys) is var c and not 0 ? c : a.Seq.CompareTo(b.Seq));
            for (int i = skip; i < rows.Count && result.Count < limit; i++) result.Add(rows[i].Doc);
        }

        var projection = Projection.Parse(options?.Projection);
        if (projection is not null)
            for (int i = 0; i < result.Count; i++) result[i] = projection.Apply(result[i]);
        return result;
    }

    // ------------------------------------------------------------------ indexes

    public static string CreateIndex(EngineTx tx, CollectionMeta meta, string field, bool unique)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (field == "_id") return "_id_";
        if (field.StartsWith('$') || field.Contains("..") || field.EndsWith('.')) throw new FolioException($"Invalid index field '{field}'.");

        var existing = meta.FindIndexByField(field);
        if (existing is not null)
        {
            if (existing.Unique != unique) throw new FolioException($"An index on '{field}' already exists with different options.");
            return existing.Name;
        }

        var index = new IndexMeta { Name = field + "_1", Field = field, Unique = unique, Root = BTree.Create(tx.Storage) };
        var tree = new BTree(tx.Storage, index.Root);
        var primary = new BTree(tx.Storage, meta.PrimaryRoot);
        var cur = primary.CreateCursor();
        for (bool ok = cur.SeekFirst(); ok; ok = cur.MoveNext())
        {
            var idKey = cur.Key.ToArray();
            foreach (var k in ExtractIndexKeys(cur.Value, field, out bool multi))
            {
                CheckKeySize(tx, index, k.Length + idKey.Length);
                if (unique && ConflictsInUniqueIndex(tree, k, idKey))
                    throw new DuplicateKeyException($"Cannot create unique index on '{field}': duplicate value {DocJson.WriteValue(KeyEncoder.Decode(k, out _))}.");
                tree.Insert(Concat(k, idKey), [], overwrite: true);
                if (multi) index.MultiKey = true;
            }
        }
        meta.Indexes.Add(index);
        tx.SaveCollection(meta);
        return index.Name;
    }

    public static bool DropIndex(EngineTx tx, CollectionMeta meta, string nameOrField)
    {
        var index = meta.Indexes.FirstOrDefault(i => i.Name == nameOrField || i.Field == nameOrField);
        if (index is null) return false;
        new BTree(tx.Storage, index.Root).Drop();
        meta.Indexes.Remove(index);
        tx.SaveCollection(meta);
        return true;
    }
}

/// <summary>Sort specification ({field: 1 | -1, ...}) evaluated on encoded values.</summary>
internal sealed class SortSpec
{
    private readonly (byte[][] Segments, int Direction)[] _fields;

    private SortSpec((byte[][], int)[] fields) => _fields = fields;

    public static SortSpec? Parse(Document? sort)
    {
        if (sort is null || sort.Count == 0) return null;
        var fields = new List<(byte[][], int)>();
        foreach (var (k, v) in sort)
        {
            int dir = v.IsNumber ? Math.Sign(v.AsDouble) : 0;
            if (dir == 0) throw new FolioException($"Sort direction for '{k}' must be 1 or -1.");
            fields.Add((k.Split('.').Select(Encoding.UTF8.GetBytes).ToArray(), dir));
        }
        return new SortSpec(fields.ToArray());
    }

    private static readonly byte[] s_nullKey = [KeyEncoder.TagNull];

    public byte[][] KeysFor(RawDocument doc)
    {
        var keys = new byte[_fields.Length][];
        using var buf = new ByteBuffer(64);
        for (int i = 0; i < _fields.Length; i++)
        {
            keys[i] = s_nullKey;
            var segs = _fields[i].Segments;
            var current = new RawValue(DocType.Document, doc.Data);
            bool found = true;
            foreach (var s in segs)
            {
                if (current.Type != DocType.Document || !current.AsDocument.TryGetField(s, out current))
                {
                    found = false;
                    break;
                }
            }
            if (found)
            {
                buf.Clear();
                KeyEncoder.Encode(buf, current);
                keys[i] = buf.ToArray();
            }
        }
        return keys;
    }

    public int Compare(byte[][] a, byte[][] b)
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            int c = a[i].AsSpan().SequenceCompareTo(b[i]);
            if (c != 0) return c * _fields[i].Direction;
        }
        return 0;
    }
}

/// <summary>Field projection: inclusion ({a: 1, "b.c": 1}) or exclusion ({a: 0}); _id is included unless excluded.</summary>
internal sealed class Projection
{
    private readonly List<string> _paths;
    private readonly bool _include;
    private readonly bool _includeId;

    private Projection(List<string> paths, bool include, bool includeId)
    {
        _paths = paths;
        _include = include;
        _includeId = includeId;
    }

    public static Projection? Parse(Document? spec)
    {
        if (spec is null || spec.Count == 0) return null;
        bool? include = null;
        bool includeId = true;
        var paths = new List<string>();
        foreach (var (k, v) in spec)
        {
            bool on = v.Type == DocType.Boolean ? v.AsBoolean : v.IsNumber && v.AsDouble != 0;
            if (k == "_id")
            {
                includeId = on;
                continue;
            }
            if (include is null) include = on;
            else if (include != on) throw new FolioException("Projection cannot mix inclusion and exclusion (except for _id).");
            paths.Add(k);
        }
        return new Projection(paths, include ?? false, includeId);
    }

    public Document Apply(Document doc)
    {
        if (_include)
        {
            var result = new Document();
            if (_includeId && doc.TryGetValue("_id", out var id)) result.Set("_id", id);
            foreach (var p in _paths)
                if (doc.TryGetPath(p, out var v)) result.SetPath(p, v);
            return result;
        }
        var clone = doc.Clone();
        foreach (var p in _paths) clone.RemovePath(p);
        if (!_includeId) clone.Remove("_id");
        return clone;
    }
}
