using FolioDb.Engine;

namespace FolioDb.Query;

/// <summary>A materialized, read-only pipeline. Validation precedes execution, including on empty collections.</summary>
internal sealed class Aggregation
{
    private sealed record Stage(Func<List<Document>, List<Document>> Apply, Filter? Match = null);
    private readonly Stage[] _stages;

    public Aggregation(IEnumerable<Document> pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _stages = pipeline.Select(CompileStage).ToArray();
    }

    public static IEnumerable<Document> Parse(DocValue pipeline)
    {
        if (pipeline.Type != DocType.Array) throw new FolioException("Aggregation requires an array of stage documents.");
        return pipeline.AsArray.Select(v => v.Type == DocType.Document ? v.AsDocument
            : throw new FolioException("Every aggregation stage must be a document."));
    }

    public List<Document> Execute(EngineTx tx, CollectionMeta? meta)
    {
        var firstMatch = _stages.Length > 0 ? _stages[0].Match : null;
        var rows = CollectionEngine.Find(tx, meta, firstMatch ?? Filter.All, null);
        for (int i = firstMatch is null ? 0 : 1; i < _stages.Length; i++) rows = _stages[i].Apply(rows);
        return rows;
    }

    private static Stage CompileStage(Document stage)
    {
        if (stage is null || stage.Count != 1) throw new FolioException("Each aggregation stage must contain exactly one operator.");
        var (op, arg) = stage.First();
        switch (op)
        {
            case "$match":
            {
                var filter = FilterParser.Parse(RequireDocument(op, arg));
                return new(rows => rows.Where(d => filter.Matches(new RawDocument(d.ToBytes()))).ToList(), filter);
            }
            case "$project":
            {
                var projection = Projection.Parse(RequireDocument(op, arg));
                return new(rows => projection is null ? rows : rows.Select(d => projection.Apply(new RawDocument(d.ToBytes()))).ToList());
            }
            case "$sort":
            {
                var spec = RequireDocument(op, arg);
                foreach (var (path, dir) in spec)
                {
                    ValidatePath(path);
                    if (!dir.IsNumber || (dir != (DocValue)1 && dir != (DocValue)(-1)))
                        throw new FolioException($"Sort direction for '{path}' must be 1 or -1.");
                }
                var sort = SortSpec.Parse(spec);
                return new(rows =>
                {
                    if (sort is null) return rows;
                    var keyed = rows.Select((d, i) => (Doc: d, Keys: sort.KeysFor(new RawDocument(d.ToBytes())), Seq: i)).ToList();
                    keyed.Sort((a, b) => sort.Compare(a.Keys, b.Keys) is var c and not 0 ? c : a.Seq.CompareTo(b.Seq));
                    return keyed.Select(r => r.Doc).ToList();
                });
            }
            case "$skip":
            case "$limit":
            {
                int count = CountArgument(op, arg);
                return new(rows => op == "$skip" ? rows.Skip(count).ToList() : rows.Take(count).ToList());
            }
            case "$count":
            {
                if (arg.Type != DocType.String) throw new FolioException("$count requires an output field name.");
                string name = arg.AsString;
                ValidateOutputName(name);
                return new(rows => rows.Count == 0 ? [] : [new Document { [name] = (long)rows.Count }]);
            }
            case "$unwind":
            {
                if (arg.Type != DocType.String || !arg.AsString.StartsWith('$'))
                    throw new FolioException("$unwind requires a field reference such as '$items'.");
                string path = arg.AsString[1..];
                ValidatePath(path);
                return new(rows => Unwind(rows, path));
            }
            case "$group":
                return CompileGroup(RequireDocument(op, arg));
            default:
                throw new FolioException($"Unknown aggregation stage '{op}'.");
        }
    }

    private static Document RequireDocument(string op, DocValue arg) =>
        arg.Type == DocType.Document ? arg.AsDocument : throw new FolioException($"{op} requires a document.");

    private static int CountArgument(string op, DocValue arg)
    {
        if (!arg.IsNumber || arg.CompareTo(0) < 0 || arg.CompareTo(int.MaxValue) > 0)
            throw new FolioException($"{op} requires an integer between 0 and {int.MaxValue}.");
        int value = arg.AsInt32;
        if (arg != (DocValue)value) throw new FolioException($"{op} requires an integer.");
        return value;
    }

