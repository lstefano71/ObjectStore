namespace ObjectStore;

/// <summary>
/// SIEVE eviction cache — a FIFO queue with a single "visited" bit per entry.
/// On eviction, scans from hand position; if visited=true, clear and skip;
/// if visited=false, evict. Simpler and often better than LRU for caches.
/// </summary>
public sealed class SieveCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _queue;
    private LinkedListNode<CacheEntry>? _hand;

    public int Count => _map.Count;
    public int Capacity => _capacity;

    public SieveCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<CacheEntry>>(capacity);
        _queue = new LinkedList<CacheEntry>();
    }

    /// <summary>Gets a value from cache. Returns false if not cached.</summary>
    public bool TryGet(TKey key, out TValue? value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            node.Value.Visited = true;
            value = node.Value.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Inserts or updates a key-value pair in the cache.</summary>
    public void Put(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            existing.Value.Value = value;
            existing.Value.Visited = true;
            return;
        }

        // Evict if at capacity
        while (_map.Count >= _capacity)
            Evict();

        var entry = new CacheEntry { Key = key, Value = value, Visited = false };
        var node = _queue.AddLast(entry);
        _map[key] = node;
    }

    /// <summary>Invalidates (removes) a specific key from the cache.</summary>
    public bool Invalidate(TKey key)
    {
        if (_map.TryGetValue(key, out var node))
        {
            if (_hand == node)
                _hand = node.Previous ?? _queue.Last;
            _queue.Remove(node);
            _map.Remove(key);
            return true;
        }
        return false;
    }

    /// <summary>Clears all entries from the cache.</summary>
    public void Clear()
    {
        _map.Clear();
        _queue.Clear();
        _hand = null;
    }

    private void Evict()
    {
        // Start from hand or tail of queue
        var current = _hand ?? _queue.Last;
        if (current == null) return;

        while (true)
        {
            if (current.Value.Visited)
            {
                current.Value.Visited = false;
                current = current.Previous ?? _queue.Last!;
            }
            else
            {
                // Evict this entry
                _hand = current.Previous ?? _queue.Last;
                if (_hand == current) _hand = null;
                _map.Remove(current.Value.Key);
                _queue.Remove(current);
                return;
            }
        }
    }

    private sealed class CacheEntry
    {
        public TKey Key { get; set; } = default!;
        public TValue Value { get; set; } = default!;
        public bool Visited { get; set; }
    }
}
