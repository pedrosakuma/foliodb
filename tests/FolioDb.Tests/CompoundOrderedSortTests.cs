namespace FolioDb.Tests;

public class CompoundOrderedSortTests
{
    private static IEnumerable<Document> Docs() => Enumerable.Range(0, 2400).Select(i =>
    {
        var d = new Document { ["_id"] = i, ["g"] = i % 6, ["h"] = i % 2, ["pad"] = i % 7 };
        if (i % 9 == 0) d["v"] = DocValue.Null;
        else if (i % 13 != 0) d["v"] = i % 5 == 0 ? (DocValue)("s" + (i % 3)) : (DocValue)(i % 17);
        return d;
    });

    public static IEnumerable<object[]> Patterns() =>
    [
        [1, 1], [1, -1], [-1, 1], [-1, -1],
    ];

    [Theory]
    [MemberData(nameof(Patterns))]
    public void Filtered_sort_by_compound_index_matches_unindexed_sort(int indexDir, int sortDir)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var plain = db.GetCollection("plain");
        var indexed = db.GetCollection("indexed");
        plain.InsertMany(Docs());
        indexed.CreateIndex(new Document { ["g"] = 1, ["v"] = indexDir });
        indexed.CreateIndex(new Document { ["g"] = 1, ["h"] = 1, ["v"] = indexDir });
        indexed.InsertMany(Docs());

        var filters = new[]
        {
            new Document { ["g"] = 3 },
            new Document { ["g"] = 4, ["pad"] = new Document { ["$ne"] = 2 } },
            new Document { ["g"] = 1, ["v"] = new Document { ["$gte"] = 4, ["$lt"] = 12 } },
            new Document { ["g"] = 2, ["v"] = new Document { ["$gt"] = 3 } },
            new Document { ["g"] = 5, ["v"] = new Document { ["$lte"] = 9 } },
            new Document { ["g"] = 0, ["h"] = 0 },
            new Document { ["g"] = 99 },
        };
        foreach (var filter in filters)
            foreach (var (skip, limit) in new[] { (0, 1), (0, 10), (4, 7), (0, 0), (390, 20), (0, 5000) })
            {
                var o = new FindOptions { Sort = new Document { ["v"] = sortDir }, Skip = skip, Limit = limit };
                Assert.Equal(plain.Find(filter, o).Select(d => d["_id"]), indexed.Find(filter, o).Select(d => d["_id"]));
            }
    }

    [Fact]
    public void Compound_index_with_arrays_is_not_used_for_ordering()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var plain = db.GetCollection("plain");
        var indexed = db.GetCollection("indexed");
        indexed.CreateIndex(new Document { ["g"] = 1, ["v"] = 1 });
        foreach (var c in new[] { plain, indexed })
            c.InsertMany(Enumerable.Range(0, 300).Select(i => new Document
            {
                ["_id"] = i, ["g"] = i % 3, ["v"] = i % 10 == 0 ? (DocValue)new DocArray { i % 11, 20 + i % 7 } : (DocValue)(i % 11),
            }));
        var f = new Document { ["g"] = 1 };
        foreach (int dir in new[] { 1, -1 })
            foreach (int limit in new[] { 30, 100 })
            {
                var o = new FindOptions { Sort = new Document { ["v"] = dir }, Limit = limit };
                Assert.Equal(plain.Find(f, o).Select(d => d["_id"]), indexed.Find(f, o).Select(d => d["_id"]));
            }
    }

    [Fact]
    public void Override_keeps_tie_order_when_the_index_has_components_after_the_sort_field()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var plain = db.GetCollection("plain");
        var indexed = db.GetCollection("indexed");
        indexed.CreateIndex("g");
        indexed.CreateIndex(new Document { ["g"] = 1, ["v"] = 1, ["w"] = 1 });
        foreach (var c in new[] { plain, indexed })
            c.InsertMany(Enumerable.Range(0, 600).Select(i => new Document { ["_id"] = i, ["g"] = i % 3, ["v"] = i % 4, ["w"] = (i * 7) % 11 }));
        var f = new Document { ["g"] = 1 };
        foreach (int dir in new[] { 1, -1 })
            foreach (var (skip, limit) in new[] { (0, 5), (3, 20) })
            {
                var o = new FindOptions { Sort = new Document { ["v"] = dir }, Skip = skip, Limit = limit };
                Assert.Equal(plain.Find(f, o).Select(d => d["_id"]), indexed.Find(f, o).Select(d => d["_id"]));
            }
    }
}
