namespace TelltaleModding.Core.Scene;

/// <summary>
/// A cache that keeps the most recently used entries within a byte budget, dropping the least
/// recently used first. Sizes come from the size function given (approximate is fine; null
/// values count as nothing). An entry is never dropped by its own insertion, so one value
/// larger than the whole budget stays until the next one arrives. Thread-safe.
/// </summary>
public sealed class SizeBoundedCache<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new(); // first = most recently used
    private readonly Func<TValue, long> _sizeOf;
    private long _size;

    private readonly record struct Entry(TKey Key, TValue Value, long Size);

    public SizeBoundedCache(long maxBytes, Func<TValue, long> sizeOf, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        MaxBytes = maxBytes;
        _sizeOf = sizeOf;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    /// <summary>The budget in bytes.</summary>
    public long MaxBytes { get; }

    /// <summary>The bytes the cached values take now.</summary>
    public long SizeBytes
    {
        get { lock (_gate) return _size; }
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>Gets a cached value and marks it as just used.</summary>
    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                Touch(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    /// <summary>Adds or replaces a value.</summary>
    public TValue this[TKey key]
    {
        set
        {
            lock (_gate)
            {
                if (_map.Remove(key, out var old))
                {
                    _order.Remove(old);
                    _size -= old.Value.Size;
                }
                AddLocked(key, value);
            }
        }
    }

    /// <summary>
    /// The cached value, or <paramref name="factory"/>'s, which is then cached. The factory runs
    /// outside the lock, so values are made in parallel; when two threads make the same one, the
    /// first to finish is kept and returned to both.
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        if (TryGetValue(key, out var cached))
            return cached;
        var made = factory(key);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                Touch(node);
                return node.Value.Value;
            }
            AddLocked(key, made);
        }
        return made;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
            _size = 0;
        }
    }

    private void Touch(LinkedListNode<Entry> node)
    {
        if (node != _order.First)
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
    }

    private void AddLocked(TKey key, TValue value)
    {
        long size = value is null ? 0 : Math.Max(0, _sizeOf(value));
        var node = _order.AddFirst(new Entry(key, value, size));
        _map[key] = node;
        _size += size;
        while (_size > MaxBytes && _order.Last is { } last && last != node)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
            _size -= last.Value.Size;
        }
    }
}

/// <summary>Cache budgets scaled to this machine's memory.</summary>
public static class CacheBudget
{
    /// <summary><paramref name="fraction"/> of the memory available to the process, kept within <paramref name="min"/> and <paramref name="max"/>.</summary>
    public static long For(double fraction, long min, long max)
    {
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return total <= 0 ? min : Math.Clamp((long)(total * fraction), min, max);
    }
}
