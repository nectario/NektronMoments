namespace NektronMoments.Services;

/// <summary>Thread-safe, byte-budgeted LRU. Eviction never disposes a bitmap in use by a control.</summary>
public sealed class WeightedCache<T>(long capacity)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, T Value, long Bytes)>> _entries = [];
    private readonly LinkedList<(string Key, T Value, long Bytes)> _order = new();
    private long _bytes;
    public long Bytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _entries.Count; } }
    public bool TryGet(string key, out T value)
    {
        lock (_gate) {
            if (_entries.TryGetValue(key, out var node)) {
                _order.Remove(node); _order.AddFirst(node); value = node.Value.Value; return true;
            }
            value = default!; return false;
        }
    }
    public void Put(string key, T value, long bytes)
    {
        if (bytes < 0 || bytes > capacity) return;
        lock (_gate) {
            if (_entries.Remove(key, out var existing)) { _bytes -= existing.Value.Bytes; _order.Remove(existing); }
            var node = _order.AddFirst((key, value, bytes)); _entries[key] = node; _bytes += bytes;
            while (_bytes > capacity && _order.Last is { } last) {
                _entries.Remove(last.Value.Key); _bytes -= last.Value.Bytes; _order.RemoveLast();
            }
        }
    }
    public void Clear() { lock (_gate) { _entries.Clear(); _order.Clear(); _bytes = 0; } }
}
