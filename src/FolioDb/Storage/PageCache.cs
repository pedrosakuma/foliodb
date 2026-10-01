using System.Collections.Concurrent;

namespace FolioDb.Storage;

/// <summary>
/// Thread-safe CLOCK cache of immutable page images. Hits are lock-free (a concurrent dictionary lookup plus a
/// reference bit that is only written when clear); insertions and eviction serialize on a lock.
/// </summary>
internal sealed class PageCache
{
    private sealed class Entry(long key, byte[] data)
    {
        public readonly long Key = key;
        public volatile byte[] Data = data;
        public volatile bool Referenced;
    }

    private readonly ConcurrentDictionary<long, Entry> _map;
    private readonly Entry?[] _ring;
    private int _hand;
    private readonly Lock _lock = new();

    public PageCache(int capacity)
    {
        _ring = new Entry?[capacity];
        _map = new ConcurrentDictionary<long, Entry>(Environment.ProcessorCount, capacity);
    }

    internal int Count => _map.Count;

    public bool TryGet(long key, out byte[] data)
    {
        if (_map.TryGetValue(key, out var entry))
        {
            // Avoid dirtying a shared cache line on every hit.
            if (!entry.Referenced) entry.Referenced = true;
            data = entry.Data;
            return true;
        }
        data = null!;
        return false;
    }

    public void Add(long key, byte[] data)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Data = data;
                return;
            }

            // Each sweep step either clears a reference bit or stops, so this finds a slot within two revolutions.
            int hand = _hand;
            while (true)
            {
                var victim = _ring[hand];
                if (victim is null) break;
                if (!victim.Referenced)
                {
                    _map.TryRemove(victim.Key, out _);
                    break;
                }
                victim.Referenced = false;
                hand = hand + 1 == _ring.Length ? 0 : hand + 1;
            }

            var entry = new Entry(key, data);
            _ring[hand] = entry;
            _map[key] = entry;
            _hand = hand + 1 == _ring.Length ? 0 : hand + 1;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            Array.Clear(_ring);
            _hand = 0;
        }
    }
}
