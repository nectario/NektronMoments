using System.Collections.Specialized;
using System.Text.Json;
using NektronMoments.Models;

internal static class CompactCatalogTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var catalog = await ReadCatalogAsync(650, 8);
        check(catalog.Count == 650 && catalog.MaterializedCount == 0, "Streaming catalog retains bytes, not 650 eager DTOs");
        check(!catalog.TryGetReady(0, out _), "Cold rows are not silently decoded by cache-only checks");
        await catalog.PreparePrefixAsync(200);
        check(catalog.PreparedPrefixCount == 200 && catalog.MaterializedCount == 200, "Initial 200 browsing rows are prepared and pinned");
        var first = catalog.Get(0); var lastPinned = catalog.Get(199);
        for (var index = 200; index < 230; index++) catalog.Get(index);
        check(catalog.MaterializedCount == 208, "Look-ahead materialization is bounded independently of the pinned prefix");
        check(!catalog.TryGetReady(200, out _) && catalog.TryGetReady(229, out _), "Compact catalog evicts least-recently-used DTOs");
        check(ReferenceEquals(first, catalog.Get(0)) && ReferenceEquals(lastPinned, catalog.Get(199)), "Eviction preserves UI browsing item identity");
        check(first.CatalogIndex == 0 && first.CatalogIdentity == catalog.Identity, "Items carry a stable catalog position and snapshot identity");
        check(ReferenceEquals(first.Key, first.Hash) && ReferenceEquals(first.Path, first.Paths[0]), "Materialized rows compact duplicate identity and path strings");
        check(ReferenceEquals(first.MediaType, lastPinned.MediaType), "Low-cardinality catalog labels share strings");
        check(catalog.Get(649).Name == "Moment 649 — Αθήνα 📷.jpg", "UTF-8 indexed records preserve Unicode and exact ordering");
        check(catalog.Get(649).ByteSize == 9007199254740993L && catalog.Get(649).ModifiedNs == 1700000000123456789L, "File versions retain exact 64-bit integers");

        var collection = new CatalogCollection(); var changes = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, args) => changes.Add(args.Action);
        collection.ReplaceSnapshot(catalog);
        check(collection.Count == 650 && changes.SequenceEqual([NotifyCollectionChangedAction.Reset]), "Compact publication emits one reset without enumerating the library");
        check(collection.IndexOf(first) == 0 && collection.PreparedPrefixCount == 200, "Selection uses snapshot identity and index");
        var captured = collection.Capture(); var second = await ReadCatalogAsync(650, 8);
        await second.PreparePrefixAsync(200); collection.ReplaceSnapshot(second);
        check(collection.IndexOf(first) == -1, "A stale selection from a replaced catalog is rejected");
        check(ReferenceEquals(captured[0], first), "Background workers retain the immutable snapshot they captured");
        await collection.PreparePrefixAsync(400);
        check(collection.PreparedPrefixCount == 400 && collection.TryGetReady(399, out _), "Next browsing range is ready before native publication");
        check(collection.MaterializedCount <= 408, "Prefix growth preserves the separate bounded look-ahead cache");
        using (var cancel = new CancellationTokenSource()) {
            cancel.Cancel();
            await MustFailAsync(() => collection.PreparePrefixAsync(600, cancel.Token), check, "Cancelled preparation does not pin an unfinished range");
        }
        check(collection.PreparedPrefixCount == 400, "Cancellation leaves the previous browsing range intact");

        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => second.Get(625))));
        check(simultaneous.All(item => ReferenceEquals(item, simultaneous[0])), "Concurrent materialization returns one cached identity");
        var unprepared = await ReadCatalogAsync(650, 8);
        await MustFailAsync(() => { collection.ReplaceSnapshot(unprepared); return Task.CompletedTask; }, check, "UI publication rejects an unprepared initial prefix");
        check(collection.Capture().Identity == second.Identity, "Failed catalog publication preserves the currently usable snapshot");

        var fixture = new MediaItem { Key = "fixture" }; collection.ReplaceAll([fixture]); var fixtureCapture = collection.Capture();
        var added = new MediaItem { Key = "added" }; collection.Add(added);
        check(collection.IndexOf(added) == 1 && collection.PreparedPrefixCount == 2, "Fixture/search collections support add and indexed selection");
        check(fixtureCapture.Count == 1 && collection.Count == 2, "Adding an item does not mutate a captured fixture snapshot");
        collection.ReplaceAll(collection);
        check(collection.Count == 2 && collection.IndexOf(fixture) == 0, "Self replacement is safe");
        collection.Clear(); check(collection.Count == 0 && collection.IndexOf(added) == -1, "Clearing invalidates old selection identities");

        var empty = await ReadCatalogAsync(0, 8); collection.ReplaceSnapshot(empty);
        check(empty.Count == 0 && empty.StoredBytes == 0 && collection.MaterializedCount == 0, "An empty catalog is a valid complete snapshot");
        var multipleChunks = await ReadCatalogAsync(5000, 8);
        check(multipleChunks.StoredBytes > CompactCatalog.ChunkBytes, "Large fixture spans multiple bounded UTF-8 chunks");
        foreach (var index in new[] { 0, 1000, 2048, 4096, 4999 })
            check(multipleChunks.Get(index).Key == $"key-{index}", "Indexed access remains exact across chunk boundaries");
        check(multipleChunks.MaterializedCount == 5, "A 5,000-row snapshot only materializes explicitly requested items");

        await InvalidFramesAsync(["{not json}"], check, "Malformed JSON is rejected");
        await InvalidFramesAsync(["{\"ok\":true,\"catalogStart\":{\"version\":2,\"total\":0}}"], check, "Unknown protocol versions are rejected");
        await InvalidFramesAsync(["{\"ok\":true,\"catalogStart\":{\"version\":1,\"total\":-1}}"], check, "Negative row counts are rejected");
        await InvalidFramesAsync([$"{{\"ok\":true,\"catalogStart\":{{\"version\":1,\"total\":{CompactCatalog.MaximumItems + 1}}}}}"], check, "Implausible row counts are rejected before allocating an index");
        await InvalidFramesAsync([Header(1)], check, "Truncated streams are rejected");
        await InvalidFramesAsync([Header(1), Footer(0)], check, "Premature catalog completion is rejected");
        await InvalidFramesAsync([Header(0), Footer(1)], check, "Footer/header count mismatches are rejected");
        await InvalidFramesAsync([Header(1), RowFrame(0), RowFrame(1)], check, "Extra rows cannot be mistaken for a footer");
        await InvalidFramesAsync([Header(1), "{\"ok\":false,\"error\":\"fixture failure\"}"], check, "Midstream errors reject the incomplete catalog");
        await InvalidFramesAsync([Header(1), "{\"ok\":true,\"item\":null}"], check, "Null rows are rejected");
        await InvalidFramesAsync([Header(1), RowFrame(0).Replace("\"paths\":[", "\"paths\":null,\"unused\":[")], check, "Invalid alias arrays are rejected before lazy UI materialization");
        await InvalidFramesAsync([Header(1), new string('x', CompactCatalog.ChunkBytes + 1)], check, "Oversized frames are rejected");
        using (var cancel = new CancellationTokenSource()) {
            var position = 0;
            await MustFailAsync(() => CompactCatalog.ReadStreamAsync(token => {
                token.ThrowIfCancellationRequested(); if (position == 3) cancel.Cancel();
                return Task.FromResult<string?>(position++ == 0 ? Header(650) : RowFrame(position - 2));
            }, cancel.Token), check, "Cancellation aborts a partial catalog without publishing it");
        }
    }

    internal static Task<CompactCatalog> ReadCatalogAsync(int count, int capacity)
    {
        var position = -1;
        return CompactCatalog.ReadStreamAsync(token => {
            token.ThrowIfCancellationRequested(); position++;
            return Task.FromResult<string?>(position == 0 ? Header(count) : position <= count ? RowFrame(position - 1) : position == count + 1 ? Footer(count) : null);
        }, cacheCapacity: capacity);
    }
    private static string Header(int count) => JsonSerializer.Serialize(new { ok = true, catalogStart = new { version = 1, total = count } });
    private static string Footer(int count) => JsonSerializer.Serialize(new { ok = true, catalogEnd = new { count } });
    private static string RowFrame(int index) => JsonSerializer.Serialize(new { ok = true, item = new {
        key = $"key-{index}", hash = $"key-{index}", name = $"Moment {index} — Αθήνα 📷.jpg", path = $"D:/Pictures/{index}.jpg",
        paths = new[] { $"D:/Pictures/{index}.jpg", $"E:/Backup/{index}.jpg" }, source = "My Photos", occurrences = 2,
        captured = "2026-09-19T14:30:00", dateSource = "embedded", mediaType = "Photo", byteSize = 9007199254740993L, modifiedNs = 1700000000123456789L
    } });
    private static Task InvalidFramesAsync(string[] frames, Action<bool, string> check, string message)
    {
        var position = 0;
        return MustFailAsync(() => CompactCatalog.ReadStreamAsync(_ => Task.FromResult<string?>(position < frames.Length ? frames[position++] : null)), check, message);
    }
    private static async Task MustFailAsync(Func<Task> action, Action<bool, string> check, string message)
    {
        var failed = false;
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or JsonException or OperationCanceledException) { failed = true; }
        check(failed, message);
    }
}
