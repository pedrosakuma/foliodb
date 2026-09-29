using System.Numerics;

namespace FolioDb.Tests;

public class DecimalTests
{
    // Exact rational value of a finite number: Num / Den (Den > 0).
    private static (BigInteger Num, BigInteger Den) Exact(DocValue v)
    {
        switch (v.Type)
        {
            case DocType.Int32:
            case DocType.Int64: return (v.AsInt64, BigInteger.One);
            case DocType.Decimal:
            {
                var bits = decimal.GetBits(v.AsDecimal);
                var m = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
                return (bits[3] < 0 ? -m : m, BigInteger.Pow(10, (bits[3] >> 16) & 0xFF));
            }
            default:
            {
                double d = v.AsDouble;
                long raw = BitConverter.DoubleToInt64Bits(d);
                int exp = (int)((raw >> 52) & 0x7FF);
                long mant = raw & 0xF_FFFF_FFFF_FFFFL;
                if (exp == 0) exp = 1; else mant |= 1L << 52;
                BigInteger m = raw < 0 ? -mant : mant;
                int e = exp - 1075;
                return e >= 0 ? (m << e, BigInteger.One) : (m, BigInteger.One << -e);
            }
        }
    }

    private static int ExactCompare(DocValue a, DocValue b)
    {
        var (an, ad) = Exact(a);
        var (bn, bd) = Exact(b);
        return (an * bd).CompareTo(bn * ad);
    }

    private static decimal RandomDecimal(Random rng)
    {
        int scale = rng.Next(0, 29);
        return new decimal(rng.Next(), rng.Next(), rng.Next(0, 4) == 0 ? rng.Next() : rng.Next(0, 1000), rng.Next(2) == 0, (byte)scale);
    }

    private static List<DocValue> Corpus(int seed)
    {
        var rng = new Random(seed);
        var values = new List<DocValue>
        {
            0, 1, -1, 0.1, 0.1m, 0.10m, -0.1m, 0.3m, 1.5, 1.5m, 2.5m, 1e-28m, -1e-28m,
            decimal.MaxValue, decimal.MinValue, long.MaxValue, long.MinValue, (decimal)long.MaxValue + 1,
            9007199254740993L, 9007199254740993m, 9007199254740992.5m, 9007199254740992.0,
            (double)decimal.MaxValue, 79228162514264337593543950335.0 * 2, -1e300, 1e300, double.Epsilon,
        };
        // Powers of two (half-ulp spacing below them) and exact ties between adjacent doubles.
        for (int e = 54; e < 96; e++)
        {
            var p = BigInteger.One << e;
            foreach (var off in new[] { BigInteger.Zero, BigInteger.One << (e - 53), -(BigInteger.One << (e - 54)), BigInteger.One, -BigInteger.One,
                                        (BigInteger.One << (e - 53)) + 1, (BigInteger.One << (e - 52)) + (BigInteger.One << (e - 53)) })
            {
                var v = p + off;
                if (v.GetBitLength() <= 96) values.Add((decimal)v);
            }
            if (e <= 90)
                foreach (var frac in new[] { 0.5m, 1.5m, -0.5m, -1.5m })
                    values.Add((decimal)p + frac);
            values.Add(Math.Pow(2, e));
            values.Add(Math.BitDecrement(Math.Pow(2, e)));
        }
        for (int e = -90; e < 53; e += 3)
        {
            double pw = Math.Pow(2, e);
            values.Add(pw);
            foreach (var probe in new[] { pw, Math.BitDecrement(pw), Math.BitIncrement(pw) })
                try
                {
                    decimal dm = (decimal)probe;
                    values.Add(dm);
                    values.Add(dm + 1e-28m);
                    values.Add(dm - 1e-28m);
                }
                catch (OverflowException) { }
        }
        // Negative binades: the half-ulp spacing is above -2^n (toward zero).
        values.AddRange(values.Select(v => v.Type switch
        {
            DocType.Int32 => (DocValue)(-(long)v.AsInt32),
            DocType.Int64 => v.AsInt64 == long.MinValue ? (DocValue)(-(decimal)long.MinValue) : -v.AsInt64,
            DocType.Double => -v.AsDouble,
            _ => -v.AsDecimal,
        }).ToList());
        for (int i = 0; i < 250; i++)
        {
            decimal x = RandomDecimal(rng);
            values.Add(x);
            double d = (double)x;
            values.Add(d);
            if (Math.Abs(d) < 9e18) values.Add((long)d);
            // Neighbouring doubles and decimals right next to them.
            values.Add(Math.BitIncrement(d));
            try { values.Add((decimal)Math.BitIncrement(d)); } catch (OverflowException) { }
            values.Add(rng.NextInt64(long.MinValue, long.MaxValue));
            values.Add(BitConverter.Int64BitsToDouble(rng.NextInt64()) is var r && double.IsFinite(r) ? r : 0.0);
        }
        return values;
    }

