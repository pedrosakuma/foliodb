using FolioDb.Engine;
using FolioDb.Storage;

namespace FolioDb.Query;

internal enum PlanKind { FullScan, PrimaryEq, PrimaryRange, IndexEq, IndexRange, CompoundScan }

/// <summary>
/// Access path chosen by the planner. Fetched documents are re-checked against the full filter unless the plan is
/// <see cref="Covered"/>.
/// </summary>
internal sealed class QueryPlan
{
    public PlanKind Kind { get; init; }
    public IndexMeta? Index { get; init; }
    public string? Field { get; init; }
    /// <summary>Encoded equality keys for Eq/In plans, already sorted and distinct so execution scans them in order.</summary>
    public List<byte[]>? Keys { get; init; }
    public byte TypeTag { get; init; }
    public byte[]? Lower { get; init; }
    public bool LowerInclusive { get; init; }
    public byte[]? Upper { get; init; }
    public bool UpperInclusive { get; init; }
    /// <summary>
    /// The access path alone decides the result: the filter never needs to be evaluated on the document, so
    /// counts (and projections of the key fields) can be answered from keys only.
    /// </summary>
    public bool Covered { get; init; }
    public byte[] Prefix { get; init; } = [];
    public int PrefixFields { get; init; }
    public QueryPlan? ComponentRange { get; init; }
    /// <summary>Field whose value is the scan key (<c>_id</c> for primary plans).</summary>
    public string KeyField => Kind is PlanKind.IndexEq or PlanKind.IndexRange ? Field! : "_id";

    public override string ToString() => Describe() + (Covered ? " covered" : "");

    private string Describe() => Kind switch
    {
        PlanKind.FullScan => "COLLSCAN",
        PlanKind.PrimaryEq => $"IDHACK _id ({Keys!.Count} key{(Keys.Count == 1 ? "" : "s")})",
        PlanKind.PrimaryRange => $"IXSCAN _id {RangeText()}",
        PlanKind.IndexEq => $"IXSCAN {Index!.Name} ({Keys!.Count} key{(Keys.Count == 1 ? "" : "s")}){(Index.MultiKey ? " multikey" : "")}",
        PlanKind.IndexRange => $"IXSCAN {Index!.Name} {RangeText()}{(Index.MultiKey ? " multikey" : "")}",
        PlanKind.CompoundScan => $"IXSCAN {Index!.Name} (equality prefix: {PrefixFields}{(ComponentRange is null ? "" : ", range")}){(Index.MultiKey ? " multikey" : "")}",
        _ => Kind.ToString(),
    };

    /// <summary>False when the lower bound is absent or only the exclusive NaN key that keeps NaN out of a numeric range.</summary>
    internal bool HasLowerBound =>
        Lower is not null && (LowerInclusive || !Lower.AsSpan().SequenceEqual(KeyEncoder.NaNKey)
                              || (Upper is not null && Upper.AsSpan().SequenceEqual(KeyEncoder.NaNKey)));

    private string RangeText()
    {
        string lo = !HasLowerBound ? "(-∞" : (LowerInclusive ? "[" : "(") + DocJson.WriteValue(KeyEncoder.Decode(Lower, out _));
        string hi = Upper is null ? "+∞)" : DocJson.WriteValue(KeyEncoder.Decode(Upper, out _)) + (UpperInclusive ? "]" : ")");
        return lo + ", " + hi;
    }
}

internal delegate bool DocVisitor(ReadOnlySpan<byte> idKey, ReadOnlySpan<byte> document);
// hints: index entry value (see Engine.IndexHint); empty for primary plans and legacy entries.
internal delegate bool KeyVisitor(ReadOnlySpan<byte> idKey, ReadOnlySpan<byte> valueKey, ReadOnlySpan<byte> hints);

