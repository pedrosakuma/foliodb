using FolioDb.Query;

namespace FolioDb.Tests;

public class InPlaceUpdateTests
{
    private static byte[] Slow(byte[] bytes, Document update) =>
        DocumentSerializer.Serialize(UpdateApplier.Apply(Document.FromBytes(bytes), update));

    [Theory]
    [InlineData("{ $inc: { i: 1 } }", true)]
    [InlineData("{ $inc: { i: 2147483647 } }", false)] // int32 -> int64 grows
    [InlineData("{ $inc: { l: NumberLong(5) } }", true)]
    [InlineData("{ $inc: { d: 0.5 } }", true)]
    [InlineData("{ $inc: { m: NumberDecimal('1.25') } }", true)]
    [InlineData("{ $inc: { d: NumberLong(1) } }", true)]
    [InlineData("{ $inc: { i: 0.5 } }", false)] // int32 -> double grows
    [InlineData("{ $mul: { l: NumberLong(3) } }", true)]
    [InlineData("{ $set: { s: 'xyz' } }", true)]
    [InlineData("{ $set: { s: 'xyzw' } }", false)]
    [InlineData("{ $set: { l: 2.5 } }", true)] // int64 -> double, same size
    [InlineData("{ $set: { b: false } }", true)]
    [InlineData("{ $set: { 'sub.x': 9 } }", true)]
    [InlineData("{ $set: { 'arr.1': 7 } }", true)]
    [InlineData("{ $set: { 'arr.5': 7 } }", false)]
    [InlineData("{ $set: { missing: 1 } }", false)]
    [InlineData("{ $set: { 'i.x': 1 } }", false)]
    [InlineData("{ $set: { sub: { x: 2 } } }", false)]
    [InlineData("{ $min: { d: -1.0 } }", true)]
    [InlineData("{ $max: { d: -1.0 } }", true)] // no-op
    [InlineData("{ $currentDate: { t: true } }", true)]
    [InlineData("{ $inc: { i: 1 }, $set: { s: 'abd' }, $mul: { d: 2 } }", true)]
    [InlineData("{ $inc: { i: 1 }, $unset: { s: 1 } }", false)]
    [InlineData("{ $push: { arr: 1 } }", false)]
    public void PatchMatchesFullRewrite(string update, bool patchable)
    {
        var doc = Document.Parse("""
            { _id: 1, i: 10, l: NumberLong(7), d: 1.5, m: NumberDecimal('3.10'), s: 'abc', b: true,
              t: ISODate('2020-01-01T00:00:00Z'), sub: { x: 1, y: 'q' }, arr: [1, 2, 3] }
            """);
        var bytes = DocumentSerializer.Serialize(doc);
        var u = Document.Parse(update);
        var patched = UpdateApplier.TryPatch(bytes, u);
        Assert.Equal(patchable, patched is not null);
        if (patched is null) return;
        if (update.Contains("$currentDate"))
            Assert.Equal(DocType.DateTime, Document.FromBytes(patched)["t"].Type);
        else
            Assert.Equal(Slow(bytes, u), patched);
    }

    [Fact]
    public void MalformedNestedLengthDeclinesFastPath()
    {
        // Nested { a: int32 } whose declared length ends right after the field name (payload crosses the container).
        var bytes = DocumentSerializer.Serialize(new Document { ["sub"] = new Document { ["a"] = 256 }, ["z"] = 5 });
        Assert.Equal(12, BitConverter.ToInt32(bytes, 9));
        BitConverter.GetBytes(8).CopyTo(bytes, 9);
        Assert.Null(UpdateApplier.TryPatch(bytes, Document.Parse("{ $set: { 'sub.a': 3 } }")));
    }

