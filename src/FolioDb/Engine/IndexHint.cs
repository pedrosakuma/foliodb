namespace FolioDb.Engine;

/// <summary>An index value key plus the type hint needed to decode it back to the stored value.</summary>
internal readonly record struct IndexKey(byte[] Key, byte[] Hint);

/// <summary>
/// Type hints stored as the value of secondary index entries (<c>idHint ++ valueHint</c>), in the spirit of MongoDB's
/// KeyString TypeBits. Numeric keys are shared across int32/int64/double/decimal and normalize <c>-0.0</c> and decimal
/// scale, so the hint records what the key cannot: the numeric type (plus the raw payload for double/decimal).
/// Entries written before hints existed have an empty value; readers then fall back to the document.
/// </summary>
internal static class IndexHint
{
    /// <summary>The key decodes to the stored value as is (string, binary, ObjectId, boolean, date).</summary>
    public const byte Exact = 0x00;
    /// <summary>The key cannot be decoded exactly (documents, arrays).</summary>
    public const byte Unknown = 0xFF;

    private static readonly byte[] s_exact = [Exact];
    private static readonly byte[] s_unknown = [Unknown];
    private static readonly byte[] s_int32 = [(byte)DocType.Int32];
    private static readonly byte[] s_int64 = [(byte)DocType.Int64];

    public static byte[] For(RawValue v) => v.Type switch
    {
        DocType.Int32 => s_int32,
        DocType.Int64 => s_int64,
        DocType.Double or DocType.Decimal => [(byte)v.Type, .. v.Data],
        DocType.String or DocType.Binary or DocType.ObjectId or DocType.Boolean or DocType.DateTime => s_exact,
        _ => s_unknown,
    };

    public static byte[] ForId(ReadOnlySpan<byte> doc) =>
        new RawDocument(doc).TryGetField("_id"u8, out var id) ? For(id) : s_unknown;

    public static byte[] Entry(byte[] idHint, byte[] valueHint) => [.. idHint, .. valueHint];

    private static int Length(ReadOnlySpan<byte> hints) => hints[0] switch
    {
        (byte)DocType.Double => 9,
        (byte)DocType.Decimal => 17,
        _ => 1,
    };

    /// <summary>Splits an index entry value into its id and value hints; false for legacy (hint-less) or malformed entries.</summary>
    public static bool TrySplit(ReadOnlySpan<byte> entry, out ReadOnlySpan<byte> idHint, out ReadOnlySpan<byte> valueHint)
    {
        idHint = valueHint = default;
        if (entry.IsEmpty) return false;
        int n = Length(entry);
        if (entry.Length <= n || entry.Length != n + Length(entry[n..])) return false;
        idHint = entry[..n];
        valueHint = entry[n..];
        return true;
    }

    /// <summary>Last byte of compound entries: the indexed fields appear in the document in index order.</summary>
    public const byte InOrder = 0x01;
    public const byte OutOfOrder = 0x00;

    /// <summary>
    /// Splits the value of an index entry with <paramref name="components"/> fields (<c>idHint ++ hint₁ … hintₙ</c>,
    /// plus the order flag when n &gt; 1) into ranges; false for legacy or malformed entries.
    /// </summary>
    public static bool TrySplit(ReadOnlySpan<byte> entry, int components, out Range idHint, Span<Range> valueHints, out bool inOrder)
    {
        idHint = default;
        inOrder = components == 1;
        if (entry.IsEmpty) return false;
        int pos = 0;
        for (int i = -1; i < components; i++)
        {
            if (pos >= entry.Length) return false;
            int n = Length(entry[pos..]);
            if (pos + n > entry.Length) return false;
            if (i < 0) idHint = pos..(pos + n);
            else valueHints[i] = pos..(pos + n);
            pos += n;
        }
        if (components > 1)
        {
            if (pos + 1 != entry.Length) return false;
            inOrder = entry[pos] == InOrder;
            return true;
        }
        return pos == entry.Length;
    }

    /// <summary>Decodes <paramref name="key"/> with its hint; false when the stored value cannot be rebuilt exactly.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> key, ReadOnlySpan<byte> hint, out DocValue value)
    {
        value = default;
        if (hint.IsEmpty)
        {
            if (!KeyEncoder.DecodesExactly(key)) return false;
            value = KeyEncoder.Decode(key, out _);
            return true;
        }
        switch (hint[0])
        {
            case Exact when KeyEncoder.DecodesExactly(key):
                value = KeyEncoder.Decode(key, out _);
                return true;
            case (byte)DocType.Int32 or (byte)DocType.Int64 when key[0] == KeyEncoder.TagNumber:
            {
                var n = KeyEncoder.Decode(key, out _);
                if (n.Type is not (DocType.Int32 or DocType.Int64)) return false;
                long l = n.AsInt64;
                if (hint[0] == (byte)DocType.Int64) value = DocValue.FromInt64(l);
                else if (l is >= int.MinValue and <= int.MaxValue) value = DocValue.FromInt32((int)l);
                else return false;
                return true;
            }
            case (byte)DocType.Double when hint.Length == 9 && key[0] == KeyEncoder.TagNumber:
                value = DocValue.FromDouble(new RawValue(DocType.Double, hint[1..]).Double);
                return true;
            case (byte)DocType.Decimal when hint.Length == 17 && key[0] == KeyEncoder.TagNumber:
                value = DocValue.FromDecimal(new RawValue(DocType.Decimal, hint[1..]).Decimal);
                return true;
            default:
                return false;
        }
    }
}
