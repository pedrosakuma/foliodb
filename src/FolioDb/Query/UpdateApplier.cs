using System.Buffers.Binary;
using System.Text;

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

    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    /// <summary>
    /// Fast path for operator updates that only overwrite existing scalar values with values of the same encoded size
    /// (e.g. <c>$inc</c> on an int32/int64/double/decimal, <c>$set</c> of a same-length string). Returns the patched
    /// bytes, byte-identical to <c>Serialize(Apply(Deserialize(original), update))</c>, or null when the update needs
    /// the general path (missing fields, growing values, containers, array operators, ...).
    /// </summary>
    public static byte[]? TryPatch(byte[] original, Document update)
    {
        if (!IsOperatorUpdate(update)) return null;
        byte[]? doc = null;
        using var buf = new ByteBuffer(64);
        foreach (var (op, arg) in update)
        {
            if (op is not ("$set" or "$inc" or "$mul" or "$min" or "$max" or "$currentDate")) return null;
            foreach (var (path, value) in arg.AsDocument)
            {
                var data = doc ?? original;
                if (!TryLocate(data, path, out int typePos, out int valuePos, out int size)) return null;
                var cur = RawValue.Read((DocType)data[typePos], data.AsSpan(valuePos), out _);
                if (cur.Type is DocType.Document or DocType.Array) return null;

                DocValue next;
                switch (op)
                {
                    case "$set": next = value; break;
                    case "$inc": next = Arith(cur.ToDocValue(), path, value, multiply: false); break;
                    case "$mul": next = Arith(cur.ToDocValue(), path, value, multiply: true); break;
                    case "$min":
                        if (value.CompareTo(cur.ToDocValue()) >= 0) continue;
                        next = value;
                        break;
                    case "$max":
                        if (value.CompareTo(cur.ToDocValue()) <= 0) continue;
                        next = value;
                        break;
                    default: next = DateTime.UtcNow; break;
                }
                if (next.Type is DocType.Document or DocType.Array) return null;

                buf.Clear();
                DocumentSerializer.WriteValue(buf, next, 0);
                if (buf.Length != size) return null;
                doc ??= (byte[])original.Clone();
                doc[typePos] = (byte)next.Type;
                buf.WrittenSpan.CopyTo(doc.AsSpan(valuePos));
            }
        }
        return doc ?? original;
    }

    /// <summary>Finds an existing value by dotted path, with the same resolution rules as <see cref="Document.TryGetPath"/>.</summary>
    private static bool TryLocate(ReadOnlySpan<byte> data, string path, out int typePos, out int valuePos, out int size)
    {
        typePos = valuePos = size = 0;
        int start = 0;
        bool isArray = false;
        Span<byte> name = stackalloc byte[DocumentSerializer.MaxNameBytes];
        var segments = path.Split('.');
        for (int s = 0; s < segments.Length; s++)
        {
            string segment = segments[s];
            int index = -1, nameLen = 0;
            if (isArray)
            {
                if (!int.TryParse(segment, out index) || index < 0) return false;
            }
            else
            {
                try
                {
                    if (s_strictUtf8.GetByteCount(segment) > name.Length) return false;
                    nameLen = s_strictUtf8.GetBytes(segment, name);
                }
                catch (ArgumentException) { return false; }
            }

            // Every read stays inside the current container; malformed bytes make the fast path decline.
            if (start + 4 > data.Length) return false;
            int len = BinaryPrimitives.ReadInt32LittleEndian(data[start..]);
            if (len < 5 || len > data.Length - start || data[start + len - 1] != 0) return false;
            int end = start + len - 1;
            int pos = start + 4;
            bool found = false;
            for (int i = 0; pos < end && data[pos] != 0; i++)
            {
                int vpos;
                bool match;
                if (isArray)
                {
                    vpos = pos + 1;
                    match = i == index;
                }
                else
                {
                    if (pos + 2 > end) return false;
                    int n = data[pos + 1];
                    vpos = pos + 2 + n;
                    if (vpos > end) return false;
                    match = data.Slice(pos + 2, n).SequenceEqual(name[..nameLen]);
                }
                int consumed;
                try { RawValue.Read((DocType)data[pos], data[vpos..end], out consumed); }
                catch (Exception e) when (e is ArgumentOutOfRangeException or CorruptDatabaseException) { return false; }
                if (vpos + consumed > end) return false;
                if (match)
                {
                    typePos = pos;
                    valuePos = vpos;
                    size = consumed;
                    found = true;
                    break;
                }
                pos = vpos + consumed;
            }
            if (!found) return false;
            var type = (DocType)data[typePos];
            if (type is not (DocType.Document or DocType.Array))
            {
                if (s + 1 < segments.Length) return false;
                break;
            }
            start = valuePos;
            isArray = type == DocType.Array;
        }
        return true;
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

    private static DocValue Arith(Document doc, string path, DocValue operand, bool multiply) =>
        Arith(doc.TryGetPath(path, out var cur) ? cur : DocValue.Null, path, operand, multiply);

    private static DocValue Arith(DocValue cur, string path, DocValue operand, bool multiply)
    {
        if (!operand.IsNumber) throw new FolioException($"{(multiply ? "$mul" : "$inc")} requires a numeric argument.");
        if (cur.IsNull) return multiply ? Zero(operand) : operand;
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
