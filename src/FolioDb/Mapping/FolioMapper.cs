using System.ComponentModel;
using System.Globalization;

namespace FolioDb.Mapping;

/// <summary>Runtime helpers called by source-generated mappers. Not intended for direct use.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class FolioMapper
{
    public static DocValue WriteArray<T>(IEnumerable<T>? items, Func<T, DocValue> write)
    {
        if (items is null) return DocValue.Null;
        var arr = items is ICollection<T> c ? new DocArray(c.Count) : new DocArray();
        foreach (var item in items) arr.Add(write(item));
        return arr;
    }

    public static List<T> ReadList<T>(DocValue value, Func<DocValue, T> read)
    {
        if (value.IsNull) return null!;
        var arr = value.AsArray;
        var list = new List<T>(arr.Count);
        foreach (var item in arr) list.Add(read(item));
        return list;
    }

    public static T[] ReadArray<T>(DocValue value, Func<DocValue, T> read) => value.IsNull ? null! : ReadList(value, read).ToArray();

    public static HashSet<T> ReadHashSet<T>(DocValue value, Func<DocValue, T> read) => value.IsNull ? null! : new HashSet<T>(ReadList(value, read));

    public static DocValue WriteMap<T>(IEnumerable<KeyValuePair<string, T>>? map, Func<T, DocValue> write)
    {
        if (map is null) return DocValue.Null;
        var doc = new Document();
        foreach (var (k, v) in map) doc.Set(k, write(v));
        return doc;
    }

    public static Dictionary<string, T> ReadMap<T>(DocValue value, Func<DocValue, T> read)
    {
        if (value.IsNull) return null!;
        var doc = value.AsDocument;
        var map = new Dictionary<string, T>(doc.Count);
        foreach (var (k, v) in doc) map[k] = read(v);
        return map;
    }

    public static DocValue WriteGuid(Guid value) => value.ToString("D");

    public static Guid ReadGuid(DocValue value) => value.Type switch
    {
        DocType.String => Guid.Parse(value.AsString),
        DocType.Binary when value.AsBinary.Length == 16 => new Guid(value.AsBinary),
        _ => throw new InvalidCastException($"Cannot read {value.Type} value as Guid."),
    };

    /// <summary>Decimals are stored as invariant strings: lossless (value and scale), but not numerically comparable in queries.</summary>
    public static DocValue WriteDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static decimal ReadDecimal(DocValue value) => value.Type switch
    {
        DocType.String => decimal.Parse(value.AsString, CultureInfo.InvariantCulture),
        DocType.Int32 or DocType.Int64 => value.AsInt64,
        _ => (decimal)value.AsDouble,
    };

    public static DocValue WriteDateTimeOffset(DateTimeOffset value) => DocValue.FromDateTime(value.UtcDateTime);
    public static DateTimeOffset ReadDateTimeOffset(DocValue value) => new(value.AsDateTime);

    public static DocValue WriteDateOnly(DateOnly value) => DocValue.FromDateTime(value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    public static DateOnly ReadDateOnly(DocValue value) => DateOnly.FromDateTime(value.AsDateTime);

    public static DocValue WriteTimeSpan(TimeSpan value) => value.Ticks;
    public static TimeSpan ReadTimeSpan(DocValue value) => TimeSpan.FromTicks(value.AsInt64);

    public static DocValue WriteChar(char value) => value.ToString();
    public static char ReadChar(DocValue value) => value.AsString is [var c] ? c : throw new InvalidCastException("Expected a single-character string.");

    /// <summary>Stored as Int64 when it fits, otherwise as an invariant decimal string (lossless).</summary>
    public static DocValue WriteUInt64(ulong value) =>
        value <= long.MaxValue ? (long)value : value.ToString(CultureInfo.InvariantCulture);

    public static ulong ReadUInt64(DocValue value) => value.Type switch
    {
        DocType.String => ulong.Parse(value.AsString, CultureInfo.InvariantCulture),
        DocType.Int32 or DocType.Int64 => checked((ulong)value.AsInt64),
        _ => checked((ulong)value.AsDouble),
    };
}
