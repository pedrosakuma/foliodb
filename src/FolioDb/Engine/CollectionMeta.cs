using FolioDb.Query;

namespace FolioDb.Engine;

/// <summary>One component of an index key pattern.</summary>
internal readonly record struct IndexField(string Path, bool Descending);

internal sealed class IndexMeta
{
    public const int MaxFields = 32;

    public required string Name { get; init; }
    public required IndexField[] Fields { get; init; }
    public required bool Unique { get; init; }
    public required uint Root { get; init; }
    /// <summary>True once any indexed document produced more than one key (array value).</summary>
    public bool MultiKey { get; set; }

    /// <summary>
    /// Simple non-unique indexes created by this version also hold an entry (as null) for documents missing the field,
    /// which is how sorts treat them. That makes the index a complete ordering of the collection. Legacy indexes
    /// (and unique ones, where several missing fields would collide) do not.
    /// </summary>
    public bool IndexesMissing { get; init; }

    /// <summary>Opted out of indexing missing fields at creation (<c>sparse: true</c>); kept across rebuilds and vacuum.</summary>
    public bool Sparse { get; init; }

    /// <summary>
    /// A plain ascending single-field index. Its entries are <c>value ++ idKey</c> and documents missing the field are
    /// not indexed. Other (compound or descending) indexes store one component per field (descending components have
    /// their bytes inverted, which reverses their order because encodings are prefix-free), and index a document when
    /// at least one field is present (missing ones as null).
    /// </summary>
    /// <summary>This index as (re)built by this version: simple non-unique indexes also index missing fields.</summary>
    public IndexMeta WithMissing() => new()
    {
        Name = Name, Fields = Fields, Unique = Unique, Root = Root, MultiKey = MultiKey, Sparse = Sparse,
        IndexesMissing = !Unique && IsSimple && !Sparse,
    };

    public bool IsSimple => Fields.Length == 1 && !Fields[0].Descending;

    /// <summary>Indexed path for single-field indexes, comma-separated paths for compound ones.</summary>
    public string Field => Fields.Length == 1 ? Fields[0].Path : string.Join(",", Fields.Select(f => f.Path));

    private byte[][]? _topLevelFields;
    /// <summary>UTF-8 names of the first path segments: index keys depend only on these top-level fields.</summary>
    public byte[][] TopLevelFields => _topLevelFields ??= Paths.Select(p => p.Segments[0]).Distinct(ByteArrayComparer.Instance).ToArray();

    private FieldPath[]? _paths;
    /// <summary>Indexed paths with their UTF-8 segments decoded once, in <see cref="Fields"/> order.</summary>
    public FieldPath[] Paths => _paths ??= Fields.Select(f => new FieldPath(f.Path)).ToArray();

    public bool SameFields(IndexField[] fields) => Fields.AsSpan().SequenceEqual(fields);

    public Document KeyPattern()
    {
        var d = new Document();
        foreach (var f in Fields) d[f.Path] = f.Descending ? -1 : 1;
        return d;
    }

    public static string DefaultName(IndexField[] fields) => string.Join("_", fields.Select(f => f.Path + (f.Descending ? "_-1" : "_1")));

    /// <summary>Validates a key pattern such as <c>{ a: 1, b: -1 }</c>.</summary>
    public static IndexField[] ParsePattern(Document keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ValidateFieldCount(keys.Count);
        var fields = new IndexField[keys.Count];
        int i = 0;
        foreach (var (path, dir) in keys)
            fields[i++] = ParseField(path, dir, keys.Count);
        return fields;
    }

    private static IndexField[] ParsePattern(DocumentView keys)
    {
        int count = keys.FieldCount;
        ValidateFieldCount(count);
        var fields = new IndexField[count];
        int i = 0;
        foreach (var field in keys)
            fields[i++] = ParseField(field.GetName(), field.Value.ToDocValue(), count);
        return fields;
    }

    private static void ValidateFieldCount(int count)
    {
        if (count == 0) throw new FolioException("An index needs at least one field.");
        if (count > MaxFields) throw new FolioException($"An index can have at most {MaxFields} fields.");
    }

    private static IndexField ParseField(string path, DocValue dir, int count)
    {
        ValidatePath(path);
        if (count > 1 && path == "_id") throw new FolioException("_id cannot be part of a compound index.");
        if (!dir.IsNumber || (KeyEncoder.Compare(dir, 1) != 0 && KeyEncoder.Compare(dir, -1) != 0)) throw new FolioException($"Index direction for '{path}' must be 1 or -1.");
        return new IndexField(path, dir.AsDouble < 0);
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new FolioException("Index field names cannot be empty.");
        if (path.StartsWith('$') || path.StartsWith('.') || path.Contains("..") || path.EndsWith('.')) throw new FolioException($"Invalid index field '{path}'.");
    }

