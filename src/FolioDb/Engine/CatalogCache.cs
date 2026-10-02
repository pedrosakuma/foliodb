using System.Collections.Concurrent;

namespace FolioDb.Engine;

/// <summary>
/// Decoded collection metadata shared by read-only transactions, so a point read does not look up and decode the
/// catalog every time. Validated by a sequence lock: the state counts the catalog commits in flight (high bits) and
/// every start and end of one (low bits). A commit starts before its frames can become visible and ends once they are,
/// and commits can overlap (the write lock is released before a commit waits for durability). A reader may use the
/// cache only if it saw the same state, with none in flight, before and after taking its snapshot: its snapshot then
/// holds exactly that state's catalog. Writers never read from it: they mutate their metadata in place.
/// </summary>
internal sealed class CatalogCache
{
    private const int MaxEntries = 1024;
    private const long InFlight = 1L << 40;

    private long _generation;
    private readonly ConcurrentDictionary<string, (long Generation, CollectionMeta Meta)> _entries = new(StringComparer.Ordinal);

    public long Generation => Volatile.Read(ref _generation);

    /// <summary>The generation a snapshot taken after reading <paramref name="before"/> can use, or -1.</summary>
    public long Validate(long before)
    {
        // The snapshot's reads must not move past this one.
        Interlocked.MemoryBarrier();
        return before < InFlight && Volatile.Read(ref _generation) == before ? before : -1;
    }

    public bool TryGet(string name, long generation, out CollectionMeta? meta)
    {
        if (_entries.TryGetValue(name, out var entry) && entry.Generation == generation)
        {
            meta = entry.Meta;
            return true;
        }
        meta = null;
        return false;
    }

    public void Store(string name, long generation, CollectionMeta meta)
    {
        if (_entries.Count < MaxEntries || _entries.ContainsKey(name)) _entries[name] = (generation, meta);
    }

    internal Action? TestAfterBeginChange, TestBeforeEndChange;

    public void BeginChange()
    {
        Interlocked.Add(ref _generation, InFlight + 1);
        TestAfterBeginChange?.Invoke();
    }

    public void EndChange()
    {
        TestBeforeEndChange?.Invoke();
        Interlocked.Add(ref _generation, 1 - InFlight);
    }
}
