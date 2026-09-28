using System.Text;
using System.Text.RegularExpressions;

namespace FolioDb.Query;

/// <summary>Compiled filter evaluated directly against serialized documents (no materialization).</summary>
internal abstract class Filter
{
    public abstract bool Matches(RawDocument doc);

    public static readonly Filter All = new TrueFilter();

    private sealed class TrueFilter : Filter
    {
        public override bool Matches(RawDocument doc) => true;
        public override string ToString() => "{}";
    }
}

internal sealed class AndFilter(Filter[] children) : Filter
{
    public Filter[] Children { get; } = children;

    public override bool Matches(RawDocument doc)
    {
        foreach (var c in Children)
            if (!c.Matches(doc)) return false;
        return true;
    }

    public override string ToString() => "$and[" + string.Join(", ", Children.Select(c => c.ToString())) + "]";
}

internal sealed class OrFilter(Filter[] children) : Filter
{
    public override bool Matches(RawDocument doc)
    {
        foreach (var c in children)
            if (c.Matches(doc)) return true;
        return false;
    }

    public override string ToString() => "$or[" + string.Join(", ", children.Select(c => c.ToString())) + "]";
}

internal sealed class NotFilter(Filter inner) : Filter
{
    public override bool Matches(RawDocument doc) => !inner.Matches(doc);
    public override string ToString() => "$not(" + inner + ")";
}

internal enum FieldOp { Eq, Gt, Gte, Lt, Lte, In, Exists, Size, Regex, Type, ElemMatch, All }

/// <summary>Predicate on a (dotted) field path with Mongo semantics for arrays: matches if the value or any array element matches.</summary>
internal sealed class FieldFilter : Filter
{
    [ThreadStatic] private static ByteBuffer? t_scratch;

    public string Path { get; }
    public FieldOp Op { get; }
    /// <summary>Encoded comparison constant (Eq and ranges).</summary>
    public byte[]? Key { get; }
    public DocValue Value { get; }
    /// <summary>Sorted encoded constants for $in / $all.</summary>
    public byte[][]? Keys { get; }
    public IReadOnlyList<DocValue>? Values { get; }

    private readonly byte[][] _segments;
    private readonly int[] _segmentIndexes;
    private readonly bool _matchesMissing;
    private readonly Regex? _regex;
    private readonly int _size;
    private readonly DocType _typeFilter;
    private readonly Filter? _elemFilter;
    private readonly bool _elemIsValueFilter;

    public FieldFilter(string path, FieldOp op, DocValue value = default, IReadOnlyList<DocValue>? values = null,
        Regex? regex = null, Filter? elemFilter = null, bool elemIsValueFilter = false)
    {
        Path = path;
        Op = op;
        Value = value;
        Values = values;
        var parts = path.Split('.');
        _segments = parts.Select(Encoding.UTF8.GetBytes).ToArray();
        _segmentIndexes = parts.Select(p => int.TryParse(p, out int i) && i >= 0 ? i : -1).ToArray();

        switch (op)
        {
            case FieldOp.Eq:
            case FieldOp.Gt:
            case FieldOp.Gte:
            case FieldOp.Lt:
            case FieldOp.Lte:
                Key = KeyEncoder.Encode(value);
                _matchesMissing = value.IsNull && op is FieldOp.Eq or FieldOp.Gte or FieldOp.Lte;
                break;
            case FieldOp.In:
            case FieldOp.All:
                Keys = values!.Select(KeyEncoder.Encode).OrderBy(k => k, ByteArrayComparer.Instance).ToArray();
                _matchesMissing = op == FieldOp.In && values!.Any(v => v.IsNull);
                break;
            case FieldOp.Exists:
                break;
            case FieldOp.Size:
                _size = value.AsInt32;
                break;
            case FieldOp.Regex:
                _regex = regex;
                break;
            case FieldOp.Type:
                _typeFilter = (DocType)value.AsInt32;
                break;
            case FieldOp.ElemMatch:
                _elemFilter = elemFilter;
                _elemIsValueFilter = elemIsValueFilter;
                break;
        }
    }

    public override string ToString() => Op switch
    {
        FieldOp.In or FieldOp.All => $"{Path} ${Op.ToString().ToLowerInvariant()} [{string.Join(", ", Values!.Select(v => DocJson.WriteValue(v)))}]",
        FieldOp.Regex => $"{Path} $regex /{_regex}/",
        FieldOp.ElemMatch => $"{Path} $elemMatch({_elemFilter})",
        _ => $"{Path} ${Op.ToString().ToLowerInvariant()} {DocJson.WriteValue(Value)}",
    };

    public override bool Matches(RawDocument doc)
    {
        if (Op == FieldOp.Exists) return Walk(new RawValue(DocType.Document, doc.Data), 0, out _) == Value.AsBoolean;
        bool matched = Walk(new RawValue(DocType.Document, doc.Data), 0, out bool sawMissing);
        return matched || (sawMissing && _matchesMissing);
    }

