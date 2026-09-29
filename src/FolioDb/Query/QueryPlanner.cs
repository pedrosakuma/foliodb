using FolioDb.Engine;
using FolioDb.Storage;

namespace FolioDb.Query;

internal enum PlanKind { FullScan, PrimaryEq, PrimaryRange, IndexEq, IndexRange }

/// <summary>Access path chosen by the planner. The full filter is always re-applied to fetched documents.</summary>
internal sealed class QueryPlan
{
    public PlanKind Kind { get; init; }
    public IndexMeta? Index { get; init; }
    public string? Field { get; init; }
    /// <summary>Encoded equality keys (sorted, distinct) for Eq/In plans.</summary>
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
        _ => Kind.ToString(),
    };

    private string RangeText()
    {
        string lo = Lower is null ? "(-∞" : (LowerInclusive ? "[" : "(") + DocJson.WriteValue(KeyEncoder.Decode(Lower, out _));
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
            var index = primary ? null : meta.FindIndexByField(f.Path);
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
                plan = new QueryPlan { Kind = primary ? PlanKind.PrimaryEq : PlanKind.IndexEq, Index = index, Field = f.Path, Keys = f.Keys!.Distinct(ByteArrayComparer.Instance).ToList(), Covered = exact && conjuncts.Length == 1 };
                score = primary ? 95 : 70;
            }
            else if (f.Op is FieldOp.Gt or FieldOp.Gte or FieldOp.Lt or FieldOp.Lte && Indexable(f.Value))
            {
                plan = BuildRange(conjuncts, f, index, primary);
                score = plan.Lower is not null && plan.Upper is not null ? 60 : 50;
            }

            if (plan is not null && score > bestScore)
            {
                best = plan;
                bestScore = score;
            }
        }
        return best ?? new QueryPlan { Kind = PlanKind.FullScan, Covered = ReferenceEquals(filter, Filter.All) };
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
            switch (f.Op)
            {
                case FieldOp.Gt:
                case FieldOp.Gte:
                {
                    bool inc = f.Op == FieldOp.Gte;
                    int cmp = lower is null ? 1 : f.Key.AsSpan().SequenceCompareTo(lower);
                    if (cmp > 0 || (cmp == 0 && !inc)) { lower = f.Key; lowerInc = inc; }
                    break;
                }
                case FieldOp.Lt:
                case FieldOp.Lte:
                {
                    bool inc = f.Op == FieldOp.Lte;
                    int cmp = upper is null ? -1 : f.Key.AsSpan().SequenceCompareTo(upper);
                    if (cmp < 0 || (cmp == 0 && !inc)) { upper = f.Key; upperInc = inc; }
                    break;
                }
            }
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

    /// <summary>Streams candidate documents for the plan (filter NOT applied). Visitor returns false to stop.</summary>
    public static void Execute(StorageTx tx, CollectionMeta meta, QueryPlan plan, DocVisitor visitor)
    {
        var primary = new BTree(tx, meta.PrimaryRoot);
        switch (plan.Kind)
        {
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
                foreach (var key in plan.Keys!.OrderBy(k => k, ByteArrayComparer.Instance))
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
                foreach (var prefix in plan.Keys!.OrderBy(k => k, ByteArrayComparer.Instance))
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
            case PlanKind.FullScan:
            {
                var cur = primary.CreateCursor();
                for (bool ok = cur.SeekFirst(); ok; ok = cur.MoveNext())
                    if (!visitor(cur.Key, cur.Key, default)) return;
                return;
            }
            case PlanKind.PrimaryEq:
                foreach (var key in plan.Keys!.OrderBy(k => k, ByteArrayComparer.Instance))
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
                foreach (var prefix in plan.Keys!.OrderBy(k => k, ByteArrayComparer.Instance))
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
