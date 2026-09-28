using System.Buffers.Binary;
using System.Text;

namespace FolioDb;

/// <summary>
/// Order-preserving, prefix-free binary encoding of values. <c>memcmp</c> order of encoded keys equals the
/// logical value order, so the same encoding powers B+Tree keys, index lookups, sort and filter comparisons.
/// Type order (Mongo-like): null &lt; numbers &lt; string &lt; document &lt; array &lt; binary &lt; objectId &lt; bool &lt; datetime.
/// Numbers of different CLR types compare by value (Int32 5 == Int64 5 == Double 5.0).
/// </summary>
internal static class KeyEncoder
{
    public const byte TagNull = 0x05;
    public const byte TagNumber = 0x10;
    public const byte TagString = 0x20;
    public const byte TagDocument = 0x30;
    public const byte TagArray = 0x40;
    public const byte TagBinary = 0x50;
    public const byte TagObjectId = 0x60;
    public const byte TagBoolean = 0x70;
    public const byte TagDateTime = 0x80;

    private const byte ItemMarker = 0x01;
    private const byte End = 0x00;

    [ThreadStatic] private static ByteBuffer? t_a;
    [ThreadStatic] private static ByteBuffer? t_b;

    public static byte TypeTag(DocType t) => t switch
    {
        DocType.Null => TagNull,
        DocType.Int32 or DocType.Int64 or DocType.Double => TagNumber,
        DocType.String => TagString,
        DocType.Document => TagDocument,
        DocType.Array => TagArray,
        DocType.Binary => TagBinary,
        DocType.ObjectId => TagObjectId,
        DocType.Boolean => TagBoolean,
        DocType.DateTime => TagDateTime,
        _ => throw new FolioException($"Unsupported type {t}."),
    };

    public static byte[] Encode(DocValue v)
    {
        var buf = t_a ??= new ByteBuffer();
        buf.Clear();
        Encode(buf, v);
        return buf.ToArray();
    }

    public static int Compare(DocValue a, DocValue b)
    {
        var ba = t_a ??= new ByteBuffer();
        var bb = t_b ??= new ByteBuffer();
        ba.Clear();
        bb.Clear();
        Encode(ba, a);
        Encode(bb, b);
        return ba.WrittenSpan.SequenceCompareTo(bb.WrittenSpan);
    }

    public static void Encode(ByteBuffer buf, DocValue v)
    {
        switch (v.Type)
        {
            case DocType.Null: buf.WriteByte(TagNull); break;
            case DocType.Int32:
            case DocType.Int64: WriteInteger(buf, v.AsInt64); break;
            case DocType.Double: WriteDouble(buf, v.AsDouble); break;
            case DocType.String: WriteString(buf, v.AsString); break;
            case DocType.Binary: buf.WriteByte(TagBinary); WriteEscaped(buf, v.AsBinary); break;
            case DocType.ObjectId:
                buf.WriteByte(TagObjectId);
                v.AsObjectId.WriteTo(buf.GetSpan(ObjectId.Size));
                buf.Advance(ObjectId.Size);
                break;
            case DocType.Boolean: buf.WriteByte(TagBoolean); buf.WriteByte(v.AsBoolean ? (byte)1 : (byte)0); break;
            case DocType.DateTime: buf.WriteByte(TagDateTime); buf.WriteUInt64BigEndian(OrderedInt64(v.AsUnixMilliseconds)); break;
            case DocType.Document:
                buf.WriteByte(TagDocument);
                foreach (var (name, value) in v.AsDocument)
                {
                    buf.WriteByte(ItemMarker);
                    WriteEscapedString(buf, name);
                    Encode(buf, value);
                }
                buf.WriteByte(End);
                break;
            case DocType.Array:
                buf.WriteByte(TagArray);
                foreach (var value in v.AsArray)
                {
                    buf.WriteByte(ItemMarker);
                    Encode(buf, value);
                }
                buf.WriteByte(End);
                break;
            default: throw new FolioException($"Unsupported type {v.Type}.");
        }
    }

