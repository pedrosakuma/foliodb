using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace FolioDb;

/// <summary>
/// Binary document format (little-endian, length-prefixed, BSON-like):
/// <code>
/// document := int32 totalLength, element*, 0x00
/// element  := byte type, byte nameLength, utf8 name, value
/// array    := int32 totalLength, (byte type, value)*, 0x00
/// string   := int32 byteLength, utf8 bytes
/// binary   := int32 byteLength, bytes
/// </code>
/// Fields can be located without materializing the document (see <see cref="RawDocument"/>).
/// </summary>
internal static class DocumentSerializer
{
    public const int MaxNameBytes = 255;
    public const int MaxDepth = 100;

    [ThreadStatic] private static ByteBuffer? t_buffer;

    public static byte[] Serialize(Document doc)
    {
        var buf = t_buffer ??= new ByteBuffer(1024);
        buf.Clear();
        WriteDocument(buf, doc, 0);
        var result = buf.ToArray();
        if (buf.Length > 1 << 20) { buf.Dispose(); t_buffer = null; }
        return result;
    }

    public static void Serialize(Document doc, ByteBuffer buf) => WriteDocument(buf, doc, 0);

    /// <summary>
    /// Serializes <paramref name="doc"/> with <paramref name="id"/> written first as <c>_id</c>, ignoring any
    /// <c>_id</c> the document itself carries. Equivalent to calling <see cref="Document.InsertFirst"/> on a copy.
    /// </summary>
    public static byte[] SerializeWithId(Document doc, DocValue id)
    {
        var buf = t_buffer ??= new ByteBuffer(1024);
        buf.Clear();
        WriteDocument(buf, doc, 0, id);
        var result = buf.ToArray();
        if (buf.Length > 1 << 20) { buf.Dispose(); t_buffer = null; }
        return result;
    }

    private static void WriteDocument(ByteBuffer buf, Document doc, int depth, DocValue? idFirst = null)
    {
        if (depth > MaxDepth) throw new FolioException("Document nesting is too deep.");
        int start = buf.Length;
        buf.WriteInt32(0);
        Span<byte> name = stackalloc byte[MaxNameBytes];
        if (idFirst is { } id)
        {
            buf.WriteByte((byte)id.Type);
            buf.WriteByte(3);
            buf.Write("_id"u8);
            WriteValue(buf, id, depth);
        }
        foreach (var (key, value) in doc)
        {
            if (idFirst is not null && key == "_id") continue;
            int n;
            try { n = Encoding.UTF8.GetBytes(key, name); }
            catch (ArgumentException) { throw new FolioException($"Field name '{key[..Math.Min(32, key.Length)]}…' exceeds {MaxNameBytes} UTF-8 bytes."); }
            buf.WriteByte((byte)value.Type);
            buf.WriteByte((byte)n);
            buf.Write(name[..n]);
            WriteValue(buf, value, depth);
        }
        buf.WriteByte(0);
        buf.PatchInt32(start, buf.Length - start);
    }

    private static void WriteArray(ByteBuffer buf, DocArray arr, int depth)
    {
        if (depth > MaxDepth) throw new FolioException("Document nesting is too deep.");
        int start = buf.Length;
        buf.WriteInt32(0);
        foreach (var value in arr)
        {
            buf.WriteByte((byte)value.Type);
            WriteValue(buf, value, depth);
        }
        buf.WriteByte(0);
        buf.PatchInt32(start, buf.Length - start);
    }

    internal static void WriteValue(ByteBuffer buf, DocValue value, int depth)
    {
        switch (value.Type)
        {
            case DocType.Null: break;
            case DocType.Int32: buf.WriteInt32((int)value.RawBits); break;
            case DocType.Int64:
            case DocType.Double:
            case DocType.DateTime: buf.WriteInt64(value.RawBits); break;
            case DocType.Boolean: buf.WriteByte((byte)(value.RawBits != 0 ? 1 : 0)); break;
            case DocType.Decimal:
            {
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(value.AsDecimal, bits);
                foreach (int b in bits) buf.WriteInt32(b);
                break;
            }
            case DocType.String:
            {
                string s = value.AsString;
                int max = Encoding.UTF8.GetMaxByteCount(s.Length);
                var span = buf.GetSpan(4 + max);
                int n = Encoding.UTF8.GetBytes(s, span[4..]);
                BinaryPrimitives.WriteInt32LittleEndian(span, n);
                buf.Advance(4 + n);
                break;
            }
            case DocType.Binary:
            {
                var b = value.AsBinary;
                buf.WriteInt32(b.Length);
                buf.Write(b);
                break;
            }
            case DocType.ObjectId:
                value.AsObjectId.WriteTo(buf.GetSpan(ObjectId.Size));
                buf.Advance(ObjectId.Size);
                break;
            case DocType.Document: WriteDocument(buf, value.AsDocument, depth + 1); break;
            case DocType.Array: WriteArray(buf, value.AsArray, depth + 1); break;
            default: throw new FolioException($"Unsupported value type {value.Type}.");
        }
    }

