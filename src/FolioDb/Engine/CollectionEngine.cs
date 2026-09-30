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
    /// <remarks>Each key carries the <see cref="IndexHint"/> of its value; for duplicates the first occurrence wins.</remarks>
    internal static List<IndexKey> ExtractIndexKeys(ReadOnlySpan<byte> doc, string field, out bool multiKey)
    {
        var segments = field.Split('.').Select(Encoding.UTF8.GetBytes).ToArray();
        var keys = new List<IndexKey>(1);
        multiKey = false;
        using var buf = new ByteBuffer(64);
        Collect(new RawValue(DocType.Document, new RawDocument(doc).Data), segments, 0, keys, buf, ref multiKey);
        if (keys.Count > 1)
        {
            // Stable sort keeps the first occurrence of equal keys first.
            var sorted = keys.Select((k, i) => (k, i)).OrderBy(x => x.k.Key, ByteArrayComparer.Instance).ThenBy(x => x.i).Select(x => x.k).ToList();
            keys.Clear();
            foreach (var k in sorted)
                if (keys.Count == 0 || !k.Key.AsSpan().SequenceEqual(keys[^1].Key)) keys.Add(k);
        }
        return keys;
    }

    /// <summary>Upper bound of keys one document may produce in a compound index (cartesian product of array fields).</summary>
    public const int MaxCompoundKeysPerDocument = 1000;

    private static readonly IndexKey s_missing = new([KeyEncoder.TagNull], [IndexHint.Unknown]);

    /// <summary>Distinct entries (value part) of <paramref name="index"/> for a document. See <see cref="IndexMeta.IsSimple"/>.</summary>
    /// <remarks>Compound hints are the component hints followed by the <see cref="IndexHint.InOrder"/> flag.</remarks>
    internal static List<IndexKey> ExtractIndexKeys(ReadOnlySpan<byte> doc, IndexMeta index, out bool multiKey)
    {
        if (index.IsSimple) return ExtractIndexKeys(doc, index.Fields[0].Path, out multiKey);
        multiKey = false;
        var fields = index.Fields;
        var parts = new List<IndexKey>[fields.Length];
        bool any = false;
        long product = 1;
        for (int i = 0; i < fields.Length; i++)
        {
            var keys = ExtractIndexKeys(doc, fields[i].Path, out bool multi);
            multiKey |= multi;
            if (keys.Count == 0) keys.Add(s_missing);
            else any = true;
            if (fields[i].Descending)
                for (int j = 0; j < keys.Count; j++) keys[j] = keys[j] with { Key = IndexMeta.Inverted(keys[j].Key) };
            parts[i] = keys;
            product *= keys.Count;
            if (product > MaxCompoundKeysPerDocument)
                throw new FolioException($"Document produces more than {MaxCompoundKeysPerDocument} keys for compound index '{index.Name}' (several array fields).");
        }
        if (!any) return [];

        byte[] flag = fields.Length == 1 ? [] : [!multiKey && FieldsInOrder(doc, fields) ? IndexHint.InOrder : IndexHint.OutOfOrder];
        var result = new List<IndexKey>((int)product);
        Combine(parts, 0, [], [], result, flag);
        return result;
    }

    private static void Combine(List<IndexKey>[] parts, int i, byte[] key, byte[] hint, List<IndexKey> result, byte[] flag)
    {
        if (i == parts.Length)
        {
            result.Add(new(key, [.. hint, .. flag]));
            return;
        }
        foreach (var (k, h) in parts[i]) Combine(parts, i + 1, [.. key, .. k], [.. hint, .. h], result, flag);
    }

    /// <summary>True when the present index fields appear in the document (depth-first) in index order.</summary>
    private static bool FieldsInOrder(ReadOnlySpan<byte> doc, IndexField[] fields)
    {
        var raw = new RawDocument(doc);
        int last = -1;
        foreach (var f in fields)
        {
            var current = new RawValue(DocType.Document, raw.Data);
            bool found = true;
            foreach (var segment in f.Path.Split('.'))
            {
                if (current.Type != DocType.Document || !current.AsDocument.TryGetField(Encoding.UTF8.GetBytes(segment), out current))
                {
                    found = false;
                    break;
                }
            }
            // Values are slices of the document, so their offsets give the depth-first order. Missing (or empty)
            // values cannot be rebuilt from keys anyway (their hint is Unknown), so they do not constrain the order.
            if (!found || !raw.Data.Overlaps(current.Data, out int offset)) continue;
            if (offset <= last) return false;
            last = offset;
        }
        return true;
    }

    private static void Collect(RawValue v, byte[][] segments, int seg, List<IndexKey> keys, ByteBuffer buf, ref bool multiKey)
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
                    keys.Add(new(Encode(buf, item), IndexHint.For(item)));
                }
                if (!any) keys.Add(new(Encode(buf, v), IndexHint.For(v))); // empty array is indexed as []
            }
            else keys.Add(new(Encode(buf, v), IndexHint.For(v)));
            return;
        }
        if (v.Type == DocType.Document)
        {
            if (v.AsDocument.TryGetField(segments[seg], out var next)) Collect(next, segments, seg + 1, keys, buf, ref multiKey);
        }
        else if (v.Type == DocType.Array)
        {
            multiKey = true;
            if (int.TryParse(Encoding.UTF8.GetString(segments[seg]), out int position) && position >= 0)
            {
                int i = 0;
                foreach (var item in v.AsArray)
                    if (i++ == position)
                    {
                        Collect(item, segments, seg + 1, keys, buf, ref multiKey);
                        break;
                    }
                return;
            }
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
                : $"Value for index '{index.Name}' ({index.Field}) is too large ({size} bytes encoded, max {max}).");
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
        var perIndex = new List<(IndexMeta Index, List<IndexKey> Keys, bool Multi)>(meta.Indexes.Count);
        foreach (var index in meta.Indexes)
        {
            var keys = ExtractIndexKeys(bytes, index, out bool multi);
            foreach (var (k, _) in keys)
            {
                CheckKeySize(tx, index, k.Length + idKey.Length);
                if (index.Unique && ConflictsInUniqueIndex(new BTree(tx.Storage, index.Root), k, idKey))
                    throw new DuplicateKeyException($"Duplicate key in unique index '{index.Name}' of '{meta.Name}': {index.DescribeKey(k)}.");
            }
            perIndex.Add((index, keys, multi));
        }

        primary.Insert(idKey, bytes, overwrite: false);
        var idHint = perIndex.Count > 0 ? IndexHint.ForId(bytes) : [];
        bool metaChanged = false;
        foreach (var (index, keys, multi) in perIndex)
        {
            var tree = new BTree(tx.Storage, index.Root);
            foreach (var (k, hint) in keys) tree.Insert(Concat(k, idKey), IndexHint.Entry(idHint, hint), overwrite: true);
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
            foreach (var (k, _) in ExtractIndexKeys(docBytes, index, out _)) tree.Delete(Concat(k, idKey));
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

        var changes = new List<(IndexMeta Index, List<byte[]> Removed, List<IndexKey> Written, bool Multi)>();
        byte[] idHint = [];
        bool idHintChanged = false;
        if (meta.Indexes.Count > 0)
        {
            idHint = IndexHint.ForId(newBytes);
            idHintChanged = !idHint.AsSpan().SequenceEqual(IndexHint.ForId(oldBytes));
        }
        foreach (var index in meta.Indexes)
        {
            // Compound hints also record whether the fields are in index order, which a reorder can change.
            if (!idHintChanged && SameFields(oldBytes, newBytes, index.TopLevelFields)
                && (index.Fields.Length == 1 || FieldsInOrder(oldBytes, index.Fields) == FieldsInOrder(newBytes, index.Fields))) continue;
            var oldKeys = ExtractIndexKeys(oldBytes, index, out _);
            var newKeys = ExtractIndexKeys(newBytes, index, out bool multi);
            var removed = oldKeys.Where(o => !newKeys.Exists(n => n.Key.AsSpan().SequenceEqual(o.Key))).Select(o => o.Key).ToList();
            var added = newKeys.Where(n => !oldKeys.Exists(o => o.Key.AsSpan().SequenceEqual(n.Key))).ToList();
            // Same key but a different type (5 -> 5L) or _id type: the entry is rewritten with the new hint.
            var rehinted = newKeys.Where(n => oldKeys.Exists(o => o.Key.AsSpan().SequenceEqual(n.Key) && (idHintChanged || !o.Hint.AsSpan().SequenceEqual(n.Hint)))).ToList();
            foreach (var (k, _) in added)
            {
                CheckKeySize(tx, index, k.Length + idKey.Length);
                if (index.Unique && ConflictsInUniqueIndex(new BTree(tx.Storage, index.Root), k, idKey))
                    throw new DuplicateKeyException($"Duplicate key in unique index '{index.Name}' of '{meta.Name}': {index.DescribeKey(k)}.");
            }
            changes.Add((index, removed, [.. added, .. rehinted], multi));
        }

        bool metaChanged = false;
        foreach (var (index, removed, written, multi) in changes)
        {
            var tree = new BTree(tx.Storage, index.Root);
            foreach (var k in removed) tree.Delete(Concat(k, idKey));
            foreach (var (k, hint) in written) tree.Insert(Concat(k, idKey), IndexHint.Entry(idHint, hint), overwrite: true);
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

    private static bool SameFields(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, byte[][] names)
    {
        foreach (var name in names)
            if (!SameField(a, b, name)) return false;
        return true;
    }

    /// <summary>True when the first occurrence of a top-level field is byte-identical (or absent) in both documents.</summary>
    private static bool SameField(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> name)
    {
        bool inA = new RawDocument(a).TryGetField(name, out var va);
        bool inB = new RawDocument(b).TryGetField(name, out var vb);
        return inA == inB && (!inA || (va.Type == vb.Type && va.Data.SequenceEqual(vb.Data)));
    }

    // ------------------------------------------------------------------ reads

    public static Document? FindById(EngineTx tx, CollectionMeta? meta, DocValue id)
    {
        if (meta is null) return null;
        return new BTree(tx.Storage, meta.PrimaryRoot).TryGet(KeyEncoder.EncodeTemporary(id), out var bytes)
            ? DocumentSerializer.Deserialize(bytes)
            : null;
    }

    /// <summary>
    /// Zero-copy point lookup. The view aliases transaction page memory (or, for documents spanning several overflow
    /// pages, one freshly assembled buffer); callers must finish with it before the transaction is mutated or completed.
    /// </summary>
    public static bool TryBorrowById(EngineTx tx, CollectionMeta? meta, DocValue id, out DocumentView view)
    {
        if (meta is not null && new BTree(tx.Storage, meta.PrimaryRoot).TryGet(KeyEncoder.EncodeTemporary(id), out var bytes))
        {
            view = new DocumentView(new RawDocument(bytes));
            return true;
        }
        view = default;
        return false;
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

    public static long Visit(EngineTx tx, CollectionMeta? meta, Filter filter, Func<DocumentView, bool> visitor)
    {
        tx.Storage.ThrowIfFinished();
        if (meta is null) return 0;
        long visited = 0;
        var plan = QueryPlanner.Plan(meta, filter);
        // A covered access path already decides the result exactly (as Count relies on); skip re-evaluation.
        var check = plan.Covered ? null : filter;
        QueryPlanner.Execute(tx.Storage, meta, plan, (_, bytes) =>
        {
            var raw = new RawDocument(bytes);
            if (check is not null && !check.Matches(raw)) return true;
            visited++;
            return visitor(new DocumentView(raw));
        });
        return visited;
    }

    public static long Count(EngineTx tx, CollectionMeta? meta, Filter filter)
    {
        if (meta is null) return 0;
        long n = 0;
        var plan = QueryPlanner.Plan(meta, filter);
        if (plan.Covered)
        {
            QueryPlanner.ExecuteKeys(tx.Storage, meta, plan, (_, _, _) =>
            {
                n++;
                return true;
            });
            return n;
        }
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
        var projection = Projection.Parse(options?.Projection);
        var plan = QueryPlanner.Plan(meta, filter);

        if (sort is null && plan.Covered && projection is not null && plan.Kind == PlanKind.CompoundScan
            && projection.OnlyIncludes(plan.Index!.Fields.Select(f => f.Path)))
        {
            FindCompoundCovered(tx, meta, plan, projection, skip, limit, result);
            return result;
        }
        if (sort is null && plan.Kind != PlanKind.CompoundScan && plan.Covered && projection is not null && projection.OnlyIncludes(plan.KeyField))
        {
            FindCovered(tx, meta, plan, projection, skip, limit, result);
            return result;
        }

        var check = plan.Covered ? null : filter;
        if (sort is null)
        {
            int skipped = 0;
            QueryPlanner.Execute(tx.Storage, meta, plan, (_, doc) =>
            {
                var raw = new RawDocument(doc);
                if (check is not null && !check.Matches(raw)) return true;
                if (skipped < skip)
                {
                    skipped++;
                    return true;
                }
                result.Add(projection is null ? raw.ToDocument() : projection.Apply(raw));
                return result.Count < limit;
            });
            return result;
        }

        var rows = new List<(byte[][] Keys, int Seq, byte[] Bytes)>();
        QueryPlanner.Execute(tx.Storage, meta, plan, (_, doc) =>
        {
            var raw = new RawDocument(doc);
            if (check is null || check.Matches(raw)) rows.Add((sort.KeysFor(raw), rows.Count, raw.Data.ToArray()));
            return true;
        });
        // Stable: ties keep scan order. Only the returned window is materialized.
        rows.Sort((a, b) => sort.Compare(a.Keys, b.Keys) is var c and not 0 ? c : a.Seq.CompareTo(b.Seq));
        for (int i = skip; i < rows.Count && result.Count < limit; i++)
        {
            var raw = new RawDocument(rows[i].Bytes);
            result.Add(projection is null ? raw.ToDocument() : projection.Apply(raw));
        }
        return result;
    }

    /// <summary>
    /// Inclusion projection of the scan key field (and/or _id) answered from index keys. Keys whose decoded type is
    /// ambiguous (numbers share one encoding across int32/int64/double/decimal; nested values contain numbers) fall
    /// back to reading that document, so results are identical to the non-covered path.
    /// </summary>
    private static void FindCovered(EngineTx tx, CollectionMeta meta, QueryPlan plan, Projection projection, int skip, int limit, List<Document> result)
    {
        var fetch = new BTree(tx.Storage, meta.PrimaryRoot).CreateCursor();
        string field = plan.KeyField;
        bool includeField = field != "_id" && projection.HasPaths;
        bool includeId = projection.IncludesId;
        int skipped = 0;
        QueryPlanner.ExecuteKeys(tx.Storage, meta, plan, (idKey, valueKey, hints) =>
        {
            if (skipped < skip)
            {
                skipped++;
                return true;
            }
            ReadOnlySpan<byte> idHint = default, valueHint = default;
            if (!hints.IsEmpty && !IndexHint.TrySplit(hints, out idHint, out valueHint)) idHint = valueHint = [IndexHint.Unknown];
            DocValue id = default, value = default;
            if ((includeId && !IndexHint.TryDecode(idKey, idHint, out id)) || (includeField && !IndexHint.TryDecode(valueKey, valueHint, out value)))
            {
                if (!fetch.SeekExact(idKey)) throw new CorruptDatabaseException("Index references a missing document.");
                result.Add(projection.Apply(new RawDocument(fetch.Value)));
            }
            else
            {
                var doc = new Document();
                if (includeId) doc.Set("_id", id);
                if (includeField) doc.SetPath(field, value);
                result.Add(doc);
            }
            return result.Count < limit;
        }, withHints: includeId || includeField);
    }

    // ------------------------------------------------------------------ indexes

    private static void FindCompoundCovered(EngineTx tx, CollectionMeta meta, QueryPlan plan, Projection projection, int skip, int limit, List<Document> result)
    {
        var index = plan.Index!;
        var fetch = new BTree(tx.Storage, meta.PrimaryRoot).CreateCursor();
        int skipped = 0;
        QueryPlanner.ExecuteKeys(tx.Storage, meta, plan, (idKey, valueKey, hints) =>
        {
            if (skipped++ < skip) return true;
            Span<Range> ranges = stackalloc Range[index.Fields.Length];
            bool exact = IndexHint.TrySplit(hints, index.Fields.Length, out var idRange, ranges, out bool inOrder)
                         && inOrder && index.Fields.All(f => !f.Path.Contains('.'));
            var doc = new Document();
            if (exact && projection.IncludesId)
            {
                exact = IndexHint.TryDecode(idKey, hints[idRange], out var id);
                if (exact) doc["_id"] = id;
            }
            int pos = 0;
            for (int i = 0; exact && i < index.Fields.Length; i++)
            {
                int len = IndexMeta.ComponentLength(valueKey[pos..], index.Fields[i].Descending);
                if (projection.IncludesPath(index.Fields[i].Path))
                {
                    exact = IndexHint.TryDecode(index.Original(valueKey.Slice(pos, len), i), hints[ranges[i]], out var value);
                    if (exact) doc[index.Fields[i].Path] = value;
                }
                pos += len;
            }
            if (!exact)
            {
                if (!fetch.SeekExact(idKey)) throw new CorruptDatabaseException("Index references a missing document.");
                doc = projection.Apply(new RawDocument(fetch.Value));
            }
            result.Add(doc);
            return result.Count < limit;
        }, withHints: true);
    }

    public static string CreateIndex(EngineTx tx, CollectionMeta meta, string field, bool unique)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        return CreateIndex(tx, meta, new Document { [field] = 1 }, unique);
    }

    public static string CreateIndex(EngineTx tx, CollectionMeta meta, Document keys, bool unique)
    {
        var fields = IndexMeta.ParsePattern(keys);
        if (fields.Length == 1 && fields[0].Path == "_id") return "_id_";
        if (fields.Select(f => f.Path).Distinct().Count() != fields.Length) throw new FolioException("An index cannot contain the same field twice.");

        var existing = meta.Indexes.FirstOrDefault(i => i.SameFields(fields));
        if (existing is not null)
        {
            if (existing.Unique != unique) throw new FolioException($"Index '{existing.Name}' already exists with different options.");
            return existing.Name;
        }
        string name = IndexMeta.DefaultName(fields);
        string baseName = name;
        for (int suffix = 2; meta.Indexes.Exists(i => i.Name == name); suffix++) name = baseName + "_" + suffix;

        var index = new IndexMeta { Name = name, Fields = fields, Unique = unique, Root = BTree.Create(tx.Storage) };
        var tree = new BTree(tx.Storage, index.Root);
        var primary = new BTree(tx.Storage, meta.PrimaryRoot);
        var cur = primary.CreateCursor();
        for (bool ok = cur.SeekFirst(); ok; ok = cur.MoveNext())
        {
            var idKey = cur.Key.ToArray();
            var docBytes = cur.Value;
            var idHint = IndexHint.ForId(docBytes);
            foreach (var (k, hint) in ExtractIndexKeys(docBytes, index, out bool multi))
            {
                CheckKeySize(tx, index, k.Length + idKey.Length);
                if (unique && ConflictsInUniqueIndex(tree, k, idKey))
                    throw new DuplicateKeyException($"Cannot create unique index '{name}': duplicate value {index.DescribeKey(k)}.");
                tree.Insert(Concat(k, idKey), IndexHint.Entry(idHint, hint), overwrite: true);
                if (multi) index.MultiKey = true;
            }
        }
        meta.Indexes.Add(index);
        tx.SaveCollection(meta);
        return index.Name;
    }

    public static bool DropIndex(EngineTx tx, CollectionMeta meta, string nameOrField)
    {
        var index = FindIndex(meta, nameOrField);
        if (index is null) return false;
        new BTree(tx.Storage, index.Root).Drop();
        meta.Indexes.Remove(index);
        tx.SaveCollection(meta);
        return true;
    }

    public static bool DropIndex(EngineTx tx, CollectionMeta meta, Document keys)
    {
        var fields = IndexMeta.ParsePattern(keys);
        var index = meta.Indexes.FirstOrDefault(i => i.SameFields(fields));
        return index is not null && DropIndex(tx, meta, index.Name);
    }

    private static IndexMeta? FindIndex(CollectionMeta meta, string nameOrField) =>
        meta.Indexes.FirstOrDefault(i => i.Name == nameOrField)
        ?? meta.Indexes.FirstOrDefault(i => i.IsSimple && i.Field == nameOrField);

    public static bool RebuildIndex(EngineTx tx, CollectionMeta meta, string nameOrField) =>
        RebuildIndex(tx, meta, FindIndex(meta, nameOrField));

    public static bool RebuildIndex(EngineTx tx, CollectionMeta meta, Document keys)
    {
        var fields = IndexMeta.ParsePattern(keys);
        return RebuildIndex(tx, meta, meta.Indexes.FirstOrDefault(i => i.SameFields(fields)));
    }

    private static bool RebuildIndex(EngineTx tx, CollectionMeta meta, IndexMeta? old)
    {
        if (old is null) return false;
        using var sorted = new IndexSort();
        var primary = new BTree(tx.Storage, meta.PrimaryRoot).CreateCursor();
        bool multiKey = false;
        for (bool ok = primary.SeekFirst(); ok; ok = primary.MoveNext())
        {
            var idKey = primary.Key;
            var bytes = primary.Value;
            var idHint = IndexHint.ForId(bytes);
            var keys = ExtractIndexKeys(bytes, old, out bool multi);
            multiKey |= multi;
            foreach (var (key, hint) in keys)
            {
                CheckKeySize(tx, old, key.Length + idKey.Length);
                sorted.Add(Concat(key, idKey), key.Length, IndexHint.Entry(idHint, hint));
            }
        }

        var builder = new BTree.BulkBuilder(tx.Storage);
        byte[]? previousKey = null, previousValue = null;
        IndexSort.Entry? pending = null;
        foreach (var entry in sorted.Sorted())
        {
            if (pending is { } last && !last.Key.AsSpan().SequenceEqual(entry.Key))
            {
                builder.Add(last.Key, last.Hint);
                pending = null;
            }
            var value = entry.Key.AsSpan(0, entry.ValueLength);
            if (old.Unique && previousValue is not null && value.SequenceEqual(previousValue)
                && !entry.Key.AsSpan().SequenceEqual(previousKey))
                throw new DuplicateKeyException($"Cannot rebuild unique index '{old.Name}': duplicate value {old.DescribeKey(value)}.");
            previousKey = entry.Key;
            previousValue = value.ToArray();
            pending = entry; // Repeated keys from one document keep the last hint, as regular insertion does.
        }
        if (pending is { } final) builder.Add(final.Key, final.Hint);
        var replacement = new IndexMeta
        {
            Name = old.Name,
            Fields = old.Fields,
            Unique = old.Unique,
            Root = builder.Finish(),
            MultiKey = multiKey,
        };
        meta.Indexes[meta.Indexes.IndexOf(old)] = replacement;
        tx.SaveCollection(meta);
        new BTree(tx.Storage, old.Root).Drop();
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
/// <summary>
/// Field projection applied directly to serialized documents. Paths follow the same rules as filters and updates:
/// on a document a segment is a field name; on an array a numeric segment selects that element (positional) and any
/// other segment applies to every element. Inclusion keeps the first occurrence of each field, emits <c>_id</c> first,
/// keeps matched sub-documents (possibly empty) and drops array elements that are not documents/arrays; exclusion
/// copies everything else unchanged. Ambiguous specifications are rejected: a path and one of its prefixes
/// (<c>{ a: 1, 'a.b': 1 }</c>), or positional and field segments under the same parent (<c>{ 'a.0': 1, 'a.b': 1 }</c>).
/// Operators: <c>{ path: { $slice: n | -n | [skip, limit] } }</c> keeps part of an array (neutral: alone it keeps all
/// other fields) and <c>{ field: { $elemMatch: {...} } }</c> keeps the first matching element (top-level, inclusion).
/// </summary>
internal sealed class Projection
{
    private sealed class Node
    {
        public readonly List<(string Name, byte[] Utf8, int Position, Node Child)> Children = [];
        public bool Positional;
        public bool IsLeaf => Children.Count == 0;
        public bool HasSlice;
        public long SliceSkip, SliceLimit;
        public FieldFilter? ElemMatch;

        public int Find(ReadOnlySpan<byte> utf8)
        {
            for (int i = 0; i < Children.Count; i++)
                if (utf8.SequenceEqual(Children[i].Utf8)) return i;
            return -1;
        }

        public int FindPosition(int position)
        {
            for (int i = 0; i < Children.Count; i++)
                if (Children[i].Position == position) return i;
            return -1;
        }
    }

    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    private readonly Node _root;
    private readonly List<string> _paths;
    private readonly bool _include;
    private readonly bool _includeId;
    private readonly bool _idInTree; // a sub-path of _id ('_id.x') was given: the tree decides what of _id is kept
    private readonly bool _hasOperators;

    private Projection(Node root, List<string> paths, bool include, bool includeId, bool idInTree, bool hasOperators)
    {
        _hasOperators = hasOperators;
        _root = root;
        _paths = paths;
        _include = include;
        _includeId = includeId;
        _idInTree = idInTree;
    }

    public static Projection? Parse(Document? spec)
    {
        if (spec is null || spec.Count == 0) return null;
        bool? include = null;
        bool includeId = true, idSpecified = false;
        var paths = new List<string>();
        var root = new Node();
        bool hasOperators = false;
        foreach (var (k, v) in spec)
        {
            if (v.Type == DocType.Document)
            {
                if (k == "_id") throw new FolioException("Projection operators cannot be applied to _id.");
                var leaf = AddPath(root, k);
                ParseOperator(k, v.AsDocument, leaf);
                if (leaf.ElemMatch is not null)
                {
                    if (include == false) throw new FolioException("$elemMatch cannot be used in an exclusion projection.");
                    include = true;
                }
                hasOperators = true;
                continue;
            }
            if (v.Type != DocType.Boolean && !v.IsNumber) throw new FolioException($"Invalid projection value for '{k}': expected 1/0, true/false or an operator.");
            bool on = v.Type == DocType.Boolean ? v.AsBoolean : v.AsDouble != 0;
            if (k == "_id")
            {
                includeId = on;
                idSpecified = true;
                continue;
            }
            if (include is null) include = on;
            else if (include != on) throw new FolioException("Projection cannot mix inclusion and exclusion (except for _id).");
            AddPath(root, k);
            paths.Add(k);
        }
        bool idInTree = root.Find("_id"u8) >= 0;
        if (idInTree && idSpecified) throw new FolioException("Projection path collision at '_id'.");
        foreach (var c in root.Children) Validate(c.Child, c.Name); // the root is always a document: no positions
        // { _id: 1 } alone is an inclusion of _id only; { _id: 0 } alone excludes it.
        // Only $slice given: like Mongo, all other fields are kept.
        bool mode = include ?? (!hasOperators && idSpecified && includeId);
        return new Projection(root, paths, mode, includeId, idInTree, hasOperators);
    }

    private static void ParseOperator(string path, Document op, Node leaf)
    {
        if (op.Count != 1) throw new FolioException($"Invalid projection for '{path}': use dotted paths or a single operator ($slice, $elemMatch).");
        var (name, arg) = op.First();
        switch (name)
        {
            case "$slice":
                leaf.HasSlice = true;
                if (arg.IsNumber && IsInteger(arg))
                {
                    long n = arg.AsInt64;
                    (leaf.SliceSkip, leaf.SliceLimit) = n >= 0 ? (0L, n) : (n, long.MaxValue);
                }
                else if (arg.Type == DocType.Array && arg.AsArray.Count == 2 && arg.AsArray.All(IsInteger))
                {
                    leaf.SliceSkip = arg.AsArray[0].AsInt64;
                    leaf.SliceLimit = arg.AsArray[1].AsInt64;
                    if (leaf.SliceLimit <= 0) throw new FolioException($"$slice limit for '{path}' must be positive.");
                }
                else throw new FolioException($"$slice for '{path}' requires an integer or [skip, limit].");
                break;
            case "$elemMatch":
                if (path.Contains('.')) throw new FolioException($"$elemMatch projection requires a top-level field, got '{path}'.");
                if (arg.Type != DocType.Document) throw new FolioException("$elemMatch requires a document.");
                leaf.ElemMatch = (FieldFilter)FilterParser.Parse(new Document { ["e"] = new Document { ["$elemMatch"] = arg } });
                break;
            default:
                throw new FolioException($"Unknown projection operator '{name}' for '{path}'.");
        }
    }

    private static bool IsInteger(DocValue v) => v.IsNumber && v.Type switch
    {
        DocType.Int32 or DocType.Int64 => true,
        DocType.Double => v.AsDouble is > -1e18 and < 1e18 && double.IsInteger(v.AsDouble),
        DocType.Decimal => v.AsDecimal is > -1e18m and < 1e18m && decimal.IsInteger(v.AsDecimal),
        _ => false,
    };

    private static Node AddPath(Node root, string path)
    {
        var segments = path.Split('.');
        if (segments.Length > DocumentSerializer.MaxDepth) throw new FolioException($"Projection path '{path}' is too deep.");
        var node = root;
        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (seg.Length == 0 || seg[0] == '$') throw new FolioException($"Invalid projection path '{path}'.");
            byte[] utf8;
            try { utf8 = s_strictUtf8.GetBytes(seg); }
            catch (ArgumentException) { throw new FolioException($"Invalid projection path '{path}'."); }
            int ci = node.Find(utf8);
            bool last = i == segments.Length - 1;
            if (ci >= 0 && (last || node.Children[ci].Child.IsLeaf))
                throw new FolioException($"Projection path collision at '{string.Join('.', segments[..(i + 1)])}'.");
            if (ci < 0)
            {
                int position = int.TryParse(seg, out int p) && p >= 0 ? p : -1;
                // Below the root a segment may address an array, where '1' and '01' are the same position.
                if (position >= 0 && node != root && node.FindPosition(position) >= 0)
                    throw new FolioException($"Projection path collision at '{string.Join('.', segments[..(i + 1)])}'.");
                node.Children.Add((seg, utf8, position, new Node()));
                ci = node.Children.Count - 1;
            }
            node = node.Children[ci].Child;
        }
        return node;
    }

    private static void Validate(Node node, string prefix)
    {
        if (node.IsLeaf) return;
        int positional = node.Children.Count(c => c.Position >= 0);
        if (positional > 0 && positional < node.Children.Count)
            throw new FolioException($"Ambiguous projection under '{prefix}': cannot mix array positions and field names.");
        node.Positional = positional > 0;
        foreach (var c in node.Children) Validate(c.Child, prefix.Length == 0 ? c.Name : prefix + "." + c.Name);
    }

    public bool IsInclusion => _include;
    public bool IncludesId => _includeId && !_idInTree;
    public bool HasPaths => _paths.Count > 0;

    /// <summary>True when every included path is <paramref name="field"/> (so the result can be built from its index key).</summary>
    public bool OnlyIncludes(string field) => _include && !_idInTree && !_hasOperators && _paths.TrueForAll(p => p == field);
    public bool OnlyIncludes(IEnumerable<string> fields) => _include && !_idInTree && !_hasOperators && _paths.All(fields.Contains);
    public bool IncludesPath(string path) => _paths.Contains(path);

    /// <summary>Projects straight from the serialized document, materializing only the selected values.</summary>
    public Document Apply(RawDocument raw)
    {
        var result = new Document();
        if (_include)
        {
            if (_includeId && !_idInTree && raw.TryGetField("_id"u8, out var id)) result.AddUnchecked("_id", id.ToDocValue());
            IncludeFields(raw, _root, result, top: !_idInTree);
        }
        else ExcludeFields(raw, _root, result, dropId: !_includeId);
        return result;
    }

    private static void IncludeFields(RawDocument doc, Node node, Document into, bool top)
    {
        int n = node.Children.Count;
        Span<bool> seen = n <= 64 ? stackalloc bool[64] : new bool[n];
        foreach (var f in doc)
        {
            if (top && f.Name.SequenceEqual("_id"u8)) continue;
            int ci = node.Find(f.Name);
            if (ci < 0 || seen[ci]) continue; // first occurrence only, as filters and indexes see it
            seen[ci] = true;
            var (name, _, _, child) = node.Children[ci];
            if (TryInclude(f.Value, child, out var v)) into.AddUnchecked(name, v);
        }
    }

    private static bool TryInclude(RawValue v, Node node, out DocValue result)
    {
        if (node.IsLeaf) return TryLeaf(v, node, out result);
        switch (v.Type)
        {
            case DocType.Document:
            {
                var d = new Document();
                IncludeFields(v.AsDocument, node, d, top: false);
                result = d;
                return true;
            }
            case DocType.Array:
            {
                var arr = new DocArray();
                int i = 0;
                foreach (var item in v.AsArray)
                {
                    if (node.Positional)
                    {
                        int ci = node.FindPosition(i++);
                        if (ci < 0) continue;
                        if (TryInclude(item, node.Children[ci].Child, out var x)) arr.Add(x);
                    }
                    else if (item.Type is DocType.Document or DocType.Array && TryInclude(item, node, out var x)) arr.Add(x);
                }
                result = arr;
                return true;
            }
            default:
                result = default;
                return false;
        }
    }

    /// <summary>Value of a projected leaf: as is, sliced ($slice) or its first matching element ($elemMatch, else omitted).</summary>
    private static bool TryLeaf(RawValue v, Node leaf, out DocValue result)
    {
        if (leaf.ElemMatch is not null)
        {
            result = default;
            if (v.Type != DocType.Array) return false;
            foreach (var item in v.AsArray)
            {
                if (!leaf.ElemMatch.MatchesElement(item)) continue;
                result = new DocArray { item.ToDocValue() };
                return true;
            }
            return false;
        }
        if (!leaf.HasSlice || v.Type != DocType.Array)
        {
            result = v.ToDocValue();
            return true;
        }
        long count = 0;
        foreach (var _ in v.AsArray) count++;
        long start = leaf.SliceSkip >= 0 ? Math.Min(leaf.SliceSkip, count) : Math.Max(count + leaf.SliceSkip, 0);
        long end = start + Math.Min(leaf.SliceLimit, count - start);
        var arr = new DocArray();
        long i = 0;
        foreach (var item in v.AsArray)
        {
            if (i >= end) break;
            if (i++ >= start) arr.Add(item.ToDocValue());
        }
        result = arr;
        return true;
    }

    private static void ExcludeFields(RawDocument doc, Node node, Document into, bool dropId)
    {
        foreach (var f in doc)
        {
            if (dropId && f.Name.SequenceEqual("_id"u8)) continue;
            int ci = node.Find(f.Name);
            if (ci < 0) into.AddUnchecked(Encoding.UTF8.GetString(f.Name), f.Value.ToDocValue());
            else if (KeepExcluded(node.Children[ci].Child)) into.AddUnchecked(node.Children[ci].Name, Exclude(f.Value, node.Children[ci].Child));
        }
    }

    /// <summary>In an exclusion projection a plain leaf removes the value; a $slice leaf keeps it (sliced).</summary>
    private static bool KeepExcluded(Node node) => !node.IsLeaf || node.HasSlice;

    private static DocValue Exclude(RawValue v, Node node)
    {
        if (node.IsLeaf)
        {
            TryLeaf(v, node, out var sliced);
            return sliced;
        }
        switch (v.Type)
        {
            case DocType.Document:
            {
                var d = new Document();
                ExcludeFields(v.AsDocument, node, d, dropId: false);
                return d;
            }
            case DocType.Array:
            {
                var arr = new DocArray();
                int i = 0;
                foreach (var item in v.AsArray)
                {
                    if (node.Positional)
                    {
                        int ci = node.FindPosition(i++);
                        if (ci < 0) arr.Add(item.ToDocValue());
                        else if (KeepExcluded(node.Children[ci].Child)) arr.Add(Exclude(item, node.Children[ci].Child));
                    }
                    else arr.Add(item.Type is DocType.Document or DocType.Array ? Exclude(item, node) : item.ToDocValue());
                }
                return arr;
            }
            default:
                return v.ToDocValue();
        }
    }
}
