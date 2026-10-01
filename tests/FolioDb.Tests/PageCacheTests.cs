using FolioDb.Storage;

namespace FolioDb.Tests;

public sealed class PageCacheTests
{
    private static byte[] Page(long key) => BitConverter.GetBytes(key);

    [Fact]
    public void NeverExceedsCapacity()
    {
        var cache = new PageCache(16);
        for (long k = 0; k < 1000; k++)
        {
            cache.Add(k, Page(k));
            Assert.True(cache.Count <= 16);
            Assert.True(cache.TryGet(k, out var data));
            Assert.Equal(k, BitConverter.ToInt64(data));
        }
        Assert.Equal(16, cache.Count);
    }

    [Fact]
    public void ReferencedEntriesGetASecondChance()
    {
        var cache = new PageCache(4);
        for (long k = 0; k < 4; k++) cache.Add(k, Page(k));
        Assert.True(cache.TryGet(0, out _));
        Assert.True(cache.TryGet(2, out _));

        cache.Add(10, Page(10));
        cache.Add(11, Page(11));

        Assert.True(cache.TryGet(0, out _));
        Assert.True(cache.TryGet(2, out _));
        Assert.False(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(3, out _));
    }

    [Fact]
    public void AllReferencedStillEvictsOne()
    {
        var cache = new PageCache(4);
        for (long k = 0; k < 4; k++) cache.Add(k, Page(k));
        for (long k = 0; k < 4; k++) Assert.True(cache.TryGet(k, out _));
        cache.Add(99, Page(99));
        Assert.Equal(4, cache.Count);
        Assert.True(cache.TryGet(99, out _));
    }

    [Fact]
    public void AddingAnExistingKeyReplacesItsImage()
    {
        var cache = new PageCache(4);
        cache.Add(1, Page(1));
        cache.Add(1, Page(42));
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(1, out var data));
        Assert.Equal(42, BitConverter.ToInt64(data));
    }

    [Fact]
    public void ClearDropsEverythingAndStaysUsable()
    {
        var cache = new PageCache(4);
        for (long k = 0; k < 4; k++) cache.Add(k, Page(k));
        cache.Clear();
        Assert.Equal(0, cache.Count);
        for (long k = 0; k < 4; k++) Assert.False(cache.TryGet(k, out _));
        for (long k = 10; k < 20; k++) cache.Add(k, Page(k));
        Assert.Equal(4, cache.Count);
    }

    [Fact]
    public async Task ConcurrentHitsAndInsertsStayConsistent()
    {
        var cache = new PageCache(64);
        var token = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, Math.Max(4, Environment.ProcessorCount)).Select(t => Task.Run(() =>
        {
            var rnd = new Random(t);
            for (int i = 0; i < 50_000; i++)
            {
                long key = rnd.Next(256);
                if (cache.TryGet(key, out var data)) Assert.Equal(key, BitConverter.ToInt64(data));
                else cache.Add(key, Page(key));
            }
        }, token)).ToArray();
        await Task.WhenAll(tasks);
        Assert.InRange(cache.Count, 1, 64);
    }
}