internal static class QueryPlanner
{
    public static QueryPlan Plan(CollectionMeta meta, Filter filter)
    {
        var conjuncts = filter is AndFilter and ? and.Children : [filter];
        QueryPlan? best = null;
        int bestScore = 0;

        foreach (var c in conjuncts)
        {
            if (c is not FieldFilter f) continue;
            bool primary = f.Path == "_id";
            var index = primary ? null : SimpleIndex(meta, f.Path);
            if (!primary && index is null) continue;

            QueryPlan? plan = null;
            int score = 0;
            // Without arrays on the path (not multikey), index keys and filter evaluation see exactly the same value.
            bool exact = primary || !index!.MultiKey;
            if (f.Op == FieldOp.Eq && Indexable(f.Value))
            {
                plan = new QueryPlan { Kind = primary ? PlanKind.PrimaryEq : PlanKind.IndexEq, Index = index, Field = f.Path, Keys = [f.Key!], Covered = exact && conjuncts.Length == 1 };
                score = primary ? 100 : index!.Unique ? 90 : 80;
            }
            else if (f.Op == FieldOp.In && f.Values!.Count > 0 && f.Values.All(Indexable))
            {
                plan = new QueryPlan { Kind = primary ? PlanKind.PrimaryEq : PlanKind.IndexEq, Index = index, Field = f.Path, Keys = SortedDistinct(f.Keys!), Covered = exact && conjuncts.Length == 1 };
                score = primary ? 95 : 70;
            }
            else if (f.Op is FieldOp.Gt or FieldOp.Gte or FieldOp.Lt or FieldOp.Lte && Indexable(f.Value))
            {
                plan = BuildRange(conjuncts, f, index, primary);
                score = plan.HasLowerBound && plan.Upper is not null ? 60 : 50;
            }

            if (plan is not null && score > bestScore)
            {
                best = plan;
                bestScore = score;
            }
        }
        foreach (var index in meta.Indexes)
        {
            if (index.IsSimple) continue;
            var plan = BuildCompound(conjuncts, index, out int score);
            if (plan is not null && score > bestScore) { best = plan; bestScore = score; }
        }
        return best ?? new QueryPlan { Kind = PlanKind.FullScan, Covered = ReferenceEquals(filter, Filter.All) };
    }

    private static IndexMeta? SimpleIndex(CollectionMeta meta, string path)
    {
        foreach (var index in meta.Indexes)
            if (index.IsSimple && index.Field == path) return index;
        return null;
    }

    /// <summary>Removes adjacent duplicates from keys that <see cref="FieldFilter"/> already sorted.</summary>
    private static List<byte[]> SortedDistinct(byte[][] sorted)
    {
        var keys = new List<byte[]>(sorted.Length);
        foreach (var key in sorted)
            if (keys.Count == 0 || !keys[^1].AsSpan().SequenceEqual(key)) keys.Add(key);
        return keys;
    }

    private static QueryPlan? BuildCompound(Filter[] conjuncts, IndexMeta index, out int score)
    {
        byte[] prefix = [];
        int count = 0;
        var consumed = new HashSet<Filter>();
        QueryPlan? range = null;
        foreach (var field in index.Fields)
        {
            var conditions = conjuncts.OfType<FieldFilter>().Where(f => f.Path == field.Path).ToArray();
            var eq = conditions.FirstOrDefault(f => f.Op == FieldOp.Eq && Indexable(f.Value));
            if (eq is not null)
            {
                prefix = [.. prefix, .. field.Descending ? IndexMeta.Inverted(eq.Key!) : eq.Key!];
                consumed.Add(eq);
                count++;
                continue;
            }
            var bound = conditions.FirstOrDefault(f => f.Op is FieldOp.Gt or FieldOp.Gte or FieldOp.Lt or FieldOp.Lte && Indexable(f.Value));
            if (bound is not null)
            {
                range = BuildRange(conjuncts, bound, index, false);
                foreach (var f in conditions)
                    if ((ReferenceEquals(f, bound) || !index.MultiKey) && f.Key is not null
                        && f.Key[0] == range.TypeTag && Indexable(f.Value)
                        && f.Op is FieldOp.Gt or FieldOp.Gte or FieldOp.Lt or FieldOp.Lte) consumed.Add(f);
            }
            break;
        }
        score = Math.Min(94, (count > 0 ? 75 + count * 3 : 50) + (range is null ? 0 : 5));
        if (count == 0 && range is null) return null;
        return new QueryPlan
        {
            Kind = PlanKind.CompoundScan, Index = index, Prefix = prefix, PrefixFields = count,
            ComponentRange = range, Covered = !index.MultiKey && conjuncts.All(consumed.Contains),
        };
    }

