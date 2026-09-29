using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FolioDb;

/// <summary>
/// Order-preserving, prefix-free binary encoding of values. <c>memcmp</c> order of encoded keys equals the
/// logical value order, so the same encoding powers B+Tree keys, index lookups, sort and filter comparisons.
/// Type order (Mongo-like): null &lt; numbers &lt; string &lt; document &lt; array &lt; binary &lt; objectId &lt; bool &lt; datetime.
/// Numbers of different CLR types compare by exact value (Int32 5 == Int64 5 == Double 5.0 == Decimal 5.00).
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
        DocType.Int32 or DocType.Int64 or DocType.Double or DocType.Decimal => TagNumber,
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
            case DocType.Decimal: WriteDecimal(buf, v.AsDecimal); break;
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
            case DocType.Decimal: WriteDecimal(buf, v.Decimal); break;
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

    // Numbers: tag, d = the double nearest to the exact value x (8 bytes, ordered), an ordered int64 I,
    // then a fraction marker: 0x00, or 0x01 followed by an unsigned 128-bit big-endian F.
    //  - Int32/Int64/Double: I = x - d (an integer: 0 for doubles, exact remainder for large longs), marker 0x00.
    //  - Decimal with |d| >= 2^53 (ulp >= 1): I = floor(x - d), F = frac(x - d) · 10^28.
    //  - Decimal with |d| <  2^53 (ulp <  1): T = (x - d) / ulp(d) · 10^28 (an exact integer, |T| <= 10^28/2);
    //    I = -1 if T < 0 else 0, F = T (+ 2^127 when negative).
    // For a fixed d the pair (I, F) is monotonic in x and exact (decimals have at most 28 fractional digits),
    // so equal values share one key and memcmp order is the exact numeric order across all numeric types.
    public const int NumberKeyLength = 18;
    public const int FractionalNumberKeyLength = NumberKeyLength + 16;
    private const byte NoFraction = 0x00;
    private const byte HasFraction = 0x01;
    private static readonly UInt128 Pow10_28 = Pow10(28);
    private static readonly UInt128[] s_pow10 = Enumerable.Range(0, 29).Select(Pow10).ToArray();

    private static UInt128 Pow10(int n)
    {
        UInt128 r = 1;
        for (int i = 0; i < n; i++) r *= 10;
        return r;
    }

    private static void WriteDouble(ByteBuffer buf, double d)
    {
        buf.WriteByte(TagNumber);
        buf.WriteUInt64BigEndian(OrderedDouble(double.IsNaN(d) ? double.NegativeInfinity : d) - (double.IsNaN(d) ? 1UL : 0UL));
        buf.WriteUInt64BigEndian(OrderedInt64(0));
        buf.WriteByte(NoFraction);
    }

    private static void WriteInteger(ByteBuffer buf, long v)
    {
        double d = v;
        Int128 delta = (Int128)v - (Int128)d;
        buf.WriteByte(TagNumber);
        buf.WriteUInt64BigEndian(OrderedDouble(d));
        buf.WriteUInt64BigEndian(OrderedInt64((long)delta));
        buf.WriteByte(NoFraction);
    }

    private static void WriteDecimal(ByteBuffer buf, decimal x)
    {
        if (decimal.IsInteger(x) && x >= long.MinValue && x <= long.MaxValue)
        {
            WriteInteger(buf, (long)x);
            return;
        }
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(x, bits);
        Int128 m = (Int128)(((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0]);
        if (bits[3] < 0) m = -m;
        int scale = (bits[3] >> 16) & 0xFF;
        UInt128 p10 = s_pow10[scale];

        double d = (double)x; // approximation (may be 1 ulp off); corrected below
        Int128 val, unit;
        long M;
        int e;
        for (int step = 0; ; step++)
        {
            (M, e) = SplitDouble(d);
            if (e < 0)
            {
                // T = (x - d)·2^-e·10^28 = m·2^-e·10^(28-s) - M·10^28. |T| < 2^127, so wrapping arithmetic is exact.
                UInt128 mShift = -e >= 128 ? 0 : unchecked((UInt128)m) << -e;
                val = unchecked((Int128)(mShift * s_pow10[28 - scale] - (UInt128)(Int128)M * Pow10_28));
                unit = (Int128)Pow10_28;
            }
            else
            {
                // num = (x - d)·10^s = m - M·2^e·10^s (all magnitudes < 2^100 here).
                val = m - ((Int128)M << e) * (Int128)p10;
                unit = (Int128)p10 << e;
            }
            // Nearest (ties to even)? At a power of two the spacing toward zero is half an ulp.
            bool below = val < 0;
            bool towardZero = below == M > 0;
            Int128 dist = Int128.Abs(val) * (towardZero && Math.Abs(M) == 1L << 52 ? 4 : 2);
            if (dist < unit || (dist == unit && (M & 1) == 0)) break;
            if (step == 4) throw new InvalidOperationException($"Nearest double not found for {x}.");
            d = below ? Math.BitDecrement(d) : Math.BitIncrement(d);
        }

        buf.WriteByte(TagNumber);
        buf.WriteUInt64BigEndian(OrderedDouble(d));
        UInt128 frac;
        if (e < 0)
        {
            buf.WriteUInt64BigEndian(OrderedInt64(val < 0 ? -1 : 0));
            frac = val < 0 ? (UInt128)(val + Int128.MaxValue) + 1 : (UInt128)val;
        }
        else
        {
            var (q, r) = Int128.DivRem(val, (Int128)p10);
            if (r < 0)
            {
                q--;
                r += (Int128)p10;
            }
            buf.WriteUInt64BigEndian(OrderedInt64((long)q));
            frac = (UInt128)r * s_pow10[28 - scale];
        }
        if (e < 0 ? val == 0 : frac == 0)
        {
            buf.WriteByte(NoFraction);
            return;
        }
        buf.WriteByte(HasFraction);
        buf.WriteUInt64BigEndian((ulong)(frac >> 64));
        buf.WriteUInt64BigEndian((ulong)frac);
    }

    /// <summary>d = M · 2^e exactly (M signed, |M| &lt; 2^53; normal doubles have |M| &gt;= 2^52).</summary>
    private static (long M, int E) SplitDouble(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exp == 0) exp = 1;
        else mant |= 1L << 52;
        return (bits < 0 ? -mant : mant, exp - 1075);
    }

    private static DocValue DecodeNumber(ReadOnlySpan<byte> key, out int consumed)
    {
        ulong ob = BinaryPrimitives.ReadUInt64BigEndian(key[1..]);
        ulong bits = (ob & 0x8000_0000_0000_0000UL) != 0 ? ob & 0x7FFF_FFFF_FFFF_FFFFUL : ~ob;
        double d = BitConverter.Int64BitsToDouble((long)bits);
        long delta = (long)(BinaryPrimitives.ReadUInt64BigEndian(key[9..]) ^ 0x8000_0000_0000_0000UL);
        byte marker = key[17];
        if (marker == HasFraction)
        {
            consumed = FractionalNumberKeyLength;
            var f = new BigInteger(key.Slice(18, 16), isUnsigned: true, isBigEndian: true);
            var (M, e) = SplitDouble(d);
            BigInteger scaled; // x · 10^28
            if (e < 0)
            {
                var t = delta < 0 ? f - (BigInteger.One << 127) : f;
                var num = (BigInteger)M * BigInteger.Pow(10, 28) + t; // x·10^28·2^-e
                var q = BigInteger.DivRem(num, BigInteger.One << -e, out var r);
                if (!r.IsZero) throw new CorruptDatabaseException("Invalid decimal key.");
                scaled = q;
            }
            else scaled = (((BigInteger)M << e) + delta) * BigInteger.Pow(10, 28) + f;
            return DocValue.FromDecimal(ToDecimal(scaled, 28));
        }
        consumed = NumberKeyLength;
        if (delta == 0 && (d != Math.Floor(d) || double.IsInfinity(d) || Math.Abs(d) > 9007199254740992d)) return DocValue.FromDouble(d);
        if (double.IsNaN(d) || d != Math.Floor(d)) return DocValue.FromDouble(d);
        var exact = (BigInteger)d + delta;
        if (exact >= long.MinValue && exact <= long.MaxValue)
        {
            long l = (long)exact;
            return l is >= int.MinValue and <= int.MaxValue ? DocValue.FromInt32((int)l) : DocValue.FromInt64(l);
        }
        return DocValue.FromDecimal(ToDecimal(exact, 0));
    }

    private static decimal ToDecimal(BigInteger mantissa, int scale)
    {
        while (scale > 0 && !mantissa.IsZero && (mantissa % 10).IsZero)
        {
            mantissa /= 10;
            scale--;
        }
        if (mantissa.IsZero) return 0m;
        bool neg = mantissa.Sign < 0;
        var abs = BigInteger.Abs(mantissa);
        if (abs.GetBitLength() > 96) throw new CorruptDatabaseException("Decimal key out of range.");
        return new decimal((int)(uint)(abs & uint.MaxValue), (int)(uint)((abs >> 32) & uint.MaxValue), (int)(uint)(abs >> 64), neg, (byte)scale);
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
            case TagNumber: return key[17] == HasFraction ? FractionalNumberKeyLength : NumberKeyLength;
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
    /// <summary>
    /// True when <see cref="Decode"/> returns the stored value with its original type. Numbers are encoded by value
    /// (5, 5L, 5.0 and 5m share a key), so numbers and containers (which may hold numbers) are ambiguous.
    /// </summary>
    public static bool DecodesExactly(ReadOnlySpan<byte> key) =>
        key[0] is TagString or TagBinary or TagObjectId or TagBoolean or TagDateTime;

    public static DocValue Decode(ReadOnlySpan<byte> key, out int consumed)
    {
        byte tag = key[0];
        switch (tag)
        {
            case TagNull: consumed = 1; return DocValue.Null;
            case TagNumber: return DecodeNumber(key, out consumed);
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