    [Fact]
    public void Key_order_matches_exact_numeric_order_across_types()
    {
        var values = Corpus(42);
        var keys = values.Select(KeyEncoder.Encode).ToArray();
        var exact = values.Select(Exact).ToArray();
        for (int i = 0; i < values.Count; i++)
            for (int j = 0; j < values.Count; j++)
            {
                int expected = (exact[i].Num * exact[j].Den).CompareTo(exact[j].Num * exact[i].Den);
                int actual = Math.Sign(keys[i].AsSpan().SequenceCompareTo(keys[j]));
                Assert.True(expected == actual, $"{values[i].Type} {values[i]} vs {values[j].Type} {values[j]}: exact {expected}, key {actual}");
            }
    }

    [Fact]
    public void Keys_decode_to_the_same_value()
    {
        foreach (var v in Corpus(7))
        {
            var key = KeyEncoder.Encode(v);
            Assert.Equal(key.Length, KeyEncoder.EncodedLength(key));
            var back = KeyEncoder.Decode(key, out int consumed);
            Assert.Equal(key.Length, consumed);
            Assert.True(ExactCompare(v, back) == 0, $"{v.Type} {v} decoded as {back.Type} {back}");
            if (key.Length == KeyEncoder.FractionalNumberKeyLength) Assert.Equal(DocType.Decimal, back.Type);
        }
    }

    [Fact]
    public void Equal_values_of_different_types_share_the_key()
    {
        Assert.Equal(KeyEncoder.Encode(5), KeyEncoder.Encode(5.00m));
        Assert.Equal(KeyEncoder.Encode(5.0), KeyEncoder.Encode(5m));
        Assert.Equal(KeyEncoder.Encode(0.1m), KeyEncoder.Encode(0.10000m));
        Assert.Equal(KeyEncoder.Encode(9007199254740993L), KeyEncoder.Encode(9007199254740993m));
        Assert.NotEqual(KeyEncoder.Encode(0.1), KeyEncoder.Encode(0.1m));
        Assert.True(KeyEncoder.Compare(0.1m, 0.1) < 0); // the double 0.1 is 0.1000000000000000055…
        Assert.Equal(KeyEncoder.NumberKeyLength, KeyEncoder.Encode(1.5m).Length);
    }

    [Fact]
    public void Serializer_round_trips_decimals_with_scale()
    {
        var doc = new Document { ["a"] = 1.50m, ["b"] = decimal.MinValue, ["c"] = new DocArray { 0.1m, -0m } };
        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        Assert.Equal(DocType.Decimal, back["a"].Type);
        Assert.Equal("1.50", back["a"].ToString());
        Assert.Equal(decimal.MinValue, back["b"].AsDecimal);
        Assert.Equal(0.1m, back["c"].AsArray[0].AsDecimal);
    }

    [Fact]
    public void Json_round_trip_and_shell_syntax()
    {
        var doc = Document.Parse("{ a: NumberDecimal('0.10'), b: { $numberDecimal: '-7.5' }, c: NumberDecimal(3) }");
        Assert.Equal(0.10m, doc["a"].AsDecimal);
        Assert.Equal("0.10", doc["a"].ToString());
        Assert.Equal(-7.5m, doc["b"].AsDecimal);
        Assert.Equal(DocType.Decimal, doc["c"].Type);
        string json = doc.ToJson();
        Assert.Contains("\"$numberDecimal\":\"0.10\"", json);
        Assert.Equal(json, Document.Parse(json).ToJson());
        Assert.Throws<FormatException>(() => Document.Parse("{ a: NumberDecimal('1e40') }"));
    }