    private static void ScanCompound(StorageTx tx, QueryPlan plan, KeyVisitor visitor, bool withHints)
    {
        var index = plan.Index!;
        var range = plan.ComponentRange;
        bool descending = range is not null && index.Fields[plan.PrefixFields].Descending;
        byte[] start = plan.Prefix;
        if (range is not null)
        {
            var lower = descending ? range.Upper : range.Lower;
            start = [.. start, .. lower is null ? new byte[] { descending ? (byte)~range.TypeTag : range.TypeTag }
                : descending ? IndexMeta.Inverted(lower) : lower];
        }
        var seen = index.MultiKey ? new HashSet<byte[]>(ByteArrayComparer.Instance) : null;
        var cur = new BTree(tx, index.Root).CreateCursor();
        for (bool ok = cur.Seek(start); ok && cur.Key.StartsWith(plan.Prefix); ok = cur.MoveNext())
        {
            var key = cur.Key;
            if (range is not null)
            {
                int len = IndexMeta.ComponentLength(key[plan.Prefix.Length..], descending);
                var component = key.Slice(plan.Prefix.Length, len);
                int state = descending ? -RangeState(range, IndexMeta.Inverted(component)) : RangeState(range, component);
                if (state > 0) break;
                if (state < 0) continue;
            }
            int valueLength = index.ValueLength(key, plan.PrefixFields, plan.Prefix.Length);
            var id = key[valueLength..];
            if (seen is not null && !seen.Add(id.ToArray())) continue;
            if (!visitor(id, key[..valueLength], withHints ? cur.Value : default)) return;
        }
    }

    private static bool Indexable(DocValue v) => v.Type is not (DocType.Null or DocType.Array);

    private static QueryPlan BuildRange(Filter[] conjuncts, FieldFilter first, IndexMeta? index, bool primary)
    {
        byte tag = first.Key![0];
        byte[]? lower = null, upper = null;
        bool lowerInc = false, upperInc = false;
        // Multikey indexes cannot combine bounds: [0, 10] satisfies {$gt: 1, $lt: 5} via different elements.
        bool combine = primary || !index!.MultiKey;

        foreach (var c in conjuncts)
        {
            if (c is not FieldFilter f || f.Path != first.Path || f.Key is null || f.Key[0] != tag || !Indexable(f.Value)) continue;
            if (!combine && !ReferenceEquals(f, first)) continue;
            // NaN keys sort below every number but no numeric range holds NaN (see FieldFilter): $gt/$lt NaN is
            // (NaN, NaN), $gte/$lte NaN is [NaN, NaN], and an upper bound on a number starts above NaN.
            bool nan = f.Key.AsSpan().SequenceEqual(KeyEncoder.NaNKey);
            bool upperOp = f.Op is FieldOp.Lt or FieldOp.Lte;
            bool inc = f.Op is FieldOp.Gte or FieldOp.Lte;
            if (nan)
            {
                Tighten(ref lower, ref lowerInc, f.Key, inc, lowerBound: true);
                Tighten(ref upper, ref upperInc, f.Key, inc, lowerBound: false);
            }
            else if (upperOp)
            {
                Tighten(ref upper, ref upperInc, f.Key, inc, lowerBound: false);
                if (tag == KeyEncoder.TagNumber) Tighten(ref lower, ref lowerInc, KeyEncoder.NaNKey, false, lowerBound: true);
            }
            else Tighten(ref lower, ref lowerInc, f.Key, inc, lowerBound: true);
        }

        // Covered when every conjunct is a same-type bound on this field, i.e. all of them were folded into [lower, upper].
        bool covered = combine && conjuncts.All(c => c is FieldFilter f && f.Path == first.Path && f.Op is FieldOp.Gt or FieldOp.Gte or FieldOp.Lt or FieldOp.Lte
                                                     && f.Key is not null && f.Key[0] == tag && Indexable(f.Value));
        return new QueryPlan
        {
            Covered = covered,
            Kind = primary ? PlanKind.PrimaryRange : PlanKind.IndexRange,
            Index = index,
            Field = first.Path,
            TypeTag = tag,
            Lower = lower,
            LowerInclusive = lowerInc,
            Upper = upper,
            UpperInclusive = upperInc,
        };
    }

