using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace NektronMoments.Models;

/// <summary>
/// Memoizes only requested preview identities, without retaining catalog items.
/// Viewport and presentation validity checks reuse the same strings; a refresh
/// epoch or changed original-file identity invalidates every resolution together.
/// </summary>
public sealed class ThumbnailKeyCache
{
    private readonly ConditionalWeakTable<MediaItem, Entry> _items = new();

    public string Get(MediaItem item, long epoch, uint pixels)
    {
        var entry = _items.GetValue(item, static _ => new Entry());
        lock (entry) {
            if (!entry.Matches(item, epoch)) entry.Reset(item, epoch);
            return entry.Get(pixels);
        }
    }

    private sealed class Entry
    {
        private bool _initialized;
        private long _epoch;
        private string _key = "", _path = "", _prefix = "";
        private long? _byteSize, _modifiedNs;
        private string? _identity, _small, _medium, _large;
        private Dictionary<uint, string>? _other;

        public bool Matches(MediaItem item, long epoch) => _initialized && _epoch == epoch &&
            _key == item.Key && _path == item.Path && _byteSize == item.ByteSize && _modifiedNs == item.ModifiedNs;

        public void Reset(MediaItem item, long epoch)
        {
            _initialized = true; _epoch = epoch; _key = item.Key; _path = item.Path;
            _byteSize = item.ByteSize; _modifiedNs = item.ModifiedNs;
            // Append concrete numeric values: nullable interpolation otherwise
            // boxes values and used to allocate repeatedly during native scroll.
            var builder = new StringBuilder(_key.Length + _path.Length + 64);
            builder.Append(epoch).Append('|').Append(_key).Append('|').Append(_path).Append('|');
            if (_byteSize is long bytes) builder.Append(bytes);
            builder.Append('|');
            if (_modifiedNs is long modified) builder.Append(modified);
            _prefix = builder.ToString();
            _identity = _small = _medium = _large = null; _other = null;
        }

        public string Get(uint pixels) => pixels switch {
            0 => _identity ??= Build(0),
            256 => _small ??= Build(256),
            512 => _medium ??= Build(512),
            1024 => _large ??= Build(1024),
            _ => GetOther(pixels),
        };

        private string GetOther(uint pixels)
        {
            _other ??= [];
            if (!_other.TryGetValue(pixels, out var key)) _other.Add(pixels, key = Build(pixels));
            return key;
        }
        private string Build(uint pixels) => string.Concat(_prefix, "|", pixels.ToString(CultureInfo.InvariantCulture));
    }
}
