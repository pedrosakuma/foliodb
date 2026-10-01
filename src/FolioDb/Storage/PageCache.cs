using System.Collections.Concurrent;

namespace FolioDb.Storage;

/// <summary>
/// Thread-safe CLOCK cache of immutable page images. Hits are lock-free (a concurrent dictionary lookup plus a
/// reference bit that is only written when clear); insertions and eviction serialize on a lock.
/// <para>
/// With a <see cref="ReaderEpoch"/>, evicted page images are recycled instead of left to the GC: they wait in a limbo
/// list until every reader that might still hold one has finished (see <see cref="ReaderEpoch"/>), then go to a small
/// free pool that <see cref="RentPage"/> serves cache misses from. Images displaced by a newer copy under the same key,
/// or dropped by <see cref="Clear"/>, are never recycled.
/// </para>
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

    private readonly ReaderEpoch? _epoch;
    private readonly int _pageSize;
    private readonly int _batch;
    private readonly int _poolCap;
    private List<byte[]> _limbo = [];
    private List<byte[]> _pending = [];
    private long _pendingEpoch;
    private readonly Stack<byte[]> _free = new();

    /// <summary>Test hook: overwrite recycled images before reuse, so a reader still holding one sees garbage.</summary>
    internal static bool PoisonRecycled;

    public PageCache(int capacity, ReaderEpoch? epoch = null, int pageSize = 0)
    {
        _ring = new Entry?[capacity];
        _map = new ConcurrentDictionary<long, Entry>(Environment.ProcessorCount, capacity);
        _epoch = epoch;
        _pageSize = pageSize;
        _batch = Math.Clamp(capacity / 16, 1, 256);
        _poolCap = _batch * 4;
    }

    internal int Count => _map.Count;

    internal int PooledCount { get { lock (_lock) return _free.Count; } }

    /// <summary>A page-sized array with unspecified contents, reused from evicted images when possible.</summary>
    public byte[] RentPage()
    {
        if (_epoch is not null)
        {
            lock (_lock)
            {
                if (_free.TryPop(out var page)) return page;
            }
        }
        return GC.AllocateUninitializedArray<byte>(_pageSize);
    }

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
                    if (_epoch is not null) Retire(victim.Data);
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

    // Called under _lock, after the image was removed from the map.
    private void Retire(byte[] data)
    {
        var epoch = _epoch!;
        if (_pending.Count > 0 && epoch.Drained(_pendingEpoch))
        {
            foreach (var page in _pending)
            {
                if (_free.Count >= _poolCap) break;
                if (PoisonRecycled) page.AsSpan().Fill(0xDB);
                _free.Push(page);
            }
            _pending.Clear();
        }
        if (data.Length == _pageSize && _limbo.Count < _poolCap) _limbo.Add(data);
        // Only one batch waits for a grace period at a time, so at most two epochs have readers.
        if (_pending.Count == 0 && _limbo.Count >= _batch)
        {
            _pendingEpoch = epoch.Current;
            epoch.Advance();
            (_limbo, _pending) = (_pending, _limbo);
        }
    }

    /// <summary>
    /// After a checkpoint copied the WAL into the main file and reset it: cached images of each page's latest frame
    /// stay cached as that page's main-file image (<paramref name="mainFileKey"/>), and every other WAL frame entry,
    /// plus any older main-file image of those pages, is dropped. Keys of WAL frames are positive; main-file keys
    /// negative. Must run while no reader is registered; dropped images are left to the GC, never recycled.
    /// </summary>
    public void PromoteCheckpointed(IReadOnlyCollection<(uint Pgno, long LatestFrame)> pages, Func<uint, long> mainFileKey)
    {
        lock (_lock)
        {
            foreach (var (pgno, _) in pages) _map.TryRemove(mainFileKey(pgno), out _);
            var promoted = new Dictionary<long, long>(pages.Count);
            foreach (var (pgno, frame) in pages) promoted[frame] = mainFileKey(pgno);
            for (int i = 0; i < _ring.Length; i++)
            {
                var entry = _ring[i];
                if (entry is null) continue;
                if (entry.Key >= 0)
                {
                    _map.TryRemove(entry.Key, out _);
                    if (promoted.TryGetValue(entry.Key, out long key))
                    {
                        var moved = new Entry(key, entry.Data) { Referenced = entry.Referenced };
                        _ring[i] = moved;
                        _map[key] = moved;
                    }
                    else _ring[i] = null;
                }
                else if (!_map.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
                {
                    _ring[i] = null; // a stale main-file image removed above
                }
            }
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
