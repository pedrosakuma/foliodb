namespace FolioDb.Tests;

public sealed class ScanResistanceTests
{
    private static void Fill(FolioDatabase db, string name, int count)
    {
        using var tx = db.BeginTransaction();
        var c = tx.GetCollection(name);
        for (int i = 0; i < count; i++) c.Insert(new Document { ["_id"] = i, ["pad"] = new string('x', 300) });
        tx.Commit();
    }

    [Fact]
    public void A_large_scan_admits_a_bounded_number_of_pages()
    {
        using var tmp = new TempDb();
        var options = new FolioOptions { CacheSizePages = 128, Synchronous = SynchronousMode.Off };
        using (var setup = tmp.Open(options)) Fill(setup, "big", 10_000); // ~1000 leaf pages
        using var db = tmp.Open(options); // cold cache
        int threshold = db.Pager.ScanMissThreshold;

        Assert.Equal(0, db.GetCollection("big").Count("""{ "pad": "nope" }"""));
        Assert.InRange(db.Pager.CachedPages, 1, threshold + 8);
    }

    [Fact]
    public void Repeated_scans_of_a_table_that_fits_still_warm_up_the_cache()
    {
        using var tmp = new TempDb();
        var options = new FolioOptions { CacheSizePages = 512, Synchronous = SynchronousMode.Off };
        using (var setup = tmp.Open(options)) Fill(setup, "mid", 2_500); // more pages than one scan admits, fewer than the cache holds
        using var db = tmp.Open(options); // cold cache
        var mid = db.GetCollection("mid");

        mid.Count();
        int afterFirst = db.Pager.CachedPages;
        Assert.InRange(afterFirst, 1, db.Pager.ScanMissThreshold + 8);
        for (int i = 0; i < 10; i++) Assert.Equal(2_500, mid.Find().Count);
        Assert.True(db.Pager.CachedPages > afterFirst * 2, $"cached={db.Pager.CachedPages} afterFirst={afterFirst}");
    }

    [Fact]
    public void Many_lookups_in_one_long_snapshot_keep_warming_the_cache()
    {
        using var tmp = new TempDb();
        var options = new FolioOptions { CacheSizePages = 2048, Synchronous = SynchronousMode.Off };
        using (var setup = tmp.Open(options)) Fill(setup, "docs", 10_000);
        using var db = tmp.Open(options); // cold cache

        // Each lookup misses a page or two; together they miss far more than one operation may admit.
        using (var snap = db.BeginSnapshot())
        {
            var docs = snap.GetCollection("docs");
            for (int i = 0; i < 10_000; i += 7) Assert.NotNull(docs.FindById(i));
        }
        Assert.True(db.Pager.CachedPages > db.Pager.ScanMissThreshold * 3,
            $"cached={db.Pager.CachedPages} threshold={db.Pager.ScanMissThreshold}");

        using (var snap = db.BeginSnapshot())
        {
            int before = db.Pager.CachedPages;
            snap.GetCollection("docs").Count("""{ "pad": "nope" }""");
            Assert.InRange(db.Pager.CachedPages - before, 0, db.Pager.ScanMissThreshold + 8);
        }
    }
}
