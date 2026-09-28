using FolioDb.Storage;

namespace FolioDb.Engine;

/// <summary>Engine-level transaction: storage transaction plus a per-transaction catalog cache.</summary>
internal sealed class EngineTx : IDisposable
{
    private readonly Dictionary<string, CollectionMeta?> _catalogCache = new(StringComparer.Ordinal);

    public StorageTx Storage { get; }
    public bool IsWritable => Storage.IsWritable;

    public EngineTx(StorageTx storage) => Storage = storage;

    private BTree Catalog => new(Storage, Storage.CatalogRoot);

    private static byte[] CatalogKey(string name) => KeyEncoder.Encode(name);

    public static void ValidateCollectionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128) throw new FolioException("Collection name is too long (max 128 characters).");
        foreach (char c in name)
            if (char.IsControl(c)) throw new FolioException("Collection name contains control characters.");
    }

    public CollectionMeta? GetCollection(string name)
    {
        if (_catalogCache.TryGetValue(name, out var cached)) return cached;
        CollectionMeta? meta = null;
        if (Catalog.TryGet(CatalogKey(name), out var bytes)) meta = CollectionMeta.FromDocument(DocumentSerializer.Deserialize(bytes));
        _catalogCache[name] = meta;
        return meta;
    }

    public CollectionMeta GetOrCreateCollection(string name)
    {
        var meta = GetCollection(name);
        if (meta is not null) return meta;
        ValidateCollectionName(name);
        meta = new CollectionMeta { Name = name, PrimaryRoot = BTree.Create(Storage) };
        SaveCollection(meta);
        return meta;
    }

    public void SaveCollection(CollectionMeta meta)
    {
        Catalog.Insert(CatalogKey(meta.Name), DocumentSerializer.Serialize(meta.ToDocument()), overwrite: true);
        _catalogCache[meta.Name] = meta;
    }

    public bool DropCollection(string name)
    {
        var meta = GetCollection(name);
        if (meta is null) return false;
        foreach (var idx in meta.Indexes) new BTree(Storage, idx.Root).Drop();
        new BTree(Storage, meta.PrimaryRoot).Drop();
        Catalog.Delete(CatalogKey(name));
        _catalogCache[name] = null;
        return true;
    }

    public List<string> ListCollections()
    {
        var names = new List<string>();
        var cur = Catalog.CreateCursor();
        for (bool ok = cur.SeekFirst(); ok; ok = cur.MoveNext())
            names.Add(new RawDocument(cur.Value).ToDocument()["name"].AsString);
        return names;
    }

    public void Commit() => Storage.Commit();
    public void Dispose() => Storage.Dispose();
}
