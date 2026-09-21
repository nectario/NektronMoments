using NektronMoments.Models;

internal static class ThumbnailKeyCacheTests
{
    public static void Run(Action<bool, string> check)
    {
        var keys = new ThumbnailKeyCache();
        var item = new MediaItem { Key = "photo-1", Path = @"D:\Pictures\photo.jpg", ByteSize = 123, ModifiedNs = 456 };
        var key = keys.Get(item, 0, 512);
        check(key == @"0|photo-1|D:\Pictures\photo.jpg|123|456|512", "Preview key preserves complete original identity");
        check(ReferenceEquals(key, keys.Get(item, 0, 512)), "Repeated preview checks reuse the exact key string");
        foreach (var pixels in new uint[] { 0, 256, 512, 1024, 128 }) {
            var tier = keys.Get(item, 0, pixels);
            check(ReferenceEquals(tier, keys.Get(item, 0, pixels)), "Each requested preview tier is memoized");
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) {
            _ = keys.Get(item, 0, 512);
            _ = keys.Get(item, 0, 0);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check(allocated == 0, $"20,000 steady-state preview key checks allocate no memory (actual {allocated})");
        var epoch = keys.Get(item, 1, 512);
        check(key != epoch && epoch.StartsWith("1|", StringComparison.Ordinal), "Refresh epoch invalidates preview identities");
        item.ModifiedNs = 789;
        var modified = keys.Get(item, 1, 512);
        check(modified != epoch, "Original modification time invalidates preview identities");
        item.ByteSize = 999;
        var resized = keys.Get(item, 1, 512);
        check(resized != modified, "Original byte size invalidates preview identities");
        item.Path = @"E:\Pictures\photo.jpg";
        var moved = keys.Get(item, 1, 512);
        check(moved != resized, "Original path invalidates preview identities");
        item.Key = "photo-2";
        check(keys.Get(item, 1, 512) != moved, "Catalog item identity invalidates preview identities");
        item.ByteSize = null; item.ModifiedNs = null;
        check(keys.Get(item, 1, 512).EndsWith("|||512", StringComparison.Ordinal), "Missing file-version values remain distinguishable from zero");
        var equivalent = new MediaItem { Key = item.Key, Path = item.Path };
        check(keys.Get(item, 1, 512) == keys.Get(equivalent, 1, 512), "Equivalent re-materialized catalog items share decoded-cache identity");
        var expected = keys.Get(item, 1, 512);
        Parallel.For(0, 1000, _ => {
            if (!ReferenceEquals(expected, keys.Get(item, 1, 512))) throw new Exception("Concurrent cache read lost key identity");
        });
        check(true, "Foreground and worker preview checks safely share one memoized key");
    }
}
