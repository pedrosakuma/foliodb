using System.Collections;

namespace FolioDb;

/// <summary>An ordered, schemaless document (field order is preserved).</summary>
public sealed class Document : IEnumerable<KeyValuePair<string, DocValue>>
{
    private readonly List<KeyValuePair<string, DocValue>> _fields;

    public Document() => _fields = new List<KeyValuePair<string, DocValue>>();
    public Document(int capacity) => _fields = new List<KeyValuePair<string, DocValue>>(capacity);

    public int Count => _fields.Count;

    public DocValue this[string name]
    {
        get => TryGetValue(name, out var v) ? v : DocValue.Null;
        set => Set(name, value);
    }

    public IEnumerable<string> Keys
    {
        get { foreach (var f in _fields) yield return f.Key; }
    }

    /// <summary>Collection-initializer support; replaces an existing field with the same name.</summary>
    public void Add(string name, DocValue value) => Set(name, value);

    public Document Set(string name, DocValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        int i = IndexOf(name);
        if (i >= 0) _fields[i] = new(name, value);
        else _fields.Add(new(name, value));
        return this;
    }

    internal void InsertFirst(string name, DocValue value)
    {
        int i = IndexOf(name);
        if (i >= 0) _fields.RemoveAt(i);
        _fields.Insert(0, new(name, value));
    }

    /// <summary>Appends without checking for duplicates (deserializer fast path).</summary>
    internal void AddUnchecked(string name, DocValue value) => _fields.Add(new(name, value));

    public bool Remove(string name)
    {
        int i = IndexOf(name);
        if (i < 0) return false;
        _fields.RemoveAt(i);
        return true;
    }

    public bool ContainsKey(string name) => IndexOf(name) >= 0;

    public bool TryGetValue(string name, out DocValue value)
    {
        int i = IndexOf(name);
        if (i >= 0)
        {
            value = _fields[i].Value;
            return true;
        }
        value = default;
        return false;
    }

    private int IndexOf(string name)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_fields);
        for (int i = 0; i < span.Length; i++)
            if (string.Equals(span[i].Key, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Resolves a dotted path (e.g. <c>address.city</c> or <c>tags.0</c>).</summary>
    public bool TryGetPath(string path, out DocValue value)
    {
        value = default;
        DocValue current = DocValue.FromDocument(this);
        foreach (var segment in path.Split('.'))
        {
            if (current.Type == DocType.Document)
            {
                if (!current.AsDocument.TryGetValue(segment, out current)) return false;
            }
            else if (current.Type == DocType.Array && int.TryParse(segment, out int idx))
            {
                var arr = current.AsArray;
                if ((uint)idx >= (uint)arr.Count) return false;
                current = arr[idx];
            }
            else return false;
        }
        value = current;
        return true;
    }

    /// <summary>Sets a dotted path, creating intermediate documents as needed.</summary>
    public void SetPath(string path, DocValue value)
    {
        var segments = path.Split('.');
        var parent = GetOrCreateParent(segments, create: true)!;
        SetChild(parent, segments[^1], value);
    }

    public bool RemovePath(string path)
    {
        var segments = path.Split('.');
        var parent = GetOrCreateParent(segments, create: false);
        if (parent is Document d) return d.Remove(segments[^1]);
        if (parent is DocArray a && int.TryParse(segments[^1], out int idx) && (uint)idx < (uint)a.Count)
        {
            a[idx] = DocValue.Null; // Mongo semantics: $unset on an array element sets it to null
            return true;
        }
        return false;
    }

    private object? GetOrCreateParent(string[] segments, bool create)
    {
        object current = this;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string seg = segments[i];
            DocValue next;
            if (current is Document d)
            {
                if (!d.TryGetValue(seg, out next) || (next.Type != DocType.Document && next.Type != DocType.Array))
                {
                    if (!create) return null;
                    if (d.TryGetValue(seg, out var existing) && !existing.IsNull)
                        throw new FolioException($"Cannot create field '{segments[i + 1]}' inside non-document field '{seg}'.");
                    next = DocValue.FromDocument(new Document());
                    d.Set(seg, next);
                }
            }
            else
            {
                var a = (DocArray)current;
                if (!int.TryParse(seg, out int idx) || idx < 0) throw new FolioException($"Invalid array index '{seg}'.");
                if (idx >= a.Count || (a[idx].Type != DocType.Document && a[idx].Type != DocType.Array))
                {
                    if (!create) return null;
                    while (a.Count <= idx) a.Add(DocValue.Null);
                    a[idx] = DocValue.FromDocument(new Document());
                }
                next = a[idx];
            }
            current = next.Type == DocType.Document ? next.AsDocument : next.AsArray;
        }
        return current;
    }

    private static void SetChild(object parent, string name, DocValue value)
    {
        if (parent is Document d) { d.Set(name, value); return; }
        var a = (DocArray)parent;
        if (!int.TryParse(name, out int idx) || idx < 0) throw new FolioException($"Invalid array index '{name}'.");
        while (a.Count <= idx) a.Add(DocValue.Null);
        a[idx] = value;
    }

    public Document Clone()
    {
        var c = new Document(_fields.Count);
        foreach (var f in _fields) c._fields.Add(new(f.Key, f.Value.DeepClone()));
        return c;
    }

    public List<KeyValuePair<string, DocValue>>.Enumerator GetEnumerator() => _fields.GetEnumerator();
    IEnumerator<KeyValuePair<string, DocValue>> IEnumerable<KeyValuePair<string, DocValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static Document Parse(string json) => DocJson.ParseDocument(json);
    public string ToJson(bool indented = false) => DocJson.Write(this, indented);
    public override string ToString() => ToJson();

    public byte[] ToBytes() => DocumentSerializer.Serialize(this);
    public static Document FromBytes(ReadOnlySpan<byte> bytes) => DocumentSerializer.Deserialize(bytes);
}

/// <summary>An ordered list of document values.</summary>
public sealed class DocArray : IList<DocValue>
{
    private readonly List<DocValue> _items;

    public DocArray() => _items = new List<DocValue>();
    public DocArray(int capacity) => _items = new List<DocValue>(capacity);
    public DocArray(IEnumerable<DocValue> items) => _items = new List<DocValue>(items);

    public DocValue this[int index] { get => _items[index]; set => _items[index] = value; }
    public int Count => _items.Count;
    public bool IsReadOnly => false;
    public void Add(DocValue item) => _items.Add(item);
    public void Clear() => _items.Clear();
    public bool Contains(DocValue item) => _items.Contains(item);
    public void CopyTo(DocValue[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public int IndexOf(DocValue item) => _items.IndexOf(item);
    public void Insert(int index, DocValue item) => _items.Insert(index, item);
    public bool Remove(DocValue item) => _items.Remove(item);
    public void RemoveAt(int index) => _items.RemoveAt(index);
    public int RemoveAll(Predicate<DocValue> match) => _items.RemoveAll(match);
    public List<DocValue>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<DocValue> IEnumerable<DocValue>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public DocArray Clone()
    {
        var c = new DocArray(_items.Count);
        foreach (var v in _items) c._items.Add(v.DeepClone());
        return c;
    }

    public override string ToString() => DocJson.WriteValue(DocValue.FromArray(this));
}
