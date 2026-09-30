namespace FolioDb.Tests;

public class KeyEncoderTests
{
    private static int Sign(int x) => Math.Sign(x);

    public static TheoryData<string, string> OrderedPairs => new()
    {
        { "null", "-1e300" },
        { "-5", "-4.5" },
        { "-1", "0" },
        { "0", "1" },
        { "1", "1.5" },
        { "9007199254740992", "9007199254740993" }, // beyond double precision
        { "9223372036854775806", "9223372036854775807" },
        { "1e300", "''" },
        { "''", "'a'" },
        { "'a'", "'a\\u0000'" },
        { "'a\\u0000'", "'a\\u0001'" },
        { "'ab'", "'b'" },
        { "'zzz'", "{}" },
        { "{}", "{ a: 1 }" },
        { "{ a: 1 }", "{ a: 2 }" },
        { "{ a: 1 }", "{ a: 1, b: 1 }" },
        { "{ z: 1 }", "[]" },
        { "[]", "[1]" },
        { "[1]", "[1, 2]" },
        { "[1, 2]", "[2]" },
        { "[9]", "{ $binary: 'AA==' }" },
        { "{ $oid: '000000000000000000000001' }", "{ $oid: '000000000000000000000002' }" },
        { "{ $oid: 'ffffffffffffffffffffffff' }", "false" },
        { "false", "true" },
        { "true", "{ $date: '1970-01-01T00:00:00Z' }" },
        { "{ $date: '1969-12-31T23:59:59Z' }", "{ $date: '1970-01-01T00:00:00Z' }" },
    };

    [Theory]
    [MemberData(nameof(OrderedPairs))]
    public void Encoding_preserves_order(string a, string b)
    {
        var va = DocJson.Parse(a);
        var vb = DocJson.Parse(b);
        var ka = KeyEncoder.Encode(va);
        var kb = KeyEncoder.Encode(vb);
        Assert.True(KeyEncoder.EncodeTemporary(va).SequenceEqual(ka));
        Assert.True(KeyEncoder.EncodeTemporary(vb).SequenceEqual(kb));
        Assert.True(ka.AsSpan().SequenceCompareTo(kb) < 0, $"{a} should sort before {b}");
        Assert.True(KeyEncoder.Compare(va, vb) < 0);
        Assert.True(va.CompareTo(vb) < 0);
    }

    [Fact]
    public void Numeric_types_with_equal_values_encode_identically()
    {
        Assert.Equal(KeyEncoder.Encode(5), KeyEncoder.Encode(5L));
        Assert.Equal(KeyEncoder.Encode(5), KeyEncoder.Encode(5.0));
        Assert.Equal(DocValue.FromInt32(5), DocValue.FromDouble(5.0));
    }

    [Fact]
    public void Encoded_keys_are_prefix_free_and_decodable()
    {
        var values = new[] { "null", "1", "-2.5", "'abc'", "'a\\u0000b'", "{ a: [1, { b: 'x' }] }", "[[], [[]]]", "true", "{ $binary: 'AAEC' }" }
            .Select(DocJson.Parse).ToList();
        foreach (var v in values)
        {
            var key = KeyEncoder.Encode(v);
            var withSuffix = key.Concat(new byte[] { 0x42, 0x00, 0x01 }).ToArray();
            Assert.Equal(key.Length, KeyEncoder.EncodedLength(withSuffix));
            var decoded = KeyEncoder.Decode(withSuffix, out int consumed);
            Assert.Equal(key.Length, consumed);
            Assert.Equal(0, KeyEncoder.Compare(v, decoded));
        }
    }

    [Fact]
    public void Random_values_sort_consistently_with_encoded_bytes()
    {
        var rnd = new Random(1234);
        var values = new List<DocValue>();
        for (int i = 0; i < 2000; i++)
        {
            values.Add((rnd.Next(6)) switch
            {
                0 => DocValue.FromInt32(rnd.Next(-1000, 1000)),
                1 => DocValue.FromInt64(rnd.NextInt64(long.MinValue, long.MaxValue)),
                2 => DocValue.FromDouble((rnd.NextDouble() - 0.5) * Math.Pow(10, rnd.Next(-5, 20))),
                3 => DocValue.FromString(new string((char)rnd.Next('a', 'e'), rnd.Next(0, 4)) + (char)rnd.Next(0, 3)),
                4 => DocValue.FromBoolean(rnd.Next(2) == 0),
                _ => DocValue.Null,
            });
        }
        var byKey = values.OrderBy(v => KeyEncoder.Encode(v), Comparer<byte[]>.Create((x, y) => x.AsSpan().SequenceCompareTo(y))).ToList();
        for (int i = 1; i < byKey.Count; i++)
        {
            var a = byKey[i - 1];
            var b = byKey[i];
            Assert.True(KeyEncoder.Compare(a, b) <= 0);
            if (a.IsNumber && b.IsNumber)
            {
                if (a.Type != DocType.Double && b.Type != DocType.Double) Assert.True(a.AsInt64 <= b.AsInt64);
                else Assert.True(a.AsDouble <= b.AsDouble); // int64 -> double rounding is monotonic
            }
            if (a.Type == DocType.String && b.Type == DocType.String) Assert.True(string.CompareOrdinal(a.AsString, b.AsString) <= 0);
        }
    }
}