    public static Document Deserialize(ReadOnlySpan<byte> data) => new RawDocument(data).ToDocument();
}

/// <summary>Zero-copy view over a serialized value.</summary>
internal readonly ref struct RawValue
{
    public readonly DocType Type;
    /// <summary>Encoded payload: fixed-size scalars, utf8/binary bytes (no length prefix), or the full nested document/array.</summary>
    public readonly ReadOnlySpan<byte> Data;

    public RawValue(DocType type, ReadOnlySpan<byte> data)
    {
        Type = type;
        Data = data;
    }

    public int Int32 => BinaryPrimitives.ReadInt32LittleEndian(Data);
    public long Int64 => BinaryPrimitives.ReadInt64LittleEndian(Data);
    public double Double => BitConverter.Int64BitsToDouble(Int64);
    public bool Boolean => Data[0] != 0;

    /// <summary>Decimal stored as its four little-endian <see cref="decimal.GetBits(decimal)"/> words.</summary>
    public decimal Decimal
    {
        get
        {
            Span<int> bits = stackalloc int[4];
            for (int i = 0; i < 4; i++) bits[i] = BinaryPrimitives.ReadInt32LittleEndian(Data[(i * 4)..]);
            try { return new decimal(bits); }
            catch (ArgumentException) { throw new CorruptDatabaseException("Invalid decimal value."); }
        }
    }
    public RawDocument AsDocument => new(Data);
    public RawArray AsArray => new(Data);

    public DocValue ToDocValue() => Type switch
    {
        DocType.Null => DocValue.Null,
        DocType.Int32 => DocValue.FromInt32(Int32),
        DocType.Int64 => DocValue.FromInt64(Int64),
        DocType.Double => DocValue.FromDouble(Double),
        DocType.Decimal => DocValue.FromDecimal(Decimal),
        DocType.DateTime => DocValue.FromUnixMilliseconds(Int64),
        DocType.Boolean => DocValue.FromBoolean(Boolean),
        DocType.String => DocValue.FromString(Encoding.UTF8.GetString(Data)),
        DocType.Binary => DocValue.FromBinary(Data.ToArray()),
        DocType.ObjectId => DocValue.FromObjectId(new ObjectId(Data)),
        DocType.Document => DocValue.FromDocument(AsDocument.ToDocument()),
        DocType.Array => DocValue.FromArray(AsArray.ToArray()),
        _ => throw new CorruptDatabaseException($"Unknown value type 0x{(byte)Type:X2}."),
    };

    /// <summary>Reads the value starting at <paramref name="data"/>[0] and returns the number of bytes consumed.</summary>
    public static RawValue Read(DocType type, ReadOnlySpan<byte> data, out int consumed)
    {
        consumed = Measure(type, data, 0, out int length);
        return new(type, data.Slice(consumed - length, length));
    }

    // Payload size of fixed-size types indexed by type byte; -1 = length-prefixed, -2 = unknown.
    private static ReadOnlySpan<sbyte> FixedSizes =>
    [
        -2, 8, -1, -1, -1, -1, -2, ObjectId.Size, 1, 8, 0, -2, -2, -2, -2, -2, 4, -2, 8, 16,
    ];

    /// <summary>
    /// Bytes occupied by the value at <paramref name="data"/>[<paramref name="pos"/>]; the payload is its last
    /// <paramref name="length"/> bytes. Returns plain integers so enumerators keep their state in registers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Measure(DocType type, ReadOnlySpan<byte> data, int pos, out int length)
    {
        var sizes = FixedSizes;
        int size = (byte)type < (uint)sizes.Length ? sizes[(byte)type] : -2;
        if (size >= 0)
        {
            if ((uint)pos > (uint)data.Length || size > data.Length - pos) ThrowTruncated();
            length = size;
            return size;
        }
        return MeasureVariable(type, data, pos, out length);
    }

    private static int MeasureVariable(DocType type, ReadOnlySpan<byte> data, int pos, out int length)
    {
        if (type is DocType.String or DocType.Binary or DocType.Document or DocType.Array)
        {
            if ((uint)pos > (uint)data.Length || data.Length - pos < 4) ThrowTruncated();
            int n = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
            if (type is DocType.String or DocType.Binary)
            {
                if (n < 0) throw new CorruptDatabaseException("Negative length.");
                if (n > data.Length - pos - 4) ThrowTruncated();
                length = n;
                return 4 + n;
            }
            if (n < 5) throw new CorruptDatabaseException("Invalid nested length.");
            if (n > data.Length - pos) ThrowTruncated();
            length = n;
            return n;
        }
        throw new CorruptDatabaseException($"Unknown value type 0x{(byte)type:X2}.");
    }

    // Same exception the span slicing used to raise for truncated values.
    internal static void ThrowTruncated() => throw new ArgumentOutOfRangeException("data");
}