    /// <summary>Encodes a serialized value directly from its raw bytes (no materialization).</summary>
    public static void Encode(ByteBuffer buf, RawValue v)
    {
        switch (v.Type)
        {
            case DocType.Null: buf.WriteByte(TagNull); break;
            case DocType.Int32: WriteInteger(buf, v.Int32); break;
            case DocType.Int64: WriteInteger(buf, v.Int64); break;
            case DocType.Double: WriteDouble(buf, v.Double); break;
            case DocType.String: buf.WriteByte(TagString); WriteEscaped(buf, v.Data); break;
            case DocType.Binary: buf.WriteByte(TagBinary); WriteEscaped(buf, v.Data); break;
            case DocType.ObjectId: buf.WriteByte(TagObjectId); buf.Write(v.Data); break;
            case DocType.Boolean: buf.WriteByte(TagBoolean); buf.WriteByte(v.Boolean ? (byte)1 : (byte)0); break;
            case DocType.DateTime: buf.WriteByte(TagDateTime); buf.WriteUInt64BigEndian(OrderedInt64(v.Int64)); break;
            case DocType.Document:
                buf.WriteByte(TagDocument);
                foreach (var f in v.AsDocument)
                {
                    buf.WriteByte(ItemMarker);
                    WriteEscaped(buf, f.Name);
                    Encode(buf, f.Value);
                }
                buf.WriteByte(End);
                break;
            case DocType.Array:
                buf.WriteByte(TagArray);
                foreach (var item in v.AsArray)
                {
                    buf.WriteByte(ItemMarker);
                    Encode(buf, item);
                }
                buf.WriteByte(End);
                break;
            default: throw new CorruptDatabaseException($"Unsupported type {v.Type}.");
        }
    }

    private static ulong OrderedInt64(long v) => (ulong)v ^ 0x8000_0000_0000_0000UL;

    private static ulong OrderedDouble(double d)
    {
        if (d == 0) d = 0; // normalize -0.0
        if (double.IsNaN(d)) d = double.NaN; // canonical NaN
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(d);
        return (bits & 0x8000_0000_0000_0000UL) != 0 ? ~bits : bits | 0x8000_0000_0000_0000UL;
    }

    // Numbers: tag, ordered double (8 bytes), ordered correction (8 bytes). The correction keeps exact
    // ordering/equality for Int64 values that are not exactly representable as double (|v| > 2^53).
    private static void WriteDouble(ByteBuffer buf, double d)
    {
        buf.WriteByte(TagNumber);
        buf.WriteUInt64BigEndian(OrderedDouble(double.IsNaN(d) ? double.NegativeInfinity : d) - (double.IsNaN(d) ? 1UL : 0UL));
        buf.WriteUInt64BigEndian(OrderedInt64(0));
    }

    private static void WriteInteger(ByteBuffer buf, long v)
    {
        double d = v;
        Int128 delta = (Int128)v - (Int128)d;
        buf.WriteByte(TagNumber);
        buf.WriteUInt64BigEndian(OrderedDouble(d));
        buf.WriteUInt64BigEndian(OrderedInt64((long)delta));
    }

    private static void WriteString(ByteBuffer buf, string s)
    {
        buf.WriteByte(TagString);
        WriteEscapedString(buf, s);
    }

    private static void WriteEscapedString(ByteBuffer buf, string s)
    {
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        if (max <= 512)
        {
            Span<byte> tmp = stackalloc byte[512];
            int n = Encoding.UTF8.GetBytes(s, tmp);
            WriteEscaped(buf, tmp[..n]);
        }
        else WriteEscaped(buf, Encoding.UTF8.GetBytes(s));
    }

    // 0x00 is escaped as 0x00 0xFF; terminator is 0x00 0x01 (so shorter strings sort first).
    private static void WriteEscaped(ByteBuffer buf, ReadOnlySpan<byte> data)
    {
        while (true)
        {
            int z = data.IndexOf((byte)0);
            if (z < 0)
            {
                buf.Write(data);
                break;
            }
            buf.Write(data[..(z + 1)]);
            buf.WriteByte(0xFF);
            data = data[(z + 1)..];
        }
        buf.WriteByte(0x00);
        buf.WriteByte(0x01);
    }