    // ------------------------------------------------------------------ key layout

    /// <summary>Length of the component at the start of <paramref name="key"/>.</summary>
    public static int ComponentLength(ReadOnlySpan<byte> key, bool descending)
    {
        return descending ? KeyEncoder.EncodedLengthInverted(key) : KeyEncoder.EncodedLength(key);
    }

    public static void Invert(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i++) destination[i] = (byte)~source[i];
    }

    public static byte[] Inverted(ReadOnlySpan<byte> source)
    {
        var r = new byte[source.Length];
        Invert(source, r);
        return r;
    }

    /// <summary>Byte length of the value part (all components) at the start of an index entry.</summary>
    public int ValueLength(ReadOnlySpan<byte> entry, int fromComponent = 0, int offset = 0)
    {
        int pos = offset;
        for (int i = fromComponent; i < Fields.Length; i++) pos += ComponentLength(entry[pos..], Fields[i].Descending);
        return pos;
    }

    /// <summary>Decodes component <paramref name="i"/> (already sliced to its bytes) to its original, non-inverted encoding.</summary>
    public byte[] Original(ReadOnlySpan<byte> component, int i) => Fields[i].Descending ? Inverted(component) : component.ToArray();

    /// <summary>Human-readable value of an encoded key (for error messages).</summary>
    public string DescribeKey(ReadOnlySpan<byte> valueKey)
    {
        if (Fields.Length == 1) return DocJson.WriteValue(KeyEncoder.Decode(Original(valueKey, 0), out _));
        var d = new Document();
        int pos = 0;
        for (int i = 0; i < Fields.Length; i++)
        {
            int len = ComponentLength(valueKey[pos..], Fields[i].Descending);
            d[Fields[i].Path] = KeyEncoder.Decode(Original(valueKey.Slice(pos, len), i), out _);
            pos += len;
        }
        return DocJson.WriteValue(d);
    }

    // ------------------------------------------------------------------ catalog

    public Document ToDocument()
    {
        var d = new Document { ["name"] = Name };
        // Simple indexes keep the original catalog shape.
        if (IsSimple) d["field"] = Fields[0].Path;
        else d["keys"] = KeyPattern();
        d["unique"] = Unique;
        d["root"] = (long)Root;
        d["multiKey"] = MultiKey;
        if (IndexesMissing) d["missing"] = true;
        if (Sparse) d["sparse"] = true;
        return d;
    }

    public static IndexMeta FromView(DocumentView d) => new()
    {
        Name = CollectionMeta.Field(d, "name"u8).GetString(),
        Fields = d.TryGetValue("keys"u8, out var keys) ? ParsePattern(keys.AsDocument) : [new IndexField(CollectionMeta.Field(d, "field"u8).GetString(), false)],
        Unique = CollectionMeta.Field(d, "unique"u8).AsBoolean,
        Root = (uint)CollectionMeta.Field(d, "root"u8).AsInt64,
        MultiKey = CollectionMeta.Field(d, "multiKey"u8).AsBoolean,
        IndexesMissing = d.TryGetValue("missing"u8, out var missing) && missing.AsBoolean,
        Sparse = d.TryGetValue("sparse"u8, out var sparse) && sparse.AsBoolean,
    };
}

internal sealed class CollectionMeta
{
    public required string Name { get; init; }
    public required uint PrimaryRoot { get; init; }
    public List<IndexMeta> Indexes { get; } = new();

    public Document ToDocument()
    {
        var arr = new DocArray();
        foreach (var i in Indexes) arr.Add(i.ToDocument());
        return new Document
        {
            ["name"] = Name,
            ["primaryRoot"] = (long)PrimaryRoot,
            ["indexes"] = arr,
        };
    }

    internal static DocValueView Field(DocumentView d, scoped ReadOnlySpan<byte> name)
    {
        d.TryGetValue(name, out var value);
        return value;
    }

    public static CollectionMeta FromBytes(ReadOnlySpan<byte> bytes)
    {
        var d = new DocumentView(new RawDocument(bytes));
        var meta = new CollectionMeta
        {
            Name = Field(d, "name"u8).GetString(),
            PrimaryRoot = (uint)Field(d, "primaryRoot"u8).AsInt64,
        };
        foreach (var i in Field(d, "indexes"u8).AsArray) meta.Indexes.Add(IndexMeta.FromView(i.AsDocument));
        return meta;
    }
}
