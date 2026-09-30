using FolioDb.Storage;

namespace FolioDb.Engine;

/// <summary>Engine-level transaction: storage transaction plus a per-transaction catalog cache.</summary>
internal sealed class EngineTx : IDisposable
{
    // Almost every transaction touches a single collection, so the first entry lives in a pair of fields and the
    // dictionary is only allocated once a second collection name shows up.
    private string? _cachedName;
    private CollectionMeta? _cachedMeta;
    private Dictionary<string, CollectionMeta?>? _catalogCache;

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

    private bool TryGetCached(string name, out CollectionMeta? meta)
    {
        if (string.Equals(_cachedName, name, StringComparison.Ordinal))
        {
            meta = _cachedMeta;
            return true;
        }
        if (_catalogCache is not null) return _catalogCache.TryGetValue(name, out meta);
        meta = null;
        return false;
    }

    private void Cache(string name, CollectionMeta? meta)
    {
        if (_cachedName is null || string.Equals(_cachedName, name, StringComparison.Ordinal))
        {
            _cachedName = name;
            _cachedMeta = meta;
            return;
        }
        (_catalogCache ??= new(StringComparer.Ordinal))[name] = meta;
    }

    public CollectionMeta? GetCollection(string name)
    {
        if (TryGetCached(name, out var cached)) return cached;
        CollectionMeta? meta = null;
        if (Catalog.TryGet(CatalogKey(name), out var bytes)) meta = CollectionMeta.FromBytes(bytes);
        Cache(name, meta);
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
        Cache(meta.Name, meta);
    }

    public bool DropCollection(string name)
    {
        var meta = GetCollection(name);
        if (meta is null) return false;
        foreach (var idx in meta.Indexes) new BTree(Storage, idx.Root).Drop();
        new BTree(Storage, meta.PrimaryRoot).Drop();
        Catalog.Delete(CatalogKey(name));
        Cache(name, null);
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
