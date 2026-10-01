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
    public void Removed_entries_free_their_slot_without_evicting_a_newer_entry_under_the_same_key()
    {
        var cache = new PageCache(4);
        cache.Add(1, [1]);
        cache.Add(2, [2]);
        cache.Remove(1);
        Assert.False(cache.TryGet(1, out _));
        cache.Add(3, [3]);
        cache.Add(4, [4]);
        cache.Add(1, [10]); // the ring still holds the removed entry for key 1
        Assert.True(cache.TryGet(1, out var one));
        Assert.Equal(10, one[0]);
        // Sweeping the removed entry must not take the new one with it.
        for (long k = 100; k < 103; k++) cache.Add(k, [0]);
        Assert.True(cache.Count <= 4);
        int mapped = 0;
        foreach (long k in new long[] { 1, 2, 3, 4, 100, 101, 102 }) if (cache.TryGet(k, out _)) mapped++;
        Assert.Equal(cache.Count, mapped);
    }
}
