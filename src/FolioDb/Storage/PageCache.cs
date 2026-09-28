namespace FolioDb.Storage;

/// <summary>Thread-safe LRU cache of immutable page images.</summary>
internal sealed class PageCache
{
    private readonly int _capacity;
    private readonly Dictionary<long, LinkedListNode<(long Key, byte[] Data)>> _map;
    private readonly LinkedList<(long Key, byte[] Data)> _lru = new();
    private readonly Lock _lock = new();

    public PageCache(int capacity)
    {
        _capacity = capacity;
        _map = new Dictionary<long, LinkedListNode<(long, byte[])>>(capacity);
    }

    public bool TryGet(long key, out byte[] data)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (node != _lru.First)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                }
                data = node.Value.Data;
                return true;
            }
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
                existing.Value = (key, data);
                return;
            }
            if (_map.Count >= _capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
            _map[key] = _lru.AddFirst((key, data));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
        }
    }
}
