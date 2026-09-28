using System.Text.RegularExpressions;

namespace FolioDb.Query;

/// <summary>Compiles Mongo-style filter documents into <see cref="Filter"/> trees.</summary>
internal static class FilterParser
{
    public static Filter Parse(Document? filter)
    {
        if (filter is null || filter.Count == 0) return Filter.All;
        var list = new List<Filter>();
        foreach (var (key, value) in filter)
        {
            switch (key)
            {
                case "$and": list.Add(Combine(ParseList(key, value), and: true)); break;
                case "$or": list.Add(Combine(ParseList(key, value), and: false)); break;
                case "$nor": list.Add(new NotFilter(Combine(ParseList(key, value), and: false))); break;
                default:
                    if (key.StartsWith('$')) throw new FolioException($"Unknown top-level operator '{key}'.");
                    if (key.Length == 0) throw new FolioException("Empty field name in filter.");
                    list.Add(ParseField(key, value));
                    break;
            }
        }
        return list.Count == 1 ? list[0] : new AndFilter(Flatten(list));
    }

    private static Filter[] Flatten(List<Filter> list)
    {
        var result = new List<Filter>(list.Count);
        foreach (var f in list)
        {
            if (f is AndFilter a) result.AddRange(a.Children);
            else result.Add(f);
        }
        return result.ToArray();
    }

    private static Filter Combine(List<Filter> filters, bool and)
    {
        if (filters.Count == 1) return filters[0];
        return and ? new AndFilter(Flatten(filters)) : new OrFilter(filters.ToArray());
    }

    private static List<Filter> ParseList(string op, DocValue value)
    {
        if (value.Type != DocType.Array || value.AsArray.Count == 0)
            throw new FolioException($"{op} requires a non-empty array of filter documents.");
        var list = new List<Filter>();
        foreach (var item in value.AsArray)
        {
            if (item.Type != DocType.Document) throw new FolioException($"{op} entries must be documents.");
            list.Add(Parse(item.AsDocument));
        }
        return list;
    }

    private static bool IsOperatorDocument(DocValue value)
    {
        if (value.Type != DocType.Document || value.AsDocument.Count == 0) return false;
        foreach (var (k, _) in value.AsDocument) return k.StartsWith('$');
        return false;
    }

    private static Filter ParseField(string path, DocValue value)
    {
        if (!IsOperatorDocument(value)) return new FieldFilter(path, FieldOp.Eq, value);

        var ops = value.AsDocument;
        var list = new List<Filter>();
        string? regexOptions = ops.TryGetValue("$options", out var o) ? o.AsString : null;
        foreach (var (op, arg) in ops)
        {
            switch (op)
            {
                case "$eq": list.Add(new FieldFilter(path, FieldOp.Eq, arg)); break;
                case "$ne": list.Add(new NotFilter(new FieldFilter(path, FieldOp.Eq, arg))); break;
                case "$gt": list.Add(new FieldFilter(path, FieldOp.Gt, arg)); break;
                case "$gte": list.Add(new FieldFilter(path, FieldOp.Gte, arg)); break;
                case "$lt": list.Add(new FieldFilter(path, FieldOp.Lt, arg)); break;
                case "$lte": list.Add(new FieldFilter(path, FieldOp.Lte, arg)); break;
                case "$in": list.Add(new FieldFilter(path, FieldOp.In, values: RequireArray(op, arg))); break;
                case "$nin": list.Add(new NotFilter(new FieldFilter(path, FieldOp.In, values: RequireArray(op, arg)))); break;
                case "$all": list.Add(new FieldFilter(path, FieldOp.All, values: RequireArray(op, arg))); break;
                case "$exists": list.Add(new FieldFilter(path, FieldOp.Exists, Truthy(arg))); break;
                case "$size":
                    if (!arg.IsNumber) throw new FolioException("$size requires a number.");
                    list.Add(new FieldFilter(path, FieldOp.Size, arg.AsInt32));
                    break;
                case "$type": list.Add(new FieldFilter(path, FieldOp.Type, (int)ParseType(arg))); break;
                case "$regex":
                {
                    if (arg.Type != DocType.String) throw new FolioException("$regex requires a string pattern.");
                    list.Add(new FieldFilter(path, FieldOp.Regex, arg, regex: BuildRegex(arg.AsString, regexOptions)));
                    break;
                }
                case "$options": break;
                case "$not":
                    if (!IsOperatorDocument(arg)) throw new FolioException("$not requires an operator document.");
                    list.Add(new NotFilter(ParseField(path, arg)));
                    break;
                case "$elemMatch":
                {
                    if (arg.Type != DocType.Document) throw new FolioException("$elemMatch requires a document.");
                    bool valueOps = IsOperatorDocument(arg);
                    var inner = valueOps ? ParseField("v", arg) : Parse(arg.AsDocument);
                    list.Add(new FieldFilter(path, FieldOp.ElemMatch, elemFilter: inner, elemIsValueFilter: valueOps));
                    break;
                }
                default: throw new FolioException($"Unknown operator '{op}' on field '{path}'.");
            }
        }
        return list.Count == 1 ? list[0] : new AndFilter(Flatten(list));
    }

    private static bool Truthy(DocValue v) => v.Type switch
    {
        DocType.Boolean => v.AsBoolean,
        DocType.Null => false,
        DocType.Int32 or DocType.Int64 or DocType.Double => v.AsDouble != 0,
        _ => true,
    };

    private static List<DocValue> RequireArray(string op, DocValue v)
    {
        if (v.Type != DocType.Array) throw new FolioException($"{op} requires an array.");
        return v.AsArray.ToList();
    }

    private static DocType ParseType(DocValue v)
    {
        if (v.IsNumber) return (DocType)v.AsInt32;
        return v.AsString.ToLowerInvariant() switch
        {
            "double" => DocType.Double,
            "string" => DocType.String,
            "object" or "document" => DocType.Document,
            "array" => DocType.Array,
            "bindata" or "binary" => DocType.Binary,
            "objectid" => DocType.ObjectId,
            "bool" or "boolean" => DocType.Boolean,
            "date" or "datetime" => DocType.DateTime,
            "null" => DocType.Null,
            "int" or "int32" => DocType.Int32,
            "long" or "int64" => DocType.Int64,
            var s => throw new FolioException($"Unknown $type '{s}'."),
        };
    }

    private static Regex BuildRegex(string pattern, string? options)
    {
        var o = RegexOptions.CultureInvariant;
        foreach (char c in options ?? "")
        {
            o |= c switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                'x' => RegexOptions.IgnorePatternWhitespace,
                _ => throw new FolioException($"Unsupported regex option '{c}'."),
            };
        }
        return new Regex(pattern, o, TimeSpan.FromSeconds(2));
    }
}