    [Fact]
    public void RandomPatchesMatchFullRewrite()
    {
        var rng = new Random(42);
        string[] fields = ["i", "l", "d", "m", "s", "sub.x", "arr.0", "nope"];
        DocValue RandomValue() => rng.Next(6) switch
        {
            0 => rng.Next(-1000, 1000),
            1 => (long)rng.Next(),
            2 => rng.NextDouble() * 100,
            3 => (decimal)rng.Next(1000) / 100m,
            4 => new string('a', rng.Next(1, 5)),
            _ => rng.Next(2) == 0,
        };
        int patched = 0;
        for (int iter = 0; iter < 5000; iter++)
        {
            var doc = new Document
            {
                ["_id"] = iter, ["i"] = RandomValue(), ["l"] = RandomValue(), ["d"] = RandomValue(), ["m"] = RandomValue(),
                ["s"] = RandomValue(), ["sub"] = new Document { ["x"] = RandomValue() }, ["arr"] = new DocArray { RandomValue() },
            };
            var bytes = DocumentSerializer.Serialize(doc);
            var update = new Document();
            foreach (var op in new[] { "$set", "$inc", "$mul", "$min", "$max" })
                if (rng.Next(3) == 0)
                {
                    var arg = new Document();
                    arg[fields[rng.Next(fields.Length)]] = op is "$inc" or "$mul" ? (DocValue)(rng.Next(2) == 0 ? rng.Next(-5, 5) : rng.NextDouble()) : RandomValue();
                    update[op] = arg;
                }
            if (update.Count == 0) continue;

            byte[]? slow;
            try { slow = Slow(bytes, update); }
            catch (FolioException)
            {
                // The fast path must either decline or fail the same way.
                try { Assert.Null(UpdateApplier.TryPatch(bytes, update)); } catch (FolioException) { }
                continue;
            }
            var fast = UpdateApplier.TryPatch(bytes, update);
            if (fast is null) continue;
            patched++;
            Assert.Equal(slow, fast);
        }
        Assert.True(patched > 500, $"only {patched} patched");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(64 * 1024)]
    public void InPlaceUpdateIsDurableAndSnapshotIsolated(int payload)
    {
        using var tmp = new TempDb();
        using (var db = tmp.Open())
        {
            var c = db.GetCollection("c");
            c.CreateIndex("n");
            string big = new('x', payload);
            for (int i = 0; i < 20; i++) c.Insert(new Document { ["_id"] = i, ["n"] = 0, ["payload"] = big, ["tail"] = 1.0 });

            using var snap = db.BeginSnapshot();
            for (int k = 0; k < 5; k++)
            {
                var r = c.UpdateOne(Document.Parse("{ _id: 3 }"), Document.Parse("{ $inc: { n: 1, tail: 0.5 } }"));
                Assert.Equal(1, r.ModifiedCount);
            }
            Assert.Equal(0, c.UpdateOne("{ _id: 3 }", "{ $max: { n: 1 } }").ModifiedCount);

            var old = snap.GetCollection("c").FindById(3)!;
            Assert.Equal(0, old["n"].AsInt32);
            Assert.Equal(1.0, old["tail"].AsDouble);
            Assert.Equal(1, c.Count("{ n: 5 }"));
            Assert.Equal(19, c.Count("{ n: 0 }"));
            db.CheckIntegrity();
        }
        using (var db = tmp.Open())
        {
            var c = db.GetCollection("c");
            var d = c.FindById(3)!;
            Assert.Equal(5, d["n"].AsInt32);
            Assert.Equal(3.5, d["tail"].AsDouble);
            Assert.Equal(payload, d["payload"].AsString.Length);
            Assert.Equal(1, c.Count("{ n: 5 }"));
            db.CheckIntegrity();
        }
    }

    [Fact]
    public void RolledBackInPlaceUpdateLeavesDocumentUnchanged()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        db.GetCollection("c").Insert(new Document { ["_id"] = 1, ["n"] = 1, ["payload"] = new string('y', 20_000) });
        using (var tx = db.BeginTransaction())
        {
            tx.GetCollection("c").UpdateOne("{ _id: 1 }", "{ $inc: { n: 41 } }");
            Assert.Equal(42, tx.GetCollection("c").FindById(1)!["n"].AsInt32);
        }
        Assert.Equal(1, db.GetCollection("c").FindById(1)!["n"].AsInt32);
    }
}