    /// <summary>Length of the encoded value at the start of <paramref name="key"/> (encodings are prefix-free).</summary>
    public static int EncodedLength(ReadOnlySpan<byte> key)
    {
        byte tag = key[0];
        switch (tag)
        {
            case TagNull: return 1;
            case TagNumber: return 17;
            case TagObjectId: return 1 + ObjectId.Size;
            case TagBoolean: return 2;
            case TagDateTime: return 9;
            case TagString:
            case TagBinary: return 1 + EscapedLength(key[1..]);
            case TagDocument:
            {
                int pos = 1;
                while (key[pos] == ItemMarker)
                {
                    pos++;
                    pos += EscapedLength(key[pos..]);
                    pos += EncodedLength(key[pos..]);
                }
                return pos + 1;
            }
            case TagArray:
            {
                int pos = 1;
                while (key[pos] == ItemMarker)
                {
                    pos++;
                    pos += EncodedLength(key[pos..]);
                }
                return pos + 1;
            }
            default: throw new CorruptDatabaseException($"Invalid key tag 0x{tag:X2}.");
        }
    }

    private static int EscapedLength(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        while (true)
        {
            int z = data[pos..].IndexOf((byte)0);
            if (z < 0) throw new CorruptDatabaseException("Unterminated key string.");
            pos += z;
            if (data[pos + 1] == 0x01) return pos + 2;
            pos += 2; // escaped 0x00 0xFF
        }
    }

    /// <summary>Decodes an encoded key back to a value (used for primary keys recovered from index entries).</summary>
    public static DocValue Decode(ReadOnlySpan<byte> key, out int consumed)
    {
        byte tag = key[0];
        switch (tag)
        {
            case TagNull: consumed = 1; return DocValue.Null;
            case TagNumber:
            {
                consumed = 17;
                ulong ob = BinaryPrimitives.ReadUInt64BigEndian(key[1..]);
                ulong bits = (ob & 0x8000_0000_0000_0000UL) != 0 ? ob & 0x7FFF_FFFF_FFFF_FFFFUL : ~ob;
                double d = BitConverter.Int64BitsToDouble((long)bits);
                long delta = (long)(BinaryPrimitives.ReadUInt64BigEndian(key[9..]) ^ 0x8000_0000_0000_0000UL);
                if (delta == 0 && d != Math.Floor(d)) return DocValue.FromDouble(d);
                if (delta == 0 && (d < -9.2233720368547758E18 || d >= 9.2233720368547758E18)) return DocValue.FromDouble(d);
                if (delta == 0 && Math.Abs(d) > 9007199254740992d) return DocValue.FromDouble(d);
                long l = (long)((Int128)d + delta);
                return l is >= int.MinValue and <= int.MaxValue ? DocValue.FromInt32((int)l) : DocValue.FromInt64(l);
            }
            case TagString:
            {
                consumed = 1 + EscapedLength(key[1..]);
                return DocValue.FromString(Encoding.UTF8.GetString(Unescape(key[1..consumed])));
            }
            case TagBinary:
            {
                consumed = 1 + EscapedLength(key[1..]);
                return DocValue.FromBinary(Unescape(key[1..consumed]));
            }
            case TagObjectId: consumed = 1 + ObjectId.Size; return DocValue.FromObjectId(new ObjectId(key[1..]));
            case TagBoolean: consumed = 2; return DocValue.FromBoolean(key[1] != 0);
            case TagDateTime:
                consumed = 9;
                return DocValue.FromUnixMilliseconds((long)(BinaryPrimitives.ReadUInt64BigEndian(key[1..]) ^ 0x8000_0000_0000_0000UL));
            case TagDocument:
            {
                var doc = new Document();
                int pos = 1;
                while (key[pos] == ItemMarker)
                {
                    pos++;
                    int nl = EscapedLength(key[pos..]);
                    string name = Encoding.UTF8.GetString(Unescape(key.Slice(pos, nl)));
                    pos += nl;
                    doc.AddUnchecked(name, Decode(key[pos..], out int c));
                    pos += c;
                }
                consumed = pos + 1;
                return doc;
            }
            case TagArray:
            {
                var arr = new DocArray();
                int pos = 1;
                while (key[pos] == ItemMarker)
                {
                    pos++;
                    arr.Add(Decode(key[pos..], out int c));
                    pos += c;
                }
                consumed = pos + 1;
                return arr;
            }
            default: throw new CorruptDatabaseException($"Invalid key tag 0x{tag:X2}.");
        }
    }

    private static byte[] Unescape(ReadOnlySpan<byte> escapedWithTerminator)
    {
        var body = escapedWithTerminator[..^2];
        var result = new List<byte>(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            result.Add(body[i]);
            if (body[i] == 0) i++; // skip 0xFF escape
        }
        return result.ToArray();
    }
}
