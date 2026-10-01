using FolioDb.Storage;

namespace FolioDb.Tests;

public sealed class CheckpointCacheTests
{
    [Fact]
    public void Checkpoint_keeps_the_cache_warm_with_the_latest_images()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { CacheSizePages = 4096, AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Off });
        var c = db.GetCollection("docs");
        c.CreateIndex("v");
        for (int i = 0; i < 500; i++) c.Insert(new Document { ["_id"] = i, ["v"] = 0, ["pad"] = new string('x', 100) });
        Assert.True(db.Checkpoint());
        for (int i = 0; i < 500; i++) Assert.NotNull(c.FindById(i)); // main-file images cached

        // Several versions of the same pages in the WAL; only the latest may survive the checkpoint.
        for (int round = 1; round <= 3; round++)
            for (int i = 0; i < 500; i += 3) c.UpdateOne(new Document { ["_id"] = i }, Document.Parse($"{{ $set: {{ v: {round} }} }}"));
        Assert.True(db.Checkpoint());
        Assert.Equal(0, db.Pager.WalFrameCount);
        int cachedAfter = db.Pager.CachedPages;
        Assert.True(cachedAfter > 0);

        for (int i = 0; i < 500; i++) Assert.Equal(i % 3 == 0 ? 3 : 0, c.FindById(i)!["v"].AsInt32);
        Assert.Equal(167, c.Count("""{ "v": 3 }"""));
        Assert.Equal(333, c.Count("""{ "v": 0 }"""));
        // Every page was already cached under its main-file key: reading everything added nothing.
        Assert.Equal(cachedAfter, db.Pager.CachedPages);
        db.CheckIntegrity();

        // The same must hold for a fresh handle that reads everything from disk.
        db.Dispose();
        using var reopened = tmp.Open();
        var r = reopened.GetCollection("docs");
        for (int i = 0; i < 500; i++) Assert.Equal(i % 3 == 0 ? 3 : 0, r.FindById(i)!["v"].AsInt32);
    }

    [Fact]
    public void Promotion_replaces_stale_main_file_images_and_drops_older_frames()
    {
        var cache = new PageCache(16);
        static long Main(uint pgno) => -(long)pgno - 1;
        cache.Add(Main(1), [1]);   // stale main-file image of page 1
        cache.Add(Main(2), [2]);   // page 2 untouched by the WAL
        cache.Add(Main(3), [3]);   // stale, and its latest frame is not cached
        cache.Add(1, [10]);        // page 1, older frame
        cache.Add(2, [11]);        // page 1, latest frame
        cache.Add(3, [30]);        // page 3, older frame

        cache.PromoteCheckpointed([(1u, 2L), (3u, 4L)], Main);

        Assert.True(cache.TryGet(Main(1), out var p1));
        Assert.Equal(11, p1[0]);
        Assert.True(cache.TryGet(Main(2), out var p2));
        Assert.Equal(2, p2[0]);
        Assert.False(cache.TryGet(Main(3), out _));
        for (long frame = 1; frame <= 4; frame++) Assert.False(cache.TryGet(frame, out _));
        Assert.Equal(2, cache.Count);

        // Freed slots are reused without exceeding capacity.
        for (long k = 100; k < 140; k++) cache.Add(k, [0]);
        Assert.Equal(16, cache.Count);
    }
}
