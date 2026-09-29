namespace FolioDb;

internal static class NumericMath
{
    public static DocValue Apply(DocValue left, DocValue right, bool multiply, string context)
    {
        if (left.Type == DocType.Decimal || right.Type == DocType.Decimal)
        {
            try { return multiply ? left.AsDecimal * right.AsDecimal : left.AsDecimal + right.AsDecimal; }
            catch (OverflowException) { throw new FolioException($"Decimal overflow {context}."); }
        }
        if (left.Type == DocType.Double || right.Type == DocType.Double)
            return multiply ? left.AsDouble * right.AsDouble : left.AsDouble + right.AsDouble;

        long a = left.AsInt64, b = right.AsInt64;
        long result = multiply ? checked(a * b) : checked(a + b);
        bool wide = left.Type == DocType.Int64 || right.Type == DocType.Int64 || result is > int.MaxValue or < int.MinValue;
        return wide ? DocValue.FromInt64(result) : DocValue.FromInt32((int)result);
    }
}
