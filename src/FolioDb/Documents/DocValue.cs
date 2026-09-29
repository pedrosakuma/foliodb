using System.Globalization;

namespace FolioDb;

/// <summary>
/// An immutable, tagged document value. Scalars are stored inline (no boxing except for <see cref="ObjectId"/>);
/// strings, binaries, documents and arrays are stored by reference.
/// </summary>
public readonly struct DocValue : IEquatable<DocValue>, IComparable<DocValue>
{
    private readonly long _bits;
    private readonly object? _ref;
    private readonly DocType _type;

    private DocValue(DocType type, long bits, object? reference)
    {
        _type = type;
        _bits = bits;
        _ref = reference;
    }

    public static DocValue Null => default;

    public DocType Type => _type == 0 ? DocType.Null : _type;
    public bool IsNull => Type == DocType.Null;
    public bool IsNumber => _type is DocType.Int32 or DocType.Int64 or DocType.Double or DocType.Decimal;

    public static DocValue FromInt32(int v) => new(DocType.Int32, v, null);
    public static DocValue FromInt64(long v) => new(DocType.Int64, v, null);
    public static DocValue FromDouble(double v) => new(DocType.Double, BitConverter.DoubleToInt64Bits(v), null);
    public static DocValue FromDecimal(decimal v) => new(DocType.Decimal, 0, v);
    public static DocValue FromBoolean(bool v) => new(DocType.Boolean, v ? 1 : 0, null);
    public static DocValue FromString(string? v) => v is null ? Null : new(DocType.String, 0, v);
    public static DocValue FromBinary(byte[]? v) => v is null ? Null : new(DocType.Binary, 0, v);
    public static DocValue FromObjectId(ObjectId v) => new(DocType.ObjectId, 0, v);
    public static DocValue FromDocument(Document? v) => v is null ? Null : new(DocType.Document, 0, v);
    public static DocValue FromArray(DocArray? v) => v is null ? Null : new(DocType.Array, 0, v);

    /// <summary>Date/time stored as milliseconds since Unix epoch (UTC).</summary>
    public static DocValue FromDateTime(DateTime v) => FromUnixMilliseconds(ToUnixMs(v));
    public static DocValue FromUnixMilliseconds(long ms) => new(DocType.DateTime, ms, null);

    private static long ToUnixMs(DateTime v)
    {
        if (v.Kind == DateTimeKind.Local) v = v.ToUniversalTime();
        return (v.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
    }

    public static implicit operator DocValue(int v) => FromInt32(v);
    public static implicit operator DocValue(long v) => FromInt64(v);
    public static implicit operator DocValue(double v) => FromDouble(v);
    public static implicit operator DocValue(decimal v) => FromDecimal(v);
    public static implicit operator DocValue(bool v) => FromBoolean(v);
    public static implicit operator DocValue(string? v) => FromString(v);
    public static implicit operator DocValue(byte[]? v) => FromBinary(v);
    public static implicit operator DocValue(ObjectId v) => FromObjectId(v);
    public static implicit operator DocValue(DateTime v) => FromDateTime(v);
    public static implicit operator DocValue(Document? v) => FromDocument(v);
    public static implicit operator DocValue(DocArray? v) => FromArray(v);

    public int AsInt32 => _type switch
    {
        DocType.Int32 => (int)_bits,
        DocType.Int64 => checked((int)_bits),
        DocType.Double => checked((int)BitConverter.Int64BitsToDouble(_bits)),
        DocType.Decimal => (int)(decimal)_ref!,
        _ => throw InvalidCast("Int32"),
    };

    public long AsInt64 => _type switch
    {
        DocType.Int32 or DocType.Int64 => _bits,
        DocType.Double => checked((long)BitConverter.Int64BitsToDouble(_bits)),
        DocType.Decimal => (long)(decimal)_ref!,
        _ => throw InvalidCast("Int64"),
    };

    public double AsDouble => _type switch
    {
        DocType.Int32 or DocType.Int64 => _bits,
        DocType.Double => BitConverter.Int64BitsToDouble(_bits),
        DocType.Decimal => (double)(decimal)_ref!,
        _ => throw InvalidCast("Double"),
    };

    /// <summary>Exact for integers and decimals; doubles are converted with <see cref="decimal"/>'s own rounding (~15 significant digits).</summary>
    public decimal AsDecimal => _type switch
    {
        DocType.Int32 or DocType.Int64 => _bits,
        DocType.Double => (decimal)BitConverter.Int64BitsToDouble(_bits),
        DocType.Decimal => (decimal)_ref!,
        _ => throw InvalidCast("Decimal"),
    };

    public bool AsBoolean => _type == DocType.Boolean ? _bits != 0 : throw InvalidCast("Boolean");
    public string AsString => _type == DocType.String ? (string)_ref! : throw InvalidCast("String");
    public byte[] AsBinary => _type == DocType.Binary ? (byte[])_ref! : throw InvalidCast("Binary");
    public ObjectId AsObjectId => _type == DocType.ObjectId ? (ObjectId)_ref! : throw InvalidCast("ObjectId");
    public Document AsDocument => _type == DocType.Document ? (Document)_ref! : throw InvalidCast("Document");
    public DocArray AsArray => _type == DocType.Array ? (DocArray)_ref! : throw InvalidCast("Array");
    public long AsUnixMilliseconds => _type == DocType.DateTime ? _bits : throw InvalidCast("DateTime");
    public DateTime AsDateTime => _type == DocType.DateTime
        ? new DateTime(DateTime.UnixEpoch.Ticks + _bits * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc)
        : throw InvalidCast("DateTime");

    /// <summary>Raw 64-bit payload for scalar types (used by the serializer).</summary>
    internal long RawBits => _bits;

    private InvalidCastException InvalidCast(string target) => new($"Cannot read {Type} value as {target}.");

    public bool Equals(DocValue other) => KeyEncoder.Compare(this, other) == 0;
    public override bool Equals(object? obj) => obj is DocValue v && Equals(v);
    public override int GetHashCode()
    {
        var key = KeyEncoder.Encode(this);
        var h = new HashCode();
        h.AddBytes(key);
        return h.ToHashCode();
    }

    /// <summary>Total order across all types (same order used by indexes and sort).</summary>
    public int CompareTo(DocValue other) => KeyEncoder.Compare(this, other);

    public static bool operator ==(DocValue l, DocValue r) => l.Equals(r);
    public static bool operator !=(DocValue l, DocValue r) => !l.Equals(r);

    public override string ToString() => Type switch
    {
        DocType.Null => "null",
        DocType.Int32 or DocType.Int64 => _bits.ToString(CultureInfo.InvariantCulture),
        DocType.Double => AsDouble.ToString("R", CultureInfo.InvariantCulture),
        DocType.Decimal => AsDecimal.ToString(CultureInfo.InvariantCulture),
        DocType.Boolean => AsBoolean ? "true" : "false",
        DocType.String => AsString,
        DocType.ObjectId => AsObjectId.ToString(),
        DocType.DateTime => AsDateTime.ToString("O", CultureInfo.InvariantCulture),
        _ => DocJson.WriteValue(this),
    };

    /// <summary>Deep copy (documents, arrays and binary buffers are cloned; immutable scalars are shared).</summary>
    public DocValue DeepClone() => _type switch
    {
        DocType.Document => FromDocument(AsDocument.Clone()),
        DocType.Array => FromArray(AsArray.Clone()),
        DocType.Binary => FromBinary(AsBinary.ToArray()),
        _ => this,
    };
}
