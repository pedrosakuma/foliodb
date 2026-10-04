namespace FolioDb.Tests;

public class TopNSortTests
{
    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(1, 0, 10)]
    [InlineData(-1, 0, 10)]
    [InlineData(1, 7, 5)]
    [InlineData(-1, 13, 40)]
    [InlineData(-1, 0, 5000)]
    public void Limited_sort_matches_full_sort_window(int dir, int skip, int limit)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("docs");
        // Many ties, missing fields and mixed types stress the tie-break (scan order).
        c.InsertMany(Enumerable.Range(0, 500).Select(i =>
        {
            var d = new Document { ["_id"] = i, ["g"] = i % 7 };
            if (i % 11 != 0) d["v"] = i % 5 == 0 ? (DocValue)("s" + (i % 3)) : (DocValue)(i % 13);
            return d;
        }));
        var sort = new Document { ["v"] = dir, ["g"] = -dir };
        var filter = new Document { ["g"] = new Document { ["$ne"] = 3 } };
        var all = c.Find(filter, new FindOptions { Sort = sort });
        var got = c.Find(filter, new FindOptions { Sort = sort, Skip = skip, Limit = limit });
        Assert.Equal(all.Skip(skip).Take(limit).Select(d => d["_id"]), got.Select(d => d["_id"]));
    }
}
