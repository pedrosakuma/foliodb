using System.Buffers.Binary;
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

    private static void WriteDocument(ByteBuffer buf, Document doc, int depth)
    {
        if (depth > MaxDepth) throw new FolioException("Document nesting is too deep.");
        int start = buf.Length;
        buf.WriteInt32(0);
        Span<byte> name = stackalloc byte[MaxNameBytes];
        foreach (var (key, value) in doc)
        {
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
        switch (type)
        {
            case DocType.Null: consumed = 0; return new(type, default);
            case DocType.Boolean: consumed = 1; return new(type, data[..1]);
            case DocType.Int32: consumed = 4; return new(type, data[..4]);
            case DocType.Int64:
            case DocType.Double:
            case DocType.DateTime: consumed = 8; return new(type, data[..8]);
            case DocType.ObjectId: consumed = ObjectId.Size; return new(type, data[..ObjectId.Size]);
            case DocType.Decimal: consumed = 16; return new(type, data[..16]);
            case DocType.String:
            case DocType.Binary:
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(data);
                if (n < 0) throw new CorruptDatabaseException("Negative length.");
                consumed = 4 + n;
                return new(type, data.Slice(4, n));
            }
            case DocType.Document:
            case DocType.Array:
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(data);
                if (n < 5) throw new CorruptDatabaseException("Invalid nested length.");
                consumed = n;
                return new(type, data[..n]);
            }
            default: throw new CorruptDatabaseException($"Unknown value type 0x{(byte)type:X2}.");
        }
    }
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

    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    /// <summary>Resolves a dotted path with the same rules as <see cref="Document.TryGetPath"/> (first field occurrence, numeric array indexes).</summary>
    public bool TryGetPath(string path, out RawValue value)
    {
        value = new RawValue(DocType.Document, Data);
        Span<byte> name = stackalloc byte[DocumentSerializer.MaxNameBytes];
        foreach (var segment in path.Split('.'))
        {
            if (value.Type == DocType.Document)
            {
                int n;
                try
                {
                    if (s_strictUtf8.GetByteCount(segment) > name.Length) return false;
                    n = s_strictUtf8.GetBytes(segment, name);
                }
                catch (ArgumentException) { return false; }
                if (!value.AsDocument.TryGetField(name[..n], out value)) return false;
            }
            else if (value.Type == DocType.Array && int.TryParse(segment, out int idx) && idx >= 0)
            {
                bool found = false;
                foreach (var item in value.AsArray)
                {
                    if (idx-- == 0)
                    {
                        value = item;
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }
            else return false;
        }
        return true;
    }

    public Document ToDocument()
    {
        var doc = new Document();
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
        private Field _current;

        public Enumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 4;
            _current = default;
        }

        public readonly Field Current => _current;

        public bool MoveNext()
        {
            if (_pos >= _data.Length - 1) return false;
            var type = (DocType)_data[_pos];
            if (type == 0) return false;
            int nameLen = _data[_pos + 1];
            var name = _data.Slice(_pos + 2, nameLen);
            int vpos = _pos + 2 + nameLen;
            var value = RawValue.Read(type, _data[vpos..], out int consumed);
            _pos = vpos + consumed;
            _current = new Field(name, value);
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

    public DocArray ToArray()
    {
        var arr = new DocArray();
        foreach (var v in this) arr.Add(v.ToDocValue());
        return arr;
    }

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private RawValue _current;

        public Enumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 4;
            _current = default;
        }

        public readonly RawValue Current => _current;

        public bool MoveNext()
        {
            if (_pos >= _data.Length - 1) return false;
            var type = (DocType)_data[_pos];
            if (type == 0) return false;
            _current = RawValue.Read(type, _data[(_pos + 1)..], out int consumed);
            _pos += 1 + consumed;
            return true;
        }
    }
}
