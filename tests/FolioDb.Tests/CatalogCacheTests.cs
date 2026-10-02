namespace FolioDb.Tests;

public class CatalogCacheTests
{
    private static string[] Indexes(Collection c) => c.GetIndexes().Where(i => i.Field != "_id").Select(i => i.Field).Order().ToArray();

    [Fact]
    public void Readers_see_catalog_changes_as_soon_as_they_commit()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("docs");
        c.Insert(new Document { ["_id"] = 1, ["a"] = 1 });
        Assert.Empty(Indexes(c));

        c.CreateIndex("a");
        Assert.Equal(["a"], Indexes(c));
        Assert.False(c.GetIndexes().Single(i => i.Field == "a").MultiKey);
        c.Insert(new Document { ["_id"] = 2, ["a"] = new DocArray { 1, 2 } });
        Assert.True(c.GetIndexes().Single(i => i.Field == "a").MultiKey);
        c.DropIndex("a");
        Assert.Empty(Indexes(c));

        Assert.NotNull(c.FindById(1));
        Assert.True(db.DropCollection("docs"));
        Assert.Null(c.FindById(1));
        Assert.Equal(0, c.Count());
    }

    [Fact]
    public void A_snapshot_keeps_the_catalog_it_started_with()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("docs");
        c.Insert(new Document { ["_id"] = 1, ["a"] = 1 });
        using var before = db.BeginSnapshot();
        c.CreateIndex("a");
        Assert.Equal(["a"], Indexes(c)); // caches the new catalog
        Assert.Empty(Indexes(before.GetCollection("docs")));

        Assert.Empty(Indexes(before.GetCollection("docs"))); // caches the old one
        Assert.Equal(["a"], Indexes(c));
    }

    [Fact]
    public void A_catalog_change_while_a_reader_starts_is_not_hidden_by_the_cache()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("docs");
        c.Insert(new Document { ["_id"] = 1, ["a"] = 1 });
        Assert.Empty(Indexes(c));

        // Between the reader's first look at the generation and its snapshot.
        db.TestBeforeReadSnapshot = () =>
        {
            db.TestBeforeReadSnapshot = null;
            c.CreateIndex("a");
        };
        Assert.Equal(["a"], Indexes(c));
    }

    [Fact]
    public void Readers_during_a_catalog_commit_do_not_share_what_they_decoded()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("docs");
        c.Insert(new Document { ["_id"] = 1, ["a"] = 1 });
        Assert.Empty(Indexes(c));

        string[]? early = null, late = null;
        db.Catalog.TestAfterBeginChange = () => early = Indexes(c); // before the frames are visible
        db.Catalog.TestBeforeEndChange = () => late = Indexes(c);   // after
        c.CreateIndex("a");
        db.Catalog.TestAfterBeginChange = db.Catalog.TestBeforeEndChange = null;

        Assert.Empty(early!);
        Assert.Equal(["a"], late!);
        Assert.Equal(["a"], Indexes(c));
    }

    [Fact]
    public void Uncommitted_catalog_changes_do_not_leak_into_the_cache()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("docs");
        c.Insert(new Document { ["_id"] = 1, ["a"] = 1 });
        Assert.Empty(Indexes(c));
        using (var tx = db.BeginTransaction())
        {
            tx.GetCollection("docs").CreateIndex("a");
            Assert.Empty(Indexes(c));
        }
        Assert.Empty(Indexes(c));
    }

    [Fact]
    public async Task Overlapping_catalog_commits_do_not_validate_the_cache()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("docs");
        c.Insert(new Document { ["_id"] = 1, ["a"] = 1, ["b"] = 1 });
        Assert.Empty(Indexes(c));

        // Commit A releases the write lock and then stalls before ending its change; commit B runs meanwhile.
        using var inA = new ManualResetEventSlim();
        using var releaseA = new ManualResetEventSlim();
        int a = Environment.CurrentManagedThreadId;
        string[]? early = null, late = null;
        db.Catalog.TestBeforeEndChange = () =>
        {
            if (Environment.CurrentManagedThreadId == a) return;
            inA.Set();
            releaseA.Wait(TimeSpan.FromSeconds(10));
        };
        var token = TestContext.Current.CancellationToken;
        var commitA = Task.Factory.StartNew(() => c.CreateIndex("a"), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(inA.Wait(TimeSpan.FromSeconds(10), token));

        db.Catalog.TestAfterBeginChange = () => early = Indexes(c); // B's frames are not visible yet
        db.Catalog.TestBeforeEndChange = () =>
        {
            if (Environment.CurrentManagedThreadId == a) late = Indexes(c);
        };
        c.CreateIndex("b");
        releaseA.Set();
        await commitA.WaitAsync(TimeSpan.FromSeconds(10), token);
        db.Catalog.TestAfterBeginChange = db.Catalog.TestBeforeEndChange = null;

        Assert.Equal(["a"], early!);
        Assert.Equal(["a", "b"], late!);
        Assert.Equal(["a", "b"], Indexes(c));
    }
}