    [Fact]
    public void Queries_indexes_and_sort_mix_numeric_types()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("prices");
        c.InsertMany([
            Document.Parse("{ _id: 1, p: NumberDecimal('0.1') }"),
            Document.Parse("{ _id: 2, p: 0.1 }"),
            Document.Parse("{ _id: 3, p: 1 }"),
            Document.Parse("{ _id: 4, p: NumberDecimal('1.00') }"),
            Document.Parse("{ _id: 5, p: NumberDecimal('10.5') }"),
            Document.Parse("{ _id: 6, p: '9' }"),
        ]);

        Assert.Equal([1, 2, 3, 4, 5], c.Find((Document?)null, new FindOptions { Sort = Document.Parse("{ p: 1, _id: 1 }") }).Take(5).Select(d => d["_id"].AsInt32));
        Assert.Equal([3, 4], c.Find("{ p: 1 }").Select(d => d["_id"].AsInt32).Order());
        Assert.Equal([5], c.Find("{ p: { $gt: NumberDecimal('1') } }").Select(d => d["_id"].AsInt32));
        Assert.Equal([1], c.Find("{ p: { $lt: 0.1 } }").Select(d => d["_id"].AsInt32));
        Assert.Equal([2], c.Find("{ p: { $gt: NumberDecimal('0.1'), $lt: 1 } }").Select(d => d["_id"].AsInt32));
        Assert.Equal([1, 4, 5], c.Find("{ p: { $type: 'decimal' } }").Select(d => d["_id"].AsInt32).Order());

        c.CreateIndex("p");
        Assert.StartsWith("IXSCAN", c.Explain("{ p: { $gt: NumberDecimal('1') } }").ToString());
        Assert.Equal([3, 4], c.Find("{ p: 1 }").Select(d => d["_id"].AsInt32).Order());
        Assert.Equal([5], c.Find("{ p: { $gt: NumberDecimal('1') } }").Select(d => d["_id"].AsInt32));
        Assert.Equal([1], c.Find("{ p: { $lt: 0.1 } }").Select(d => d["_id"].AsInt32));
        Assert.Equal([2], c.Find("{ p: { $gt: NumberDecimal('0.1'), $lt: 1 } }").Select(d => d["_id"].AsInt32));
        db.CheckIntegrity();
    }

    [Fact]
    public void Decimal_ids_are_exact_primary_keys()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        c.Insert(new Document { ["_id"] = 0.1m, ["v"] = "dec" });
        c.Insert(new Document { ["_id"] = 0.1, ["v"] = "dbl" });
        Assert.Throws<DuplicateKeyException>(() => c.Insert(new Document { ["_id"] = 0.100m }));
        Assert.Equal("dec", c.FindById(0.1m)!["v"].AsString);
        Assert.Equal("dbl", c.FindById(0.1)!["v"].AsString);
        c.CreateIndex("v");
        Assert.Equal(0.1m, c.FindOne("{ v: 'dec' }")!["_id"].AsDecimal);
    }

    [Fact]
    public void Arithmetic_promotes_to_decimal()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        c.Insert("{ _id: 1, i: 1, d: 0.1, m: NumberDecimal('0.1') }");
        c.UpdateOne("{ _id: 1 }", "{ $inc: { i: NumberDecimal('0.2'), m: NumberDecimal('0.2'), n: NumberDecimal('1.5') }, $mul: { d: NumberDecimal('3') } }");
        var doc = c.FindById(1)!;
        Assert.Equal(1.2m, doc["i"].AsDecimal);
        Assert.Equal(0.3m, doc["m"].AsDecimal); // exact, unlike 0.1 + 0.2 in double
        Assert.Equal(DocType.Decimal, doc["d"].Type);
        Assert.Equal(1.5m, doc["n"].AsDecimal);
        c.UpdateOne("{ _id: 1 }", "{ $set: { big: NumberDecimal('79228162514264337593543950335') } }");
        Assert.Throws<FolioException>(() => c.UpdateOne("{ _id: 1 }", "{ $inc: { big: NumberDecimal('1') } }"));
    }
}
