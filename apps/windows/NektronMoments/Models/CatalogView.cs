using System.Collections;

namespace NektronMoments.Models;

/// <summary>Immutable ordering over the compact catalog; only prepared UI rows are held strongly.</summary>
public sealed class CatalogView : IReadOnlyList<MediaItem>
{
    private readonly CompactCatalog? _compact;
    private readonly MediaItem[]? _fixture;
    private readonly int[]? _order, _inverse;
    private readonly List<MediaItem> _prepared;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _preparing = new(1);
    public Guid Identity { get; }
    public int Count => _compact?.Count ?? _fixture!.Length;
    public bool IsCompact => _compact is not null;
    public int PreparedPrefixCount { get { lock (_gate) return _prepared.Count; } }
    public int MaterializedCount {
        get {
            if (_compact is null) return Count;
            lock (_gate) return _compact.MaterializedCount + _prepared.Count(item =>
                !_compact.TryGetReady(item.CatalogIndex, out var cached) || !ReferenceEquals(cached, item));
        }
    }
    private CatalogView(CompactCatalog? compact, MediaItem[]? fixture, Guid identity, int[]? order, IEnumerable<MediaItem> prepared)
    {
        _compact = compact; _fixture = fixture; Identity = identity; _order = order; _prepared = [.. prepared];
        if (order is not null) { _inverse = new int[order.Length]; for (var i = 0; i < order.Length; i++) _inverse[order[i]] = i; }
    }
    public static CatalogView FromCompact(CompactCatalog source) =>
        new(source, null, source.Identity, null, Enumerable.Range(0, source.PreparedPrefixCount).Select(source.Get));
    public static CatalogView FromItems(IEnumerable<MediaItem> items)
    {
        var array = items.ToArray(); var identity = Guid.NewGuid();
        for (var i = 0; i < array.Length; i++) { array[i].CatalogIndex = i; array[i].CatalogIdentity = identity; }
        return new(null, array, identity, null, array);
    }
    private int RawIndex(int index) => _order is null ? index : _order[index];
    public MediaItem this[int index] {
        get {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            lock (_gate) if (index < _prepared.Count) return _prepared[index];
            return _compact is null ? _fixture![RawIndex(index)] : _compact.Get(RawIndex(index));
        }
    }
    public int IndexOf(MediaItem item) => item.CatalogIdentity == Identity && (uint)item.CatalogIndex < (uint)Count
        ? _inverse is null ? item.CatalogIndex : _inverse[item.CatalogIndex] : -1;
    public bool TryGetReady(int index, out MediaItem? item)
    {
        if ((uint)index >= (uint)Count) { item = null; return false; }
        lock (_gate) if (index < _prepared.Count) { item = _prepared[index]; return true; }
        if (_compact is not null) return _compact.TryGetReady(RawIndex(index), out item);
        item = _fixture![RawIndex(index)]; return true;
    }
    public Task PreparePrefixAsync(int count, CancellationToken token = default)
    {
        count = Math.Clamp(count, 0, Count);
        if (count <= PreparedPrefixCount) return Task.CompletedTask;
        return Task.Run(async () => {
            await _preparing.WaitAsync(token).ConfigureAwait(false);
            try {
                var start = PreparedPrefixCount;
                if (count <= start) return;
                var rows = new MediaItem[count - start];
                for (var index = start; index < count; index++) { token.ThrowIfCancellationRequested(); rows[index - start] = this[index]; }
                token.ThrowIfCancellationRequested();
                lock (_gate) _prepared.AddRange(rows);
            } finally { _preparing.Release(); }
        }, token);
    }
    public CatalogView WithPrefix(IReadOnlyList<MediaItem> rows)
    {
        if (rows.Count > PreparedPrefixCount) throw new InvalidOperationException("Only prepared browsing rows can be rearranged.");
        var expected = new HashSet<int>(Enumerable.Range(0, rows.Count).Select(RawIndex));
        foreach (var item in rows)
            if (item.CatalogIdentity != Identity || !expected.Remove(item.CatalogIndex))
                throw new InvalidOperationException("The browsing order no longer matches this library.");
        var order = _order is null ? Enumerable.Range(0, Count).ToArray() : (int[])_order.Clone();
        for (var i = 0; i < rows.Count; i++) order[i] = rows[i].CatalogIndex;
        MediaItem[] prepared;
        lock (_gate) prepared = _prepared.ToArray();
        for (var i = 0; i < rows.Count; i++) prepared[i] = rows[i];
        return new(_compact, _fixture, Identity, order, prepared);
    }
    public static async Task<CatalogView> RestoreAsync(CompactCatalog source, IReadOnlyList<string> keys, int initialCount, CancellationToken token, IReadOnlyList<string>? paths = null)
    {
        // This is called on a worker. Scan only keys, never materialize all media DTOs.
        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        if (paths is not null && paths.Count == keys.Count)
            for (var i = 0; i < keys.Count; i++)
                if (keys[i].StartsWith("pending:", StringComparison.Ordinal) && !string.IsNullOrEmpty(paths[i])) byPath.TryAdd(paths[i], keys[i]);
        for (var i = 0; i < source.Count && found.Count < wanted.Count; i++) {
            token.ThrowIfCancellationRequested();
            var key = source.KeyAt(i);
            if (wanted.Contains(key)) found[key] = i;
            else if (byPath.Count > 0 && byPath.TryGetValue(source.PathAt(i), out var previousKey)) found.TryAdd(previousKey, i);
        }
        int[]? order = null;
        if (found.Count > 0) {
            var arranged = new List<int>(source.Count); var used = new HashSet<int>();
            foreach (var key in keys) if (found.TryGetValue(key, out var index) && used.Add(index)) arranged.Add(index);
            for (var i = 0; i < source.Count; i++) if (!used.Contains(i)) arranged.Add(i);
            order = arranged.ToArray();
        }
        var view = new CatalogView(source, null, source.Identity, order, []);
        await view.PreparePrefixAsync(initialCount, token).ConfigureAwait(false);
        return view;
    }
    public IEnumerator<MediaItem> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
