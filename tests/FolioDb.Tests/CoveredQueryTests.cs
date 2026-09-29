namespace FolioDb.Tests;

/// <summary>Differential tests: an indexed collection (covered/pushdown paths) must match an index-less twin.</summary>
public sealed class CoveredQueryTests : IDisposable
{
    private readonly TempDb _tmp = new();
    private readonly FolioDatabase _db;
    private readonly Collection _idx, _raw;

    public CoveredQueryTests()
    {
        _db = FolioDatabase.Open(_tmp.Path);
        _idx = _db.GetCollection("idx");
        _raw = _db.GetCollection("raw");
        var docs = new List<Document>();
        for (int i = 0; i < 300; i++)
        {
            DocValue num = (i % 4) switch { 0 => i % 17, 1 => (long)(i % 17), 2 => (double)(i % 17), _ => (decimal)(i % 17) };
            docs.Add(new Document
            {
                ["_id"] = i,
                ["city"] = "c" + (i % 7),
                ["num"] = num,
                ["flag"] = i % 3 == 0,
                ["sub"] = new Document { ["k"] = "k" + (i % 5), ["n"] = i },
                ["tags"] = new DocArray { "t" + (i % 3), "t" + (i % 5) },
                ["arr"] = new DocArray { i % 2, "x" + i },
            });
        }
        _idx.InsertMany(docs.Select(d => d.Clone()));
        _raw.InsertMany(docs);
        foreach (var f in new[] { "city", "num", "flag", "sub.k", "tags" }) _idx.CreateIndex(f);
    }

    public void Dispose() { _db.Dispose(); _tmp.Dispose(); }

    private static string Bytes(IEnumerable<Document> docs) =>
        string.Join("|", docs.Select(d => Convert.ToHexString(DocumentSerializer.Serialize(d))));

    [Theory]
    [InlineData("{ city: 'c3' }")]
    [InlineData("{ city: { $in: ['c1', 'c4', 'zz'] } }")]
    [InlineData("{ city: { $gte: 'c2', $lt: 'c5' } }")]
    [InlineData("{ num: 5 }")]
    [InlineData("{ num: { $gt: 3, $lte: 9 } }")]
    [InlineData("{ num: { $gt: NumberLong(3) } }")]
    [InlineData("{ flag: true }")]
    [InlineData("{ 'sub.k': 'k2' }")]
    [InlineData("{ tags: 't1' }")] // multikey: not covered
    [InlineData("{ _id: 42 }")]
    [InlineData("{ _id: { $gte: 100, $lt: 150 } }")]
    [InlineData("{ _id: { $in: [1, 2, 999] } }")]
    [InlineData("{ city: 'c3', num: 5 }")]
    [InlineData("{ city: { $gt: 'c1' }, 'sub.k': { $ne: 'k0' } }")]
    [InlineData("{}")]
    public void CountAndFindMatch(string filter)
    {
        Assert.Equal(_raw.Count(filter), _idx.Count(filter));
        foreach (var proj in new[] { null, "{ _id: 1 }", "{ city: 1, _id: 0 }", "{ city: 1 }", "{ num: 1, _id: 0 }",
                     "{ num: 1 }", "{ flag: 1 }", "{ 'sub.k': 1, _id: 0 }", "{ tags: 1 }", "{ 'arr.1': 1 }", "{ sub: 0 }" })
        {
            foreach (var (skip, limit) in new[] { (0, (int?)null), (3, (int?)5), (0, (int?)1) })
            {
                var o = new FindOptions { Projection = proj is null ? null : Document.Parse(proj), Skip = skip, Limit = limit };
                // Unsorted order is plan-dependent: compare as sets unless a sort is given.
                var a = _idx.Find(filter, o).Select(d => Bytes([d])).Order().ToList();
                var b = _raw.Find(filter, o).Select(d => Bytes([d])).Order().ToList();
                if (skip == 0 && limit is null) Assert.Equal(b, a);
                else Assert.Equal(Math.Min(b.Count, a.Count), a.Count);
            }
        }
    }

