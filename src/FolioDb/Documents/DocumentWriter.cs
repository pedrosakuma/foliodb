using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;

namespace FolioDb;

/// <summary>
/// Writes a document straight into the stored binary format, without building a <see cref="Document"/>. Used by
/// source-generated <see cref="IFolioDocument{TSelf}.WriteTo"/> implementations; not intended for direct use.
/// Each field name must be written at most once per document.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class DocumentWriter
{
    [ThreadStatic] private static DocumentWriter? t_cached;

    private readonly ByteBuffer _buf = new(512);
    private readonly int[] _starts = new int[DocumentSerializer.MaxDepth + 2];
    private readonly bool[] _isArray = new bool[DocumentSerializer.MaxDepth + 2];
    private int _level;
    private bool _hasRootId;
    private DocValue _rootId;
    private int _pendingRootIdStart;
    private bool _inUse;

    private DocumentWriter() { }

    /// <summary>
    /// Serializes <paramref name="entity"/> and returns its stored bytes and <c>_id</c>. When the entity writes no
    /// top-level <c>_id</c>, an <see cref="ObjectId"/> is generated and stored as the first field, as
    /// <see cref="Collection.Insert(Document)"/> does.
    /// </summary>
    internal static byte[] Serialize<T>(T entity, out DocValue id) where T : IFolioDocument<T>
    {
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        // A getter that serializes another entity on this thread gets its own writer.
        var w = t_cached is { _inUse: false } cached ? cached : new DocumentWriter();
        w._inUse = true;
        try
        {
            w._buf.Clear();
            w._buf.WriteInt32(0);
            w._starts[0] = 0;
            w._isArray[0] = false;
            w._level = 0;
            w._hasRootId = false;
            w._rootId = default;
            w._pendingRootIdStart = -1;

            T.WriteTo(entity, w);
            if (w._level != 0) throw new InvalidOperationException("WriteTo left a nested document or array open.");
            w._buf.WriteByte(0);
            w._buf.PatchInt32(0, w._buf.Length);

            byte[] bytes;
            if (w._hasRootId)
            {
                id = w._rootId;
                bytes = w._buf.ToArray();
            }
            else
            {
                id = ObjectId.NewObjectId();
                bytes = PrependObjectId(w._buf.WrittenSpan, id.AsObjectId);
            }

            return bytes;
        }
        finally
        {
            w._inUse = false;
            // Keep one writer per thread; return the pooled buffer of any other (nested or oversized) one.
            if (w._buf.Length <= 1 << 20 && (t_cached is null || ReferenceEquals(t_cached, w))) t_cached = w;
            else
            {
                if (ReferenceEquals(t_cached, w)) t_cached = null;
                w._buf.Dispose();
            }
        }
    }

    private static byte[] PrependObjectId(ReadOnlySpan<byte> doc, ObjectId id)
    {
        const int Element = 2 + 3 + ObjectId.Size;
        var result = new byte[doc.Length + Element];
        BinaryPrimitives.WriteInt32LittleEndian(result, result.Length);
        result[4] = (byte)DocType.ObjectId;
        result[5] = 3;
        "_id"u8.CopyTo(result.AsSpan(6));
        id.WriteTo(result.AsSpan(9, ObjectId.Size));
        doc[4..].CopyTo(result.AsSpan(4 + Element));
        return result;
    }

    // ------------------------------------------------------------------ fields

    public void Write(ReadOnlySpan<byte> utf8Name, DocValue value)
    {
        WriteHeader(utf8Name, value.Type);
        if (_level == 0 && utf8Name.SequenceEqual("_id"u8)) RecordRootId(value);
        DocumentSerializer.WriteValue(_buf, value, _level);
    }

    public void Write(string name, DocValue value)
    {
        Span<byte> utf8 = stackalloc byte[DocumentSerializer.MaxNameBytes];
        Write(utf8[..EncodeName(name, utf8)], value);
    }

    public void WriteItem(DocValue value)
    {
        EnsureContainer(array: true);
        _buf.WriteByte((byte)value.Type);
        DocumentSerializer.WriteValue(_buf, value, _level);
    }

    /// <summary>Writes the fields of <paramref name="document"/> into the current document, in order.</summary>
    public void WriteFields(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var (name, value) in document) Write(name, value);
    }

    // ------------------------------------------------------------------ nesting

    public void BeginDocument(ReadOnlySpan<byte> utf8Name) => Begin(utf8Name, array: false);
    public void BeginDocument(string name) => Begin(name, array: false);
    public void BeginDocumentItem() => BeginItem(array: false);
    public void BeginArray(ReadOnlySpan<byte> utf8Name) => Begin(utf8Name, array: true);
    public void BeginArray(string name) => Begin(name, array: true);
    public void BeginArrayItem() => BeginItem(array: true);

    /// <summary>Closes the innermost document or array opened by a <c>Begin</c> call.</summary>
    public void End()
    {
        if (_level == 0) throw new InvalidOperationException("No nested document or array is open.");
        _buf.WriteByte(0);
        int start = _starts[_level];
        _buf.PatchInt32(start, _buf.Length - start);
        if (_level == 1 && _pendingRootIdStart == start)
        {
            // The id is a nested document or array: decode the bytes just written.
            var type = _isArray[1] ? DocType.Array : DocType.Document;
            _rootId = new RawValue(type, _buf.WrittenSpan[start..]).ToDocValue();
            _pendingRootIdStart = -1;
        }
        _level--;
    }

    private void Begin(ReadOnlySpan<byte> utf8Name, bool array)
    {
        WriteHeader(utf8Name, array ? DocType.Array : DocType.Document);
        bool rootId = _level == 0 && utf8Name.SequenceEqual("_id"u8);
        if (rootId) RecordRootId(DocValue.Null);
        Open(array);
        if (rootId) _pendingRootIdStart = _starts[_level];
    }

    private void Begin(string name, bool array)
    {
        Span<byte> utf8 = stackalloc byte[DocumentSerializer.MaxNameBytes];
        Begin(utf8[..EncodeName(name, utf8)], array);
    }

    private void BeginItem(bool array)
    {
        EnsureContainer(array: true);
        _buf.WriteByte((byte)(array ? DocType.Array : DocType.Document));
        Open(array);
    }

    private void Open(bool array)
    {
        if (_level + 1 > DocumentSerializer.MaxDepth) throw new FolioException("Document nesting is too deep.");
        _level++;
        _starts[_level] = _buf.Length;
        _isArray[_level] = array;
        _buf.WriteInt32(0);
    }

    // ------------------------------------------------------------------ helpers

    private void WriteHeader(ReadOnlySpan<byte> utf8Name, DocType type)
    {
        EnsureContainer(array: false);
        if (utf8Name.Length > DocumentSerializer.MaxNameBytes)
        {
            string name = Encoding.UTF8.GetString(utf8Name);
            throw new FolioException($"Field name '{name[..Math.Min(32, name.Length)]}…' exceeds {DocumentSerializer.MaxNameBytes} UTF-8 bytes.");
        }
        _buf.WriteByte((byte)type);
        _buf.WriteByte((byte)utf8Name.Length);
        _buf.Write(utf8Name);
    }

    private static int EncodeName(string name, Span<byte> utf8)
    {
        ArgumentNullException.ThrowIfNull(name);
        try { return Encoding.UTF8.GetBytes(name, utf8); }
        catch (ArgumentException) { throw new FolioException($"Field name '{name[..Math.Min(32, name.Length)]}…' exceeds {DocumentSerializer.MaxNameBytes} UTF-8 bytes."); }
    }

    private void EnsureContainer(bool array)
    {
        if (_isArray[_level] != array)
            throw new InvalidOperationException(array ? "Array items can only be written inside an array." : "Named fields cannot be written inside an array.");
    }

    private void RecordRootId(DocValue value)
    {
        if (_hasRootId) throw new FolioException("_id is written more than once.");
        _hasRootId = true;
        _rootId = value;
    }
}
