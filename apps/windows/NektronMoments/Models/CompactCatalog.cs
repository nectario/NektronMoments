using System.Collections;
using System.Text;
using System.Text.Json;

namespace NektronMoments.Models;

/// <summary>
/// An immutable UTF-8 catalog with a small, lazy object working set. The whole
/// library remains addressable without retaining one managed object graph per photo.
/// </summary>
public sealed class CompactCatalog : IReadOnlyList<MediaItem>
{
    public const int ChunkBytes = 1024 * 1024;
    public const int DefaultCacheCapacity = 8192;
    public const int MaximumItems = 2_000_000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] TextFields = ["key", "hash", "name", "path", "source", "captured", "dateSource", "mediaType"];
    private static readonly string[] VersionFields = ["byteSize", "modifiedNs"];
    private readonly byte[][] _chunks;
    private readonly Row[] _rows;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _prefixGate = new(1);
    private readonly List<MediaItem> _prefix = [];
    private readonly Dictionary<int, LinkedListNode<Entry>> _cached = [];
    private readonly LinkedList<Entry> _recent = [];
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    private readonly int _cacheCapacity;

    private readonly record struct Row(int Chunk, int Offset, int Length);
    private readonly record struct Entry(int Index, MediaItem Item);
    public Guid Identity { get; } = Guid.NewGuid();
    public int Count => _rows.Length;
    public int PreparedPrefixCount { get { lock (_gate) return _prefix.Count; } }
    public int MaterializedCount { get { lock (_gate) return _prefix.Count + _cached.Count; } }
    public long StoredBytes { get; }
    public MediaItem this[int index] => Get(index);
    public string KeyAt(int index) => TextAt(index, "key");
    public string PathAt(int index) => TextAt(index, "path");
    private string TextAt(int index, string property)
    {
        var row = _rows[index];
        var reader = new Utf8JsonReader(_chunks[row.Chunk].AsSpan(row.Offset, row.Length));
        while (reader.Read()) {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(property)) continue;
            reader.Read(); return reader.GetString()!;
        }
        throw new InvalidDataException("Catalog identity is missing.");
    }

    private CompactCatalog(byte[][] chunks, Row[] rows, long storedBytes, int cacheCapacity)
    {
        _chunks = chunks; _rows = rows; StoredBytes = storedBytes; _cacheCapacity = cacheCapacity;
    }

    public bool TryGetReady(int index, out MediaItem? item)
    {
        lock (_gate) return TryGetReadyLocked(index, out item);
    }

    private bool TryGetReadyLocked(int index, out MediaItem? item)
    {
        if ((uint)index < (uint)_prefix.Count) { item = _prefix[index]; return true; }
        if (_cached.TryGetValue(index, out var node)) {
            _recent.Remove(node); _recent.AddLast(node); item = node.Value.Item; return true;
        }
        item = null; return false;
    }

    /// <summary>Cache misses decode one row; callers prepare UI ranges on a worker first.</summary>
    public MediaItem Get(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        lock (_gate) if (TryGetReadyLocked(index, out var ready)) return ready!;
        var row = _rows[index];
        // Parsing happens outside the cache lock, so UI cache hits cannot wait on JSON.
        var item = JsonSerializer.Deserialize<MediaItem>(_chunks[row.Chunk].AsSpan(row.Offset, row.Length), Json)
            ?? throw new InvalidDataException("A catalog item was empty.");
        item.CatalogIndex = index; item.CatalogIdentity = Identity;
        lock (_gate) {
            if (TryGetReadyLocked(index, out var ready)) return ready!;
            if (item.Hash == item.Key) item.Hash = item.Key;
            for (var alias = 0; alias < item.Paths.Length; alias++)
                if (item.Paths[alias] == item.Path) item.Paths[alias] = item.Path;
            item.Source = Label(item.Source); item.DateSource = Label(item.DateSource); item.MediaType = Label(item.MediaType);
            _cached.Add(index, _recent.AddLast(new Entry(index, item)));
            while (_cached.Count > _cacheCapacity) {
                var oldest = _recent.First!; _recent.RemoveFirst(); _cached.Remove(oldest.Value.Index);
            }
            return item;
        }
    }

    private string Label(string value)
    {
        if (value.Length == 0) return "";
        if (_labels.TryGetValue(value, out var label)) return label;
        _labels.Add(value, value); return value;
    }

    /// <summary>Prepare and pin a prefix before publishing it to XAML. Cancellation pins nothing new.</summary>
    public Task PreparePrefixAsync(int count, CancellationToken cancellation = default)
    {
        count = Math.Clamp(count, 0, Count);
        if (count <= PreparedPrefixCount) return Task.CompletedTask;
        return Task.Run(async () => {
            await _prefixGate.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                var start = PreparedPrefixCount;
                if (count <= start) return;
                var prepared = new MediaItem[count - start];
                for (var index = start; index < count; index++) {
                    cancellation.ThrowIfCancellationRequested(); prepared[index - start] = Get(index);
                }
                cancellation.ThrowIfCancellationRequested();
                lock (_gate) {
                    foreach (var item in prepared) {
                        if (_cached.Remove(item.CatalogIndex, out var cached)) _recent.Remove(cached);
                        _prefix.Add(item);
                    }
                }
            }
            finally { _prefixGate.Release(); }
        }, cancellation);
    }

    public IEnumerator<MediaItem> GetEnumerator()
    {
        for (var index = 0; index < Count; index++) yield return Get(index);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Read the versioned catalog-stream protocol; no complete UTF-16 catalog is constructed.</summary>
    public static async Task<CompactCatalog> ReadStreamAsync(
        Func<CancellationToken, Task<string?>> readLine, CancellationToken cancellation = default,
        int cacheCapacity = DefaultCacheCapacity)
    {
        ArgumentNullException.ThrowIfNull(readLine);
        if (cacheCapacity < 1) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        using var start = await ReadFrameAsync(readLine, cancellation).ConfigureAwait(false);
        if (!start.RootElement.TryGetProperty("catalogStart", out var header) ||
            header.ValueKind != JsonValueKind.Object ||
            !header.TryGetProperty("version", out var version) || !version.TryGetInt32(out var protocol) || protocol != 1 ||
            !header.TryGetProperty("total", out var totalValue) || !totalValue.TryGetInt32(out var total) || total < 0 || total > MaximumItems)
            throw new InvalidDataException("The library catalog header was invalid.");
        var builder = new Builder(total, cacheCapacity);
        for (var index = 0; index < total; index++) {
            cancellation.ThrowIfCancellationRequested();
            using var frame = await ReadFrameAsync(readLine, cancellation).ConfigureAwait(false);
            if (!frame.RootElement.TryGetProperty("item", out var item))
                throw new InvalidDataException("The library catalog ended before all items arrived.");
            ValidateItem(item);
            builder.Add(item.GetRawText());
        }
        using var end = await ReadFrameAsync(readLine, cancellation).ConfigureAwait(false);
        if (!end.RootElement.TryGetProperty("catalogEnd", out var footer) || footer.ValueKind != JsonValueKind.Object ||
            !footer.TryGetProperty("count", out var countValue) || !countValue.TryGetInt32(out var count) || count != total)
            throw new InvalidDataException("The library catalog count did not match its header.");
        return builder.Build();
    }

    private static async Task<JsonDocument> ReadFrameAsync(Func<CancellationToken, Task<string?>> readLine, CancellationToken cancellation)
    {
        var line = await readLine(cancellation).ConfigureAwait(false);
        if (line is null) throw new InvalidDataException("The library connection closed before its catalog was complete.");
        if (line.Length > ChunkBytes) throw new InvalidDataException("A library catalog frame exceeded the supported size.");
        var frame = JsonDocument.Parse(line);
        try {
            if (frame.RootElement.ValueKind != JsonValueKind.Object ||
                !frame.RootElement.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("The library catalog response was invalid.");
            if (!ok.GetBoolean())
                throw new InvalidOperationException(frame.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString() : "The library could not load its catalog.");
            return frame;
        }
        catch { frame.Dispose(); throw; }
    }

    private static void ValidateItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A library catalog item was invalid.");
        foreach (var field in TextFields)
            if (!item.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("A library catalog text field was invalid.");
        if (!item.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("A library catalog path was invalid.");
        foreach (var path in paths.EnumerateArray())
            if (path.ValueKind != JsonValueKind.String) throw new InvalidDataException("A library catalog path was invalid.");
        if (!item.TryGetProperty("occurrences", out var occurrences) || !occurrences.TryGetInt32(out _))
            throw new InvalidDataException("A library catalog occurrence count was invalid.");
        foreach (var field in VersionFields)
            if (item.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null && !value.TryGetInt64(out _))
                throw new InvalidDataException("A library catalog file version was invalid.");
    }

    private sealed class Builder(int count, int cacheCapacity)
    {
        private readonly List<byte[]> _chunks = [];
        private readonly Row[] _rows = new Row[count];
        private byte[]? _current;
        private int _used, _count;
        private long _bytes;
        public void Add(string json)
        {
            var length = Encoding.UTF8.GetByteCount(json);
            if (length > ChunkBytes) throw new InvalidDataException("A library catalog item exceeded the supported size.");
            if (_current is null || _used + length > _current.Length) {
                _current = new byte[ChunkBytes]; _chunks.Add(_current); _used = 0;
            }
            Encoding.UTF8.GetBytes(json, _current.AsSpan(_used, length));
            _rows[_count++] = new Row(_chunks.Count - 1, _used, length);
            _used += length; _bytes += length;
        }
        public CompactCatalog Build()
        {
            if (_count != count) throw new InvalidDataException("The library catalog was incomplete.");
            if (_current is not null && _used < _current.Length) _chunks[^1] = _current.AsSpan(0, _used).ToArray();
            return new CompactCatalog(_chunks.ToArray(), _rows, _bytes, cacheCapacity);
        }
    }
}
