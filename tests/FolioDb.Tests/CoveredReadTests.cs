namespace FolioDb.Tests;

/// <summary>Covered plans skip re-evaluating the filter and scan pre-sorted keys; results must equal an unindexed scan.</summary>
public class CoveredReadTests
{
    private static IEnumerable<Document> Docs() =>
    [
        new() { ["_id"] = 0, ["v"] = 1 },
        new() { ["_id"] = 1, ["v"] = 1L },
        new() { ["_id"] = 2, ["v"] = 1.0 },
        new() { ["_id"] = 3, ["v"] = 1m },
        new() { ["_id"] = 4, ["v"] = 1.5 },
        new() { ["_id"] = 5, ["v"] = 1.10m },
        new() { ["_id"] = 6, ["v"] = 3m },
        new() { ["_id"] = 7, ["v"] = "1" },
        new() { ["_id"] = 8, ["v"] = new Document { ["a"] = 1 } },
        new() { ["_id"] = 9 },
        new() { ["_id"] = 10, ["v"] = 9_007_199_254_740_993L },
        new() { ["_id"] = 11, ["v"] = 9_007_199_254_740_992.0 },
        new() { ["_id"] = 12, ["v"] = true },
    ];

    private static readonly Document[] Filters =
    [
        Document.Parse("{v:1}"),
        new() { ["v"] = 1.0 },
        new() { ["v"] = 1.1m },
        new() { ["v"] = "1" },
        new() { ["v"] = new Document { ["a"] = 1 } },
        new() { ["v"] = 9_007_199_254_740_993L },
        new() { ["v"] = 9_007_199_254_740_992.0 },
        new() { ["v"] = new Document { ["$in"] = new DocArray { 3m, 1.5, 1, 1.0, 1m, 3, "1", 1L } } },
        new() { ["_id"] = new Document { ["$in"] = new DocArray { 7, 2, 2.0, 11, 7m, 100 } } },
        Document.Parse("{v:{$gt:1,$lte:3}}"),
        Document.Parse("{v:{$gte:'0'}}"),
        Document.Parse("{v:{$lt:2}}"),
        Document.Parse("{_id:{$gt:4,$lt:9}}"),
        Document.Parse("{v:null}"),
        Document.Parse("{v:1,_id:{$lt:3}}"),
        Document.Parse("{}"),
    ];

    private static (List<int> Find, List<int> Visit, long Count) Read(Collection c, Document filter)
    {
        var prepared = PreparedFilter.FromDocument(filter);
        var visited = new List<int>();
        long n = c.Visit(prepared, d =>
        {
            Assert.True(d.TryGetValue("_id", out var id));
            visited.Add(id.AsInt32);
            return true;
        });
        Assert.Equal(visited.Count, n);
        Assert.Equal(visited, c.Find(filter).Select(d => d["_id"].AsInt32));
        return (c.Find(prepared).Select(d => d["_id"].AsInt32).ToList(), visited, c.Count(prepared));
    }

    private static void AssertSameAsReference(Collection indexed, Collection reference)
    {
        foreach (var filter in Filters)
        {
            var expected = Read(reference, filter);
            var actual = Read(indexed, filter);
            Assert.Equal(expected.Find.Order(), actual.Find.Order());
            Assert.Equal(expected.Visit.Order(), actual.Visit.Order());
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(actual.Find, actual.Visit);
        }
    }

    [Fact]
    public void Covered_plans_match_an_unindexed_scan_across_numeric_types_and_type_brackets()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var indexed = db.GetCollection("indexed");
        var reference = db.GetCollection("reference");
        indexed.InsertMany(Docs());
        reference.InsertMany(Docs());
        indexed.CreateIndex("v");

        Assert.EndsWith("covered", indexed.Explain(Document.Parse("{v:1}")));
        Assert.EndsWith("covered", indexed.Explain(Document.Parse("{v:{$gt:1,$lte:3}}")));
        Assert.StartsWith("COLLSCAN", reference.Explain(Document.Parse("{v:1}")));
        AssertSameAsReference(indexed, reference);

        // Two equal encodings (numbers compare across types) collapse into one ascending key.
        var inPlan = indexed.Explain(Filters[7]);
        Assert.Contains("(4 keys)", inPlan);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 6, 7 }, Read(indexed, Filters[7]).Visit.Order());
        Assert.Contains("(4 keys)", indexed.Explain(Filters[8]));
        Assert.Equal(new[] { 2, 7, 11 }, Read(indexed, Filters[8]).Visit);
    }

    [Fact]
    public void Covered_compound_and_descending_scans_preserve_sorted_projected_windows()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var indexed = db.GetCollection("indexed");
        var reference = db.GetCollection("reference");
        var docs = Enumerable.Range(0, 40).Select(i => new Document
        {
            ["_id"] = i, ["a"] = i % 2, ["nested"] = new Document { ["b"] = i },
        }).ToList();
        indexed.InsertMany(docs);
        reference.InsertMany(docs);
        indexed.CreateIndex(new Document { ["a"] = 1, ["nested.b"] = -1 });
        var filter = PreparedFilter.Parse("{a:1,'nested.b':{$gte:5,$lt:30}}");
        Assert.EndsWith("covered", indexed.Explain(filter));
        var options = new FindOptions
        {
            Sort = new Document { ["_id"] = -1 }, Skip = 2, Limit = 5,
            Projection = new Document { ["nested.b"] = 1 },
        };
        Assert.Equal(reference.Find(filter, options).Select(d => d.ToJson()),
            indexed.Find(filter, options).Select(d => d.ToJson()));
        var actual = new List<int>();
        indexed.Visit(filter, d =>
        {
            d.TryGetValue("_id", out var id);
            actual.Add(id.AsInt32);
            return true;
        });
        Assert.Equal(reference.Find(filter).Select(d => d["_id"].AsInt32).Order(), actual.Order());
        Assert.Equal(1, indexed.Visit(filter, static _ => false));
    }

    [Fact]
    public void Plans_stop_being_covered_when_an_index_becomes_multikey_in_a_transaction_or_snapshot()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var indexed = db.GetCollection("indexed");
        var reference = db.GetCollection("reference");
        indexed.InsertMany(Docs());
        reference.InsertMany(Docs());
        indexed.CreateIndex("v");

        using var snapshot = db.BeginSnapshot();
        using (var tx = db.BeginTransaction())
        {
            var ti = tx.GetCollection("indexed");
            var tr = tx.GetCollection("reference");
            foreach (var c in new[] { ti, tr })
            {
                c.Insert(new Document { ["_id"] = 20, ["v"] = new DocArray { 1.5, 3 } });
                c.Insert(new Document { ["_id"] = 21, ["v"] = new DocArray { 0, 5 } });
            }
            Assert.DoesNotContain("covered", ti.Explain(Document.Parse("{v:{$gt:1,$lte:3}}")));
            AssertSameAsReference(ti, tr);
            // Different elements may satisfy each bound: [0, 5] matches although no single element is in (1, 3].
            Assert.Contains(21, Read(ti, Document.Parse("{v:{$gt:1,$lte:3}}")).Visit);
            tx.Commit();
        }
        AssertSameAsReference(indexed, reference);

        var si = snapshot.GetCollection("indexed");
        Assert.EndsWith("covered", si.Explain(Document.Parse("{v:1}")));
        AssertSameAsReference(si, snapshot.GetCollection("reference"));
    }
}
