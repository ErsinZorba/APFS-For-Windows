namespace Apfs.Util;

/// <summary>
/// İş parçacığı güvenli, ağırlık sınırlı LRU önbellek. Disk salt okunur açıldığı için önbelleğe alınan değerler hiç bayatlamaz;
/// sınır yalnızca bellek kullanımını tutmak içindir.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly Lock _lock = new();
    private readonly long _capacity;
    private readonly Func<TValue, long> _weight;
    private long _total;

    private readonly record struct Entry(TKey Key, TValue Value, long Weight);

    /// <param name="capacity">Toplam ağırlık sınırı (ağırlık fonksiyonu verilmezse girdi sayısı).</param>
    public LruCache(long capacity, Func<TValue, long>? weight = null)
    {
        _capacity = capacity;
        _weight = weight ?? (_ => 1);
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        long w = Math.Max(1, _weight(value));
        if (w > _capacity) return; // tek başına sınırı aşan değer önbelleğe alınmaz
        lock (_lock)
        {
            if (_map.ContainsKey(key)) return;
            _map[key] = _lru.AddFirst(new Entry(key, value, w));
            _total += w;
            while (_total > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _total -= last.Value.Weight;
            }
        }
    }

    /// <summary>Girdiyi düşürür (yalnızca yazma oturumunun görüntüsü kullanır: değişen kayıtların önbelleği).</summary>
    public void Remove(TKey key)
    {
        lock (_lock)
        {
            if (!_map.Remove(key, out var node)) return;
            _lru.Remove(node);
            _total -= node.Value.Weight;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _total = 0;
        }
    }

    /// <summary>Önbellekte yoksa üretir ve ekler. Üretici kilit dışında çalışır (aynı anahtar iki kez üretilebilir; zararsız).</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        if (TryGet(key, out var v)) return v;
        v = factory(key);
        Add(key, v);
        return v;
    }
}
