namespace FolioDb.Query;

/// <summary>Applies Mongo-style update documents (<c>$set</c>, <c>$inc</c>, ...) or whole-document replacements.</summary>
internal static class UpdateApplier
{
    public static bool IsOperatorUpdate(Document update)
    {
        bool? ops = null;
        foreach (var (k, _) in update)
        {
            bool isOp = k.StartsWith('$');
            if (ops is null) ops = isOp;
            else if (ops != isOp) throw new FolioException("Update document cannot mix operators and plain fields.");
        }
        return ops ?? false;
    }

    public static void Validate(Document update)
    {
        if (update.Count == 0) throw new FolioException("Update document is empty.");
        if (!IsOperatorUpdate(update)) return;
        foreach (var (op, arg) in update)
        {
            if (op is not ("$set" or "$unset" or "$inc" or "$mul" or "$min" or "$max" or "$push" or "$addToSet" or "$pull" or "$pop" or "$rename" or "$currentDate"))
                throw new FolioException($"Unknown update operator '{op}'.");
            if (arg.Type != DocType.Document) throw new FolioException($"{op} requires a document argument.");
            foreach (var (path, target) in arg.AsDocument)
            {
                if (IsIdPath(path)) throw new FolioException("The _id field is immutable.");
                if (op == "$rename" && target.Type == DocType.String && IsIdPath(target.AsString))
                    throw new FolioException("The _id field is immutable.");
            }
        }
    }

    private static bool IsIdPath(string path) => path == "_id" || path.StartsWith("_id.", StringComparison.Ordinal);

    /// <summary>Returns the updated document (a new instance). The original is not modified.</summary>
    public static Document Apply(Document original, Document update)
    {
        if (!IsOperatorUpdate(update))
        {
            var replacement = update.Clone();
            if (replacement.TryGetValue("_id", out var newId) && newId != original["_id"])
                throw new FolioException("The _id field is immutable.");
            replacement.InsertFirst("_id", original["_id"]);
            return replacement;
        }

        var doc = original.Clone();
        foreach (var (op, arg) in update)
        {
            foreach (var (path, value) in arg.AsDocument)
            {
                switch (op)
                {
                    case "$set": doc.SetPath(path, value.DeepClone()); break;
                    case "$unset": doc.RemovePath(path); break;
                    case "$inc": doc.SetPath(path, Arith(doc, path, value, multiply: false)); break;
                    case "$mul": doc.SetPath(path, Arith(doc, path, value, multiply: true)); break;
                    case "$min":
                        if (!doc.TryGetPath(path, out var cur) || value.CompareTo(cur) < 0) doc.SetPath(path, value);
                        break;
                    case "$max":
                        if (!doc.TryGetPath(path, out var cur2) || value.CompareTo(cur2) > 0) doc.SetPath(path, value);
                        break;
                    case "$currentDate": doc.SetPath(path, DateTime.UtcNow); break;
                    case "$push":
                    {
                        var arr = GetArray(doc, path);
                        foreach (var v in Each(value)) arr.Add(v);
                        break;
                    }
                    case "$addToSet":
                    {
                        var arr = GetArray(doc, path);
                        foreach (var v in Each(value))
                            if (!arr.Contains(v)) arr.Add(v);
                        break;
                    }
                    case "$pull":
                    {
                        if (doc.TryGetPath(path, out var existing) && existing.Type == DocType.Array)
                            existing.AsArray.RemoveAll(v => v == value);
                        break;
                    }
                    case "$pop":
                    {
                        if (doc.TryGetPath(path, out var existing) && existing.Type == DocType.Array && existing.AsArray.Count > 0)
                        {
                            var a = existing.AsArray;
                            if (value.IsNumber && value.AsDouble < 0) a.RemoveAt(0);
                            else a.RemoveAt(a.Count - 1);
                        }
                        break;
                    }
                    case "$rename":
                    {
                        if (value.Type != DocType.String) throw new FolioException("$rename target must be a string.");
                        if (doc.TryGetPath(path, out var moved))
                        {
                            doc.RemovePath(path);
                            doc.SetPath(value.AsString, moved);
                        }
                        break;
                    }
                }
            }
        }
        if (!doc.TryGetValue("_id", out var id) || id != original["_id"])
            throw new FolioException("The _id field is immutable.");
        return doc;
    }

    private static IEnumerable<DocValue> Each(DocValue value)
    {
        if (value.Type == DocType.Document && value.AsDocument.TryGetValue("$each", out var each))
        {
            if (each.Type != DocType.Array) throw new FolioException("$each requires an array.");
            return each.AsArray.Select(v => v.DeepClone()).ToList();
        }
        return [value.DeepClone()];
    }

    private static DocArray GetArray(Document doc, string path)
    {
        if (!doc.TryGetPath(path, out var existing) || existing.IsNull)
        {
            var arr = new DocArray();
            doc.SetPath(path, arr);
            return arr;
        }
        if (existing.Type != DocType.Array) throw new FolioException($"Field '{path}' is not an array.");
        return existing.AsArray;
    }

    private static DocValue Arith(Document doc, string path, DocValue operand, bool multiply)
    {
        if (!operand.IsNumber) throw new FolioException($"{(multiply ? "$mul" : "$inc")} requires a numeric argument.");
        if (!doc.TryGetPath(path, out var cur) || cur.IsNull) return multiply ? Zero(operand) : operand;
        if (!cur.IsNumber) throw new FolioException($"Cannot apply {(multiply ? "$mul" : "$inc")} to non-numeric field '{path}'.");

        // Promotion: decimal > double > int64 > int32 (as in MongoDB).
        if (cur.Type == DocType.Decimal || operand.Type == DocType.Decimal)
        {
            try { return multiply ? cur.AsDecimal * operand.AsDecimal : cur.AsDecimal + operand.AsDecimal; }
            catch (OverflowException) { throw new FolioException($"Decimal overflow applying {(multiply ? "$mul" : "$inc")} to '{path}'."); }
        }
        if (cur.Type == DocType.Double || operand.Type == DocType.Double)
            return multiply ? cur.AsDouble * operand.AsDouble : cur.AsDouble + operand.AsDouble;

        long a = cur.AsInt64, b = operand.AsInt64;
        long r = multiply ? checked(a * b) : checked(a + b);
        bool wide = cur.Type == DocType.Int64 || operand.Type == DocType.Int64 || r is > int.MaxValue or < int.MinValue;
        return wide ? DocValue.FromInt64(r) : DocValue.FromInt32((int)r);
    }

    private static DocValue Zero(DocValue like) => like.Type switch
    {
        DocType.Double => 0.0,
        DocType.Decimal => 0m,
        DocType.Int64 => 0L,
        _ => 0,
    };
}
