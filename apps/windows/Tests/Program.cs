using NektronMoments.Models;
using NektronMoments.Services;

var assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < .001, message);
var small = ViewingPolicy.Fit(200, 100, 1200, 800, 1, false);
Near(small.Width, 200, "Small images must not be enlarged");
Near(small.Height, 100, "Preserve aspect ratio");
var hidpi = ViewingPolicy.Fit(200, 100, 1200, 800, 2, false);
Near(hidpi.Width, 100, "Original-size cap uses physical pixels on high DPI");
var expanded = ViewingPolicy.Fit(200, 100, 1200, 800, 2, true);
Near(expanded.Width, 1200, "Checkbox permits expansion");
Near(expanded.Height, 600, "Expansion keeps ratio");
var portrait = ViewingPolicy.Fit(3000, 4000, 1000, 800, 1.25, false);
Near(portrait.Height, 800, "Portrait fits canvas height");
Near(portrait.Width, 600, "Oriented portrait width");
Check(ViewingPolicy.Fit(0, 100, 100, 100, 1, false).Width == 0, "Zero dimensions are safe");
Check(ViewingPolicy.Fit(double.NaN, 100, 100, 100, 1, true).Width == 0, "NaN is safe");
foreach (var dpi in new[] { 1d, 1.25, 1.5, 2, 3, 4 }) {
    var fit = ViewingPolicy.Fit(4000, 3000, 800, 500, dpi, false);
    Check(fit.Width <= 800 && fit.Height <= 500, "Fits both canvas dimensions");
    Check(fit.Scale <= 1, "No accidental upscaling");
}
Near(ViewingPolicy.ThumbnailWidth(10), 112, "Thumbnail minimum");
Near(ViewingPolicy.ThumbnailWidth(900), 480, "Thumbnail maximum");
foreach (var size in new[] { 144d, 240, 336, 432 }) Near(ViewingPolicy.ThumbnailWidth(size), size, "Preset retained");
var large = GalleryBudget.ForMemory(192UL * 1024 * 1024 * 1024);
var modest = GalleryBudget.ForMemory(8UL * 1024 * 1024 * 1024);
Check(large.RecordBuffer == 24576 && large.PageSize == 4096, "Workstation data buffer");
Check(large.ThumbnailAhead == 1536, "Workstation look-ahead");
Check(modest.DecodedBytes < large.DecodedBytes, "Memory-adaptive budget");
Check(large.DecodedBytes < 192L * 1024 * 1024 * 1024 / 16, "Do not consume all RAM");
var cache = new WeightedCache<string>(10);
cache.Put("a", "first", 4); cache.Put("b", "second", 4);
Check(cache.TryGet("a", out _), "Cache hit");
cache.Put("c", "third", 4);
Check(!cache.TryGet("b", out _), "Least recently used entry evicted");
Check(cache.TryGet("a", out _), "Hot entry retained");
cache.Put("huge", "oversize", 100);
Check(cache.Bytes <= 10 && !cache.TryGet("huge", out _), "Byte budget respected");
cache.Clear(); Check(cache.Count == 0 && cache.Bytes == 0, "Cache clear");
Console.WriteLine($"Native viewing/cache policy: {assertions} assertions passed.");