    /// <summary>Walks the path, fanning out over arrays of documents. Returns true if any reached value matches.</summary>
    private bool Walk(RawValue current, int seg, out bool sawMissing)
    {
        sawMissing = false;
        if (seg == _segments.Length) return TestTerminal(current);

        if (current.Type == DocType.Document)
        {
            if (current.AsDocument.TryGetField(_segments[seg], out var next)) return Walk(next, seg + 1, out sawMissing);
            sawMissing = true;
            return false;
        }

        if (current.Type == DocType.Array)
        {
            int index = _segmentIndexes[seg];
            int i = 0;
            bool anyMissing = false;
            foreach (var item in current.AsArray)
            {
                if (index >= 0)
                {
                    if (i++ == index && Walk(item, seg + 1, out bool m)) return true;
                    continue;
                }
                if (item.Type == DocType.Document)
                {
                    if (Walk(item, seg, out bool m)) return true;
                    anyMissing |= m;
                }
            }
            sawMissing = anyMissing;
            return false;
        }

        sawMissing = true;
        return false;
    }

    private bool TestTerminal(RawValue v)
    {
        if (Op == FieldOp.Exists) return true;
        if (Op == FieldOp.Size) return v.Type == DocType.Array && CountItems(v) == _size;
        if (Op == FieldOp.All) return TestAll(v);
        if (Op == FieldOp.ElemMatch) return TestElemMatch(v);
        if (Test(v)) return true;
        if (v.Type == DocType.Array)
        {
            foreach (var item in v.AsArray)
                if (Test(item)) return true;
        }
        return false;
    }

    private static int CountItems(RawValue v)
    {
        int n = 0;
        foreach (var _ in v.AsArray) n++;
        return n;
    }

    private bool TestAll(RawValue v)
    {
        foreach (var k in Keys!)
        {
            bool found = Compare(v, k, out _) == 0;
            if (!found && v.Type == DocType.Array)
            {
                foreach (var item in v.AsArray)
                {
                    if (Compare(item, k, out _) == 0)
                    {
                        found = true;
                        break;
                    }
                }
            }
            if (!found) return false;
        }
        return Keys!.Length > 0;
    }

    private bool TestElemMatch(RawValue v)
    {
        if (v.Type != DocType.Array) return false;
        foreach (var item in v.AsArray)
        {
            if (_elemIsValueFilter)
            {
                // {$elemMatch: {$gt: 1, $lt: 5}} — operators apply to the element itself; wrap it in a synthetic doc.
                var buf = new ByteBuffer(item.Data.Length + 16);
                try
                {
                    buf.WriteInt32(0);
                    buf.WriteByte((byte)item.Type);
                    buf.WriteByte(1);
                    buf.WriteByte((byte)'v');
                    WriteRawValue(buf, item);
                    buf.WriteByte(0);
                    buf.PatchInt32(0, buf.Length);
                    if (_elemFilter!.Matches(new RawDocument(buf.WrittenSpan))) return true;
                }
                finally
                {
                    buf.Dispose();
                }
            }
            else if (item.Type == DocType.Document && _elemFilter!.Matches(item.AsDocument)) return true;
        }
        return false;
    }

    private static void WriteRawValue(ByteBuffer buf, RawValue v)
    {
        if (v.Type is DocType.String or DocType.Binary) buf.WriteInt32(v.Data.Length);
        buf.Write(v.Data);
    }

    private bool Test(RawValue v)
    {
        switch (Op)
        {
            case FieldOp.Eq:
                return Compare(v, Key!, out _) == 0;
            case FieldOp.Gt:
            {
                int c = Compare(v, Key!, out bool sameType);
                return sameType && c > 0;
            }
            case FieldOp.Gte:
            {
                int c = Compare(v, Key!, out bool sameType);
                return sameType && c >= 0;
            }
            case FieldOp.Lt:
            {
                int c = Compare(v, Key!, out bool sameType);
                return sameType && c < 0;
            }
            case FieldOp.Lte:
            {
                int c = Compare(v, Key!, out bool sameType);
                return sameType && c <= 0;
            }
            case FieldOp.In:
            {
                var buf = t_scratch ??= new ByteBuffer();
                buf.Clear();
                KeyEncoder.Encode(buf, v);
                var span = buf.WrittenSpan;
                int lo = 0, hi = Keys!.Length - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >>> 1;
                    int c = span.SequenceCompareTo(Keys[mid]);
                    if (c == 0) return true;
                    if (c < 0) hi = mid - 1;
                    else lo = mid + 1;
                }
                return false;
            }
            case FieldOp.Regex:
                return v.Type == DocType.String && _regex!.IsMatch(Encoding.UTF8.GetString(v.Data));
            case FieldOp.Type:
                return v.Type == _typeFilter;
            default:
                return false;
        }
    }

    /// <summary>Compares the encoded form of <paramref name="v"/> with an encoded constant. Ranges only match within the same type class.</summary>
    private static int Compare(RawValue v, byte[] key, out bool sameType)
    {
        var buf = t_scratch ??= new ByteBuffer();
        buf.Clear();
        KeyEncoder.Encode(buf, v);
        var span = buf.WrittenSpan;
        sameType = span[0] == key[0];
        return span.SequenceCompareTo(key);
    }
}

internal sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();
    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
    public int GetHashCode(byte[] obj)
    {
        var h = new HashCode();
        h.AddBytes(obj);
        return h.ToHashCode();
    }
}