/// <summary>Zero-copy reader over a serialized document.</summary>
internal readonly ref struct RawDocument
{
    public readonly ReadOnlySpan<byte> Data;

    public RawDocument(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) throw new CorruptDatabaseException("Document is too short.");
        int len = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (len < 5 || len > data.Length) throw new CorruptDatabaseException("Invalid document length.");
        Data = data[..len];
    }

    public Enumerator GetEnumerator() => new(Data);

    public bool TryGetField(scoped ReadOnlySpan<byte> utf8Name, out RawValue value)
    {
        foreach (var f in this)
        {
            if (f.Name.SequenceEqual(utf8Name))
            {
                value = f.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>Number of fields. Walks element headers only; no value is decoded.</summary>
    public int Count()
    {
        int n = 0;
        foreach (var _ in this) n++;
        return n;
    }

    /// <summary>
    /// <see cref="Count"/> for presizing, or 0 when the headers are malformed. The count pass validates less than
    /// materialization does (it skips decimal and nested checks), so a later error must not pre-empt an earlier one:
    /// on failure the caller falls back to growing, and materialization reports the first error in document order.
    /// </summary>
    internal int CapacityHint()
    {
        try { return Count(); }
        catch (Exception) { return 0; }
    }

    public Document ToDocument()
    {
        // Sizing the field list up front avoids the grow-and-copy steps (4, 8, 16...) and their discarded arrays.
        var doc = new Document(CapacityHint());
        foreach (var f in this) doc.AddUnchecked(Encoding.UTF8.GetString(f.Name), f.Value.ToDocValue());
        return doc;
    }

    public readonly ref struct Field
    {
        public readonly ReadOnlySpan<byte> Name;
        public readonly RawValue Value;
        public Field(ReadOnlySpan<byte> name, RawValue value) { Name = name; Value = value; }
    }

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private int _nameStart, _nameLength, _valueLength;
        private DocType _type;

        public Enumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 4;
        }

        public readonly Field Current => new(
            _data.Slice(_nameStart, _nameLength),
            new RawValue(_type, _data.Slice(_pos - _valueLength, _valueLength)));

        public bool MoveNext()
        {
            var data = _data;
            int pos = _pos;
            if (pos >= data.Length - 1) return false;
            var type = (DocType)data[pos];
            if (type == 0) return false;
            int nameLength = data[pos + 1];
            int vpos = pos + 2 + nameLength;
            // The name is checked before the value, so a truncated name wins over an invalid value.
            if (vpos > data.Length) RawValue.ThrowTruncated();
            int consumed = RawValue.Measure(type, data, vpos, out int valueLength);
            _type = type;
            _nameStart = pos + 2;
            _nameLength = nameLength;
            _valueLength = valueLength;
            _pos = vpos + consumed;
            return true;
        }
    }
}

/// <summary>Zero-copy reader over a serialized array.</summary>
internal readonly ref struct RawArray
{
    public readonly ReadOnlySpan<byte> Data;

    public RawArray(ReadOnlySpan<byte> data) => Data = data;

    public Enumerator GetEnumerator() => new(Data);

    /// <summary>Number of items. Walks element headers only; no value is decoded.</summary>
    public int Count()
    {
        int n = 0;
        foreach (var _ in this) n++;
        return n;
    }

    /// <inheritdoc cref="RawDocument.CapacityHint"/>
    internal int CapacityHint()
    {
        try { return Count(); }
        catch (Exception) { return 0; }
    }

    public DocArray ToArray()
    {
        var arr = new DocArray(CapacityHint());
        foreach (var v in this) arr.Add(v.ToDocValue());
        return arr;
    }

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private int _valueLength;
        private DocType _type;

        public Enumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 4;
        }

        public readonly RawValue Current => new(_type, _data.Slice(_pos - _valueLength, _valueLength));

        public bool MoveNext()
        {
            var data = _data;
            int pos = _pos;
            if (pos >= data.Length - 1) return false;
            var type = (DocType)data[pos];
            if (type == 0) return false;
            int consumed = RawValue.Measure(type, data, pos + 1, out int valueLength);
            _type = type;
            _valueLength = valueLength;
            _pos = pos + 1 + consumed;
            return true;
        }
    }
}