    private static void ValidateOutputName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.StartsWith('$') || name.Contains('.') || name.Contains('\0')
            || System.Text.Encoding.UTF8.GetByteCount(name) > DocumentSerializer.MaxNameBytes)
            throw new FolioException($"Invalid aggregation output field '{name}'.");
    }

    private static void ValidatePath(string path)
    {
        var parts = path.Split('.');
        if (parts.Length > DocumentSerializer.MaxDepth || parts.Any(p => p.Length == 0 || p.StartsWith('$') || p.Contains('\0')))
            throw new FolioException($"Invalid aggregation field path '{path}'.");
    }

    private static List<Document> Unwind(List<Document> rows, string path)
    {
        var result = new List<Document>();
        foreach (var row in rows)
        {
            if (!row.TryGetPath(path, out var value) || value.IsNull) continue;
            if (value.Type != DocType.Array) { result.Add(row); continue; }
            foreach (var item in value.AsArray)
            {
                var copy = row.Clone();
                copy.SetPath(path, item.DeepClone());
                result.Add(copy);
            }
        }
        return result;
    }

    private static Func<Document, DocValue> Expression(DocValue value, int depth = 0)
    {
        if (depth > DocumentSerializer.MaxDepth) throw new FolioException("Aggregation expression is too deep.");
        if (value.Type == DocType.String && value.AsString.StartsWith('$'))
        {
            string path = value.AsString[1..];
            ValidatePath(path);
            return row => row.TryGetPath(path, out var found) ? found.DeepClone() : DocValue.Null;
        }
        if (value.Type == DocType.Document)
        {
            var doc = value.AsDocument;
            if (doc.Count == 1 && doc.TryGetValue("$literal", out var literal))
                return _ => literal.DeepClone();
            var fields = doc.Select(kv =>
            {
                ValidateOutputName(kv.Key);
                return (kv.Key, Eval: Expression(kv.Value, depth + 1));
            }).ToArray();
            return row =>
            {
                var result = new Document();
                foreach (var (name, eval) in fields) result[name] = eval(row);
                return result;
            };
        }
        if (value.Type == DocType.Array)
        {
            var items = value.AsArray.Select(v => Expression(v, depth + 1)).ToArray();
            return row => new DocArray(items.Select(eval => eval(row)));
        }
        return _ => value.DeepClone();
    }

    private sealed record Accumulator(string Name, string Op, Func<Document, DocValue> Eval);

    private sealed class State
    {
        public DocValue Value = DocValue.Null;
        public long Count;
        public DocArray? Items;

        public void Add(Accumulator spec, Document row)
        {
            var v = spec.Eval(row);
            switch (spec.Op)
            {
                case "$count": Count++; break;
                case "$sum":
                case "$avg":
                    if (!v.IsNumber) break;
                    try { Value = Count == 0 ? v : NumericMath.Apply(Value, v, false, $"in {spec.Op}"); }
                    catch (OverflowException) { throw new FolioException($"Integer overflow in {spec.Op}."); }
                    Count++;
                    break;
                case "$min":
                case "$max":
                    if (!v.IsNull && (Value.IsNull || (spec.Op == "$min" ? v.CompareTo(Value) < 0 : v.CompareTo(Value) > 0))) Value = v;
                    break;
                case "$first": if (Count++ == 0) Value = v; break;
                case "$last": Value = v; break;
                case "$push": (Items ??= new DocArray()).Add(v); break;
            }
        }

        public DocValue Finish(string op) => op switch
        {
            "$count" => Count,
            "$sum" => Count == 0 ? (DocValue)0 : Value,
            "$avg" => Count == 0 ? DocValue.Null : Value.Type == DocType.Decimal
                ? DocValue.FromDecimal(Value.AsDecimal / Count) : DocValue.FromDouble(Value.AsDouble / Count),
            "$push" => Items ?? new DocArray(),
            _ => Value,
        };
    }

    private static Stage CompileGroup(Document spec)
    {
        if (!spec.TryGetValue("_id", out var id)) throw new FolioException("$group requires _id.");
        var key = Expression(id);
        var accumulators = new List<Accumulator>();
        foreach (var (name, value) in spec)
        {
            if (name == "_id") continue;
            ValidateOutputName(name);
            var d = RequireDocument("$group accumulator", value);
            if (d.Count != 1) throw new FolioException("Each group accumulator requires exactly one operator.");
            var (op, arg) = d.First();
            if (op is not ("$sum" or "$avg" or "$min" or "$max" or "$count" or "$push" or "$first" or "$last"))
                throw new FolioException($"Unknown group accumulator '{op}'.");
            if (op == "$count" && (arg.Type != DocType.Document || arg.AsDocument.Count != 0))
                throw new FolioException("$count accumulator requires {}.");
            accumulators.Add(new(name, op, op == "$count" ? _ => DocValue.Null : Expression(arg)));
        }
        return new(rows =>
        {
            var groups = new Dictionary<byte[], (DocValue Key, State[] States)>(ByteArrayComparer.Instance);
            foreach (var row in rows)
            {
                var idValue = key(row);
                var encoded = KeyEncoder.Encode(idValue);
                if (!groups.TryGetValue(encoded, out var group))
                {
                    group = (idValue, accumulators.Select(_ => new State()).ToArray());
                    groups.Add(encoded, group);
                }
                for (int i = 0; i < accumulators.Count; i++) group.States[i].Add(accumulators[i], row);
            }
            return groups.Values.Select(group =>
            {
                var result = new Document { ["_id"] = group.Key };
                for (int i = 0; i < accumulators.Count; i++) result[accumulators[i].Name] = group.States[i].Finish(accumulators[i].Op);
                return result;
            }).ToList();
        });
    }
}
