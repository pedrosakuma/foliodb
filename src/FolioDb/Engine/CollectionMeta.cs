namespace FolioDb.Engine;

internal sealed class IndexMeta
{
    public required string Name { get; init; }
    public required string Field { get; init; }
    public required bool Unique { get; init; }
    public required uint Root { get; init; }
    /// <summary>True once any indexed document produced more than one key (array value).</summary>
    public bool MultiKey { get; set; }

    public Document ToDocument() => new()
    {
        ["name"] = Name,
        ["field"] = Field,
        ["unique"] = Unique,
        ["root"] = (long)Root,
        ["multiKey"] = MultiKey,
    };

    public static IndexMeta FromDocument(Document d) => new()
    {
        Name = d["name"].AsString,
        Field = d["field"].AsString,
        Unique = d["unique"].AsBoolean,
        Root = (uint)d["root"].AsInt64,
        MultiKey = d["multiKey"].AsBoolean,
    };
}

internal sealed class CollectionMeta
{
    public required string Name { get; init; }
    public required uint PrimaryRoot { get; init; }
    public List<IndexMeta> Indexes { get; } = new();

    public IndexMeta? FindIndexByField(string field)
    {
        foreach (var i in Indexes)
            if (i.Field == field) return i;
        return null;
    }

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

    public static CollectionMeta FromDocument(Document d)
    {
        var meta = new CollectionMeta
        {
            Name = d["name"].AsString,
            PrimaryRoot = (uint)d["primaryRoot"].AsInt64,
        };
        foreach (var i in d["indexes"].AsArray) meta.Indexes.Add(IndexMeta.FromDocument(i.AsDocument));
        return meta;
    }
}
