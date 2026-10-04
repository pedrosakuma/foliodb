namespace FolioDb.Tests;

public class IndexOrderedSortTests
{
    private static IEnumerable<Document> Docs() => Enumerable.Range(0, 3000).Select(i =>
    {
        var d = new Document { ["_id"] = i, ["pad"] = new string('x', 60) };
        if (i % 11 == 0) d["v"] = DocValue.Null;
        else if (i % 13 != 0) d["v"] = i % 5 == 0 ? (DocValue)("s" + (i % 3)) : (DocValue)(i % 17);
        return d;
    });

    private static void Compare(Collection plain, Collection indexed)
    {
        foreach (int dir in new[] { 1, -1 })
            foreach (var (skip, limit) in new[] { (0, 1), (0, 10), (5, 20), (0, 3000), (2990, 50), (0, 0) })
            {
                var o = new FindOptions { Sort = new Document { ["v"] = dir }, Skip = skip, Limit = limit };
                Assert.Equal(plain.Find((Document?)null, o).Select(d => d["_id"]), indexed.Find((Document?)null, o).Select(d => d["_id"]));
            }
    }

    [Fact]
    public void Index_ordered_sort_matches_collection_sort()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var plain = db.GetCollection("plain");
        var indexed = db.GetCollection("indexed");
        plain.InsertMany(Docs());
        indexed.CreateIndex("v");
        indexed.InsertMany(Docs());
        Compare(plain, indexed);

        foreach (var c in new[] { plain, indexed })
        {
            c.UpdateMany(new Document { ["_id"] = new Document { ["$lt"] = 300 } }, new Document { ["$unset"] = new Document { ["v"] = 1 } });
            c.UpdateMany(new Document { ["_id"] = new Document { ["$gte"] = 300, ["$lt"] = 600 } }, new Document { ["$set"] = new Document { ["v"] = 4 } });
            c.DeleteMany(new Document { ["_id"] = new Document { ["$gte"] = 2500 } });
        }
        Compare(plain, indexed);
    }

    [Fact]
    public void Index_created_after_the_data_and_rebuilt_orders_missing_fields_too()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var plain = db.GetCollection("plain");
        var indexed = db.GetCollection("indexed");
        plain.InsertMany(Docs());
        indexed.InsertMany(Docs());
        indexed.CreateIndex("v");
        Compare(plain, indexed);
        Assert.True(indexed.RebuildIndex("v_1"));
        Compare(plain, indexed);
    }

    [Fact]
    public void Missing_fields_are_not_found_by_typed_index_queries()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        c.CreateIndex("v");
        c.InsertMany(Docs());
        var plain = db.GetCollection("p");
        plain.InsertMany(Docs());
        foreach (var f in new[] { new Document { ["v"] = new Document { ["$lt"] = 100 } }, new Document { ["v"] = new Document { ["$gte"] = 0 } }, new Document { ["v"] = 3 }, new Document { ["v"] = new Document { ["$exists"] = false } } })
            Assert.Equal(plain.Find(f).Select(d => (long)d["_id"].AsInt64).Order(), c.Find(f).Select(d => (long)d["_id"].AsInt64).Order());
    }
}