    /// <summary>Intersects a bound: keeps the higher lower bound (or the lower upper bound); exclusive wins on ties.</summary>
    private static void Tighten(ref byte[]? bound, ref bool boundInc, byte[] key, bool inc, bool lowerBound)
    {
        int cmp = bound is null ? (lowerBound ? 1 : -1) : key.AsSpan().SequenceCompareTo(bound);
        if (lowerBound ? cmp > 0 : cmp < 0) { bound = key; boundInc = inc; }
        else if (cmp == 0 && !inc) boundInc = false;
    }

    /// <summary>Streams candidate documents for the plan (filter NOT applied). Visitor returns false to stop.</summary>
    public static void Execute(StorageTx tx, CollectionMeta meta, QueryPlan plan, DocVisitor visitor)
    {
        var primary = new BTree(tx, meta.PrimaryRoot);
        switch (plan.Kind)
        {
            case PlanKind.CompoundScan:
            {
                var seek = primary.CreateCursor();
                ScanCompound(tx, plan, (id, _, _) =>
                {
                    if (!seek.SeekExact(id)) throw new CorruptDatabaseException($"Index {plan.Index!.Name} references a missing document.");
                    return visitor(id, seek.Value);
                }, false);
                return;
            }
            case PlanKind.FullScan:
            {
                var cur = primary.CreateCursor();
                for (bool ok = cur.SeekFirst(); ok; ok = cur.MoveNext())
                    if (!visitor(cur.Key, cur.Value)) return;
                return;
            }
            case PlanKind.PrimaryEq:
            {
                var seek = primary.CreateCursor();
                foreach (var key in plan.Keys!)
                    if (seek.SeekExact(key) && !visitor(key, seek.Value)) return;
                return;
            }
            case PlanKind.PrimaryRange:
            {
                var cur = primary.CreateCursor();
                for (bool ok = cur.Seek(plan.Lower ?? [plan.TypeTag]); ok; ok = cur.MoveNext())
                {
                    var key = cur.Key;
                    int state = RangeState(plan, key);
                    if (state > 0) return;
                    if (state == 0 && !visitor(key, cur.Value)) return;
                }
                return;
            }
            case PlanKind.IndexEq:
            {
                var index = new BTree(tx, plan.Index!.Root);
                var seen = plan.Index.MultiKey && plan.Keys!.Count > 1 ? new HashSet<byte[]>(ByteArrayComparer.Instance) : null;
                var cur = index.CreateCursor();
                var seek = primary.CreateCursor();
                foreach (var prefix in plan.Keys!)
                {
                    for (bool ok = cur.Seek(prefix); ok && cur.Key.StartsWith(prefix); ok = cur.MoveNext())
                    {
                        var idKey = cur.Key[prefix.Length..];
                        if (seen is not null && !seen.Add(idKey.ToArray())) continue;
                        if (!seek.SeekExact(idKey)) throw new CorruptDatabaseException($"Index {plan.Index.Name} references a missing document.");
                        if (!visitor(idKey, seek.Value)) return;
                    }
                }
                return;
            }
            case PlanKind.IndexRange:
            {
                var index = new BTree(tx, plan.Index!.Root);
                var seen = plan.Index.MultiKey ? new HashSet<byte[]>(ByteArrayComparer.Instance) : null;
                var cur = index.CreateCursor();
                var seek = primary.CreateCursor();
                for (bool ok = cur.Seek(plan.Lower ?? [plan.TypeTag]); ok; ok = cur.MoveNext())
                {
                    var entry = cur.Key;
                    int valueLen = KeyEncoder.EncodedLength(entry);
                    int state = RangeState(plan, entry[..valueLen]);
                    if (state > 0) return;
                    if (state < 0) continue;
                    var idKey = entry[valueLen..];
                    if (seen is not null && !seen.Add(idKey.ToArray())) continue;
                    if (!seek.SeekExact(idKey)) throw new CorruptDatabaseException($"Index {plan.Index.Name} references a missing document.");
                    if (!visitor(idKey, seek.Value)) return;
                }
                return;
            }
        }
    }