    [Theory]
    [InlineData("{ city: { $gte: 'c2' } }", "{ num: 1, _id: -1 }")]
    [InlineData("{}", "{ 'sub.n': -1 }")]
    [InlineData("{ num: { $lt: 10 } }", "{ city: 1, _id: 1 }")]
    public void SortedWindowMatches(string filter, string sort)
    {
        foreach (var proj in new[] { null, "{ city: 1 }", "{ sub: 0 }" })
        foreach (var (skip, limit) in new[] { (0, (int?)null), (7, (int?)11), (295, (int?)10) })
        {
            var o = new FindOptions { Sort = Document.Parse(sort), Projection = proj is null ? null : Document.Parse(proj), Skip = skip, Limit = limit };
            Assert.Equal(Bytes(_raw.Find(filter, o)), Bytes(_idx.Find(filter, o)));
        }
    }

    [Fact]
    public void ExplainReportsCoverage()
    {
        Assert.Contains("covered", _idx.Explain("{ city: 'c1' }"));
        Assert.Contains("covered", _idx.Explain("{ _id: { $gt: 5 } }"));
        Assert.DoesNotContain("covered", _idx.Explain("{ tags: 't1' }"));
        Assert.DoesNotContain("covered", _idx.Explain("{ city: 'c1', num: 3 }"));
    }

    [Fact]
    public void CoveredNumericKeysKeepOriginalType()
    {
        var docs = _idx.Find("{ num: 4 }", new FindOptions { Projection = Document.Parse("{ num: 1, _id: 0 }") });
        Assert.Contains(docs, d => d["num"].Type == DocType.Int64);
        Assert.Contains(docs, d => d["num"].Type == DocType.Decimal);
    }

    [Fact]
    public void RawProjectionMatchesDocumentProjection()
    {
        // Duplicate top-level field and array-index paths.
        var c = _db.GetCollection("dup");
        var bytes = DocumentSerializer.Serialize(new Document { ["_id"] = 1, ["a"] = new DocArray { 10, new Document { ["b"] = 2 } } });
        c.Insert(Document.FromBytes(bytes));
        foreach (var p in new[] { "{ 'a.0': 1 }", "{ 'a.1.b': 1, _id: 0 }", "{ 'a.5': 1 }", "{ a: 0 }", "{ 'a.1.b': 0 }" })
        {
            var expected = c.Find((Document?)null, new FindOptions { Projection = Document.Parse(p) });
            var viaSort = c.Find((Document?)null, new FindOptions { Projection = Document.Parse(p), Sort = Document.Parse("{ _id: 1 }") });
            Assert.Equal(Bytes(expected), Bytes(viaSort));
        }
    }

    [Fact]
    public void IndexSkipKeepsIndexesConsistent()
    {
        _idx.UpdateMany("{ num: { $lt: 5 } }", "{ $set: { other: 'x' } }");        // no indexed field changed
        _idx.UpdateMany("{ city: 'c2' }", "{ $set: { city: 'c9', 'sub.n': 0 } }"); // top-level city changes
        _idx.UpdateMany("{ 'sub.k': 'k1' }", "{ $set: { 'sub.k': 'k7' } }");       // nested index under changed 'sub'
        _idx.UpdateMany("{ tags: 't2' }", "{ $push: { tags: 'new' } }");
        _raw.UpdateMany("{ num: { $lt: 5 } }", "{ $set: { other: 'x' } }");
        _raw.UpdateMany("{ city: 'c2' }", "{ $set: { city: 'c9', 'sub.n': 0 } }");
        _raw.UpdateMany("{ 'sub.k': 'k1' }", "{ $set: { 'sub.k': 'k7' } }");
        _raw.UpdateMany("{ tags: 't2' }", "{ $push: { tags: 'new' } }");
        _db.CheckIntegrity();
        foreach (var f in new[] { "{ city: 'c2' }", "{ city: 'c9' }", "{ 'sub.k': 'k1' }", "{ 'sub.k': 'k7' }", "{ tags: 'new' }", "{ other: 'x' }" })
        {
            Assert.Equal(_raw.Count(f), _idx.Count(f));
            Assert.Equal(Bytes(_raw.Find(f, new() { Sort = Document.Parse("{ _id: 1 }") })), Bytes(_idx.Find(f, new() { Sort = Document.Parse("{ _id: 1 }") })));
        }
    }
}