    /// <summary>
    /// Streams (idKey, scan key) pairs of a <see cref="QueryPlan.Covered"/> plan without reading documents.
    /// For primary plans the scan key is the id key itself. Visitor returns false to stop.
    /// </summary>
    public static void ExecuteKeys(StorageTx tx, CollectionMeta meta, QueryPlan plan, KeyVisitor visitor, bool withHints = false)
    {
        if (!plan.Covered) throw new InvalidOperationException("Plan is not covered.");
        var primary = new BTree(tx, meta.PrimaryRoot);
        switch (plan.Kind)
        {
            case PlanKind.CompoundScan:
                ScanCompound(tx, plan, visitor, withHints);
                return;
            case PlanKind.FullScan:
            {
                var cur = primary.CreateCursor();
                for (bool ok = cur.SeekFirst(); ok; ok = cur.MoveNext())
                    if (!visitor(cur.Key, cur.Key, default)) return;
                return;
            }
            case PlanKind.PrimaryEq:
                foreach (var key in plan.Keys!)
                    if (primary.ContainsKey(key) && !visitor(key, key, default)) return;
                return;
            case PlanKind.PrimaryRange:
            {
                var cur = primary.CreateCursor();
                for (bool ok = cur.Seek(plan.Lower ?? [plan.TypeTag]); ok; ok = cur.MoveNext())
                {
                    var key = cur.Key;
                    int state = RangeState(plan, key);
                    if (state > 0) return;
                    if (state == 0 && !visitor(key, key, default)) return;
                }
                return;
            }
            case PlanKind.IndexEq:
            {
                var cur = new BTree(tx, plan.Index!.Root).CreateCursor();
                foreach (var prefix in plan.Keys!)
                    for (bool ok = cur.Seek(prefix); ok && cur.Key.StartsWith(prefix); ok = cur.MoveNext())
                        if (!visitor(cur.Key[prefix.Length..], prefix, withHints ? cur.Value : default)) return;
                return;
            }
            case PlanKind.IndexRange:
            {
                var cur = new BTree(tx, plan.Index!.Root).CreateCursor();
                for (bool ok = cur.Seek(plan.Lower ?? [plan.TypeTag]); ok; ok = cur.MoveNext())
                {
                    var entry = cur.Key;
                    int valueLen = KeyEncoder.EncodedLength(entry);
                    int state = RangeState(plan, entry[..valueLen]);
                    if (state > 0) return;
                    if (state == 0 && !visitor(entry[valueLen..], entry[..valueLen], withHints ? cur.Value : default)) return;
                }
                return;
            }
        }
    }

    /// <summary>-1: before range (skip), 0: inside, 1: past the end (stop).</summary>
    private static int RangeState(QueryPlan plan, ReadOnlySpan<byte> value)
    {
        if (value[0] != plan.TypeTag) return value[0] < plan.TypeTag ? -1 : 1;
        if (plan.Lower is not null)
        {
            int c = value.SequenceCompareTo(plan.Lower);
            if (c < 0 || (c == 0 && !plan.LowerInclusive)) return -1;
        }
        if (plan.Upper is not null)
        {
            int c = value.SequenceCompareTo(plan.Upper);
            if (c > 0 || (c == 0 && !plan.UpperInclusive)) return 1;
        }
        return 0;
    }
}
