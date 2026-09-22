using NektronMoments.Models;
using NektronMoments.Services;

internal static class GalleryFeatureTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(BrowsingPolicy.DisplayWarmCount(8192, 192UL << 30) == 1000, "Workstation display-ready lookahead prepares up to 1,000 images independently of the scrollbar range");
        check(BrowsingPolicy.DisplayWarmCount(512, 8UL << 30) == 160 && BrowsingPolicy.DisplayWarmCount(64, 192UL << 30) == 64,
            "Display warming respects machine capacity and the available image budget");
        var refreshRequests = new List<bool>();
        using (var refresh = new CompositorRefreshLease(enable => { refreshRequests.Add(enable); return 0; })) {
            refresh.Begin(); refresh.Begin(); refresh.End(); refresh.End();
            check(refreshRequests.SequenceEqual(new[] { true, false }), "High-refresh requests are balanced once per gesture");
        }
        using (var unavailable = new CompositorRefreshLease(_ => -1)) {
            unavailable.Begin(); check(!unavailable.IsHeld, "Unsupported high-refresh APIs degrade safely");
        }
        var ai = AiProcessingOptions.Resolve(null);
        var astraPrice = AiProcessingOptions.PricingModels.Single(item => item.Id == "gpt-6-astra");
        check(astraPrice.InputRate == 10m && astraPrice.OutputRate == 50m, "Astra comparison uses verified standard input/output pricing");
        check(!AiProcessingOptions.Models.Any(item => item.Id == astraPrice.Id), "Pricing-only Astra is not silently enabled for execution");
        check(ai.Limit == 64 && ai.Model == "gpt-5.6-terra", "AI defaults preserve Terra and 64");
        check(AiProcessingOptions.Resolve("{\"Limit\":10000,\"Model\":\"gpt-5.6-terra\"}").Limit == 10000, "Catch-up allowance survives preference reload");
        check(new AiProcessingOptions(10000).Estimate() == 63.2m, "Catch-up estimate scales to the full run, not a 64-photo request");
        check(new AiProcessingOptions(10000).EstimateSummary().Contains("US$63.20 estimated per source"), "Readable pricing summary retains the existing estimate and explicit USD units");
        check(new AiProcessingOptions(1, "gpt-5.6-luna").EstimateSummary().Contains("US$0.0006"), "Sub-cent estimates do not misleadingly round to free");
        check(AiProcessingOptions.Resolve("{\"Limit\":0,\"Model\":\"bad\"}") == ai, "Invalid AI preferences safely restore defaults");
        check(ai.Estimate() == .40448m && ai.Estimate(2) == .80896m, "Cost estimate uses model rates and number of sources");
        check(new AiProcessingOptions(64, "gpt-5.6-luna").Estimate() < ai.Estimate(), "Changing models changes the estimate");
        check(StartupProcessing.Arguments(StartupProcessingMode.Full, "source", new AiProcessingOptions(7, "gpt-5.6-sol")).SequenceEqual(
            new[] { "sync", "source", "--with-enrichment", "--enrichment-limit", "7", "--description-model", "gpt-5.6-sol", "--byok", "--no-input" }), "Full passes the selected model and limit through direct BYOK");
        check(StartupProcessing.Resolve(null, true) == StartupProcessingMode.FileMetadata, "Existing enabled startup setting migrates to metadata only, never paid Full mode");
        check(StartupProcessing.Resolve(null, false) == StartupProcessingMode.None, "Existing disabled startup setting stays off");
        check(StartupProcessing.Resolve("Full") == StartupProcessingMode.Full, "Full mode requires an explicit saved selection");
        check(StartupProcessing.Arguments(StartupProcessingMode.Full, "source").SequenceEqual(new[] { "sync", "source", "--with-enrichment", "--byok", "--no-input" }), "Full explicitly requests bounded direct BYOK enrichment");
        check(StartupProcessing.Arguments(StartupProcessingMode.FileMetadata, "source").SequenceEqual(new[] { "sync", "source", "--no-input" }), "Metadata mode cannot request enrichment");
        try { StartupProcessing.Arguments(StartupProcessingMode.None, "source"); check(false, "Off must not create a job"); }
        catch (InvalidOperationException) { check(true, "Off cannot create a processing command"); }
        var fullProgress = new MetadataProgress(); fullProgress.ConfigureSources(["Photos"], withEnrichment: true);
        var byokProgress = new MetadataProgress(); byokProgress.ConfigureSources(["Photos"], withEnrichment: true);
        byokProgress.Source("Photos", 1, 1); byokProgress.Report("BYOK analyzed · 1/10 descriptions completed · saved locally · 2.50 items/s");
        check(byokProgress.Snapshot.ItemsPerSecond == 2.5, "BYOK exposes measured AI throughput");
        check(byokProgress.Snapshot.PhaseRemaining?.TotalSeconds == 3.6, "BYOK estimates remaining phase time");
        byokProgress.Report("BYOK synchronized · description saved to your library");
        check(byokProgress.Snapshot.ItemsPerSecond == 2.5, "Synchronization preserves AI throughput");
        check(byokProgress.Snapshot.Completed == 1 && byokProgress.Snapshot.Total == 10, "BYOK preserves actual completion progress while synchronizing");
        byokProgress.Finish(false, false);
        check(byokProgress.Snapshot.Message.StartsWith("BYOK pass complete"), "BYOK completion is not presented as an AI job still queued");
        fullProgress.Source("Photos", 1, 1); fullProgress.Report("Preparing explicit enrichment for up to 64 media asset(s)");
        check(fullProgress.Snapshot.EnrichmentEnabled && fullProgress.Snapshot.Phase == "Preparing AI and address enrichment", "Full progress exposes enrichment rather than only metadata");
        fullProgress.Report("Staged scene preview for photo.jpg");
        check(fullProgress.Snapshot.Phase == "Preparing scene descriptions", "Scene staging is visible in processing progress");
        fullProgress.Finish(false, false);
        check(fullProgress.Snapshot.Message.Contains("may still be queued") && !fullProgress.Snapshot.Message.Contains("No paid"), "Full completion does not falsely claim no paid work or finished server-side AI");
        check(StartupProcessing.Resolve("unknown", true) == StartupProcessingMode.FileMetadata, "Invalid startup settings do not enable paid processing");
        check(StartupProcessing.Resolve("FileMetadata", false) == StartupProcessingMode.FileMetadata && StartupProcessing.Resolve("None", true) == StartupProcessingMode.None,
            "The new saved mode takes precedence over the old toggle");
        check(ReorderPolicy.Destination(2, 7, true, 20) == 7 && ReorderPolicy.Destination(7, 2, false, 20) == 2,
            "Live reordering handles moves across multiple rows in both directions");
        check(ReorderPolicy.Destination(2, 2, true, 20) == 2 && ReorderPolicy.Destination(0, 1, false, 20) == 0,
            "Crossing the insertion midpoint prevents hover oscillation");
        check(ReorderPolicy.Destination(-1, 0, false, 20) == -1 && ReorderPolicy.Destination(0, 20, true, 20) == -1,
            "Out-of-view reorder destinations are rejected");
        check(BrowsingPolicy.BatchFor(240) == 200 && BrowsingPolicy.BatchFor(432) == 200, "Medium and larger thumbnails retain 200-position batches");
        check(BrowsingPolicy.BatchFor(144) == 500 && BrowsingPolicy.BatchFor(112) == 500, "Small thumbnails use 500-position batches");
        check(BrowsingPolicy.BatchFor(192) == 300, "Intermediate thumbnail sizes have an intermediate browsing range");
        check(BrowsingPolicy.BatchFor(double.NaN) == 200, "Invalid thumbnail geometry has a safe default");
        foreach (var width in new[] { 100d, 112, 144, 160, 192, 220, 240, 336, 432 }) {
            var batch = BrowsingPolicy.BatchFor(width);
            check(BrowsingPolicy.NextCount(1200, 500, batch) == 500 + batch, "Range extension uses the current size-dependent batch");
            check(!BrowsingPolicy.ShouldExtend(1200, 500, 499, 300, true, batch), "No adaptive range expansion while dragging");
        }
        var raw = await CompactCatalogTests.ReadCatalogAsync(1000, 8);
        var view = await CatalogView.RestoreAsync(raw, ["key-999", "key-600", "key-999", "deleted-key"], 500, CancellationToken.None);
        check(view[0].Key == "key-999" && view[1].Key == "key-600" && view[2].Key == "key-0", "Saved order survives refresh, skips removed items and appends remaining items");
        check(view.Count == 1000 && view.PreparedPrefixCount == 500 && view.MaterializedCount <= 508, "Custom ordering retains a bounded prepared prefix, not a full DTO library");
        check(view.IndexOf(view[0]) == 0 && view.IndexOf(view[499]) == 499, "Viewer positions use the custom-order inverse index");
        var before = view.Take(500).ToArray(); var desired = (MediaItem[])before.Clone();
        var moved = desired[3]; Array.Copy(desired, 4, desired, 3, 96); desired[99] = moved;
        var after = view.WithPrefix(desired);
        check(after.IndexOf(moved) == 99 && ReferenceEquals(after[99], moved), "Reordering preserves item identity and viewer position");
        check(ReferenceEquals(view[3], moved) && view.IndexOf(moved) == 3, "In-flight workers keep their previous immutable order snapshot");
        check(after.Take(500).SequenceEqual(desired) && after[500].Key == view[500].Key, "Reordering leaves the unexposed suffix unchanged");
        var duplicate = desired.ToArray(); duplicate[0] = duplicate[1];
        try { view.WithPrefix(duplicate); check(false, "Duplicate reorder should fail"); } catch (InvalidOperationException) { check(true, "Duplicate reordered rows rejected"); }
        await after.PreparePrefixAsync(800);
        check(after.TryGetReady(799, out _) && after.IndexOf(after[799]) == 799, "Custom-order rows prepare ahead of further browsing");
        var cold = await CompactCatalogTests.ReadCatalogAsync(1000, 8);
        var restored = await CatalogView.RestoreAsync(cold, desired.Select(item => item.Key).ToArray(), 500, CancellationToken.None);
        check(restored.Take(500).Select(item => item.Key).SequenceEqual(desired.Select(item => item.Key)), "Arrangement survives a new compact snapshot");
        check(restored.IndexOf(moved) == -1, "Selections from an older compact snapshot are rejected");
        var hashed = await CatalogView.RestoreAsync(cold, ["pending:old-key"], 200, CancellationToken.None, ["D:/Pictures/999.jpg"]);
        check(hashed[0].Key == "key-999", "A pending photo keeps its custom position when metadata processing gives it a content hash");

        var progress = new MetadataProgress(); progress.Source("Test photos", 1, 2);
        progress.Report("Discovering media · 5,000 files found");
        check(progress.Snapshot.Percent is null, "Unknown discovery total stays indeterminate");
        progress.Report("Reading file metadata · 2,500/10,000 · 6,000 files/s");
        check(progress.Snapshot.Percent == 25 && progress.Snapshot.SourceCount == 2, "Metadata progress reports real phase counts and source context");
        progress.Report("Processing media · 20/100 · 42 files/s · 1 failed");
        check(progress.Snapshot.Percent == 20 && progress.Snapshot.Phase.Contains("hashing"), "Phase transitions do not show an invented overall percentage");
        progress.Report("Accepted manifest batch 8"); check(progress.Snapshot.Percent is null, "Saving without a denominator becomes indeterminate");
        progress.Report("Bulk metadata · Upserting · 5,000/10,000 rows"); check(progress.Snapshot.Percent == 50, "Bulk progress is decoded from actual reported counts");
        progress.Report("╭─────────╮"); check(progress.Snapshot.Percent == 50, "CLI table borders do not replace useful progress");
        progress.Report("Processing media · 2/0"); check(progress.Snapshot.Percent is null, "Zero totals do not divide by zero");
        progress.StopRequested(); progress.Report("Processing media · 100/100");
        check(progress.Snapshot.State == "Stopping", "Late worker output cannot overwrite cancellation state");
        progress.Finish(true, false); check(!progress.Snapshot.Running && progress.Snapshot.State == "Stopped", "Cancellation is distinct from success");
        progress = new(); progress.Finish(false, true); check(progress.Snapshot.State == "Failed" && progress.Snapshot.Percent is null, "Failure never reports 100 percent success");
        progress = new(); progress.Refreshing(); check(progress.Snapshot.Running && progress.Snapshot.Percent is null, "Refresh remains a running phase");
        progress.Finish(false, false); check(progress.Snapshot.Percent == 100 && progress.Snapshot.Ended.HasValue, "Successful pass completion freezes elapsed time");
        progress = new(); progress.ConfigureSources(["Photos", "Videos"]); progress.Source("Photos", 1, 2);
        progress.Report("Processing media · 50/100 · 10 files/s");
        check(progress.Snapshot.OverallPercent == 0 && progress.Snapshot.Percent == 50 && progress.Snapshot.PhaseRemaining == TimeSpan.FromSeconds(5),
            "Overall source progress is separate from the real current-phase count and ETA");
        progress.CompleteSource();
        check(progress.Snapshot.OverallPercent == 50 && progress.Snapshot.Operations[0].Completed == 50,
            "Source completion preserves the latest reported counts instead of inventing counts");
        progress.Source("Videos", 2, 2); progress.StopRequested(); progress.Finish(true, false);
        check(progress.Snapshot.OverallPercent == 50 && progress.Snapshot.Operations[1].State == "Stopped",
            "Stopping retains completed sources and does not claim the unfinished source completed");
        progress = new(); progress.Source("Photos", 1, 1);
        for (var index = 0; index < 1200; index++) progress.Report($"Processing media · {index}/2000 · 100 files/s");
        var log = progress.LogAfter(0, 1000);
        check(log.Length == 1000 && log[0].Sequence > 1 && log[^1].Sequence == progress.Snapshot.LogSequence,
            "Long processing sessions keep a bounded recent history with monotonic sequence numbers");
        check(progress.LogAfter(log[^1].Sequence).Length == 0 && progress.LogAfter(0).Length == 500,
            "Window polling retrieves only new log rows with a bounded display batch");
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var directoryName = "moments-order-tests-" + Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Path.Combine(temporaryRoot, directoryName));
        try {
            var store = new LibraryOrderStore(directory);
            await store.SaveAsync("account-a|photos", "custom", ["b", "a"], ["b.jpg", "a.jpg"], "oldest");
            await store.SaveAsync("account-a|photos", "oldest");
            var stored = await store.LoadAsync("account-a|photos");
            check(stored.Sort == "oldest" && stored.Keys.SequenceEqual(["b", "a"]), "Date sorting retains the saved arrangement");
            check(stored.BaseSort == "oldest" && stored.Paths!.SequenceEqual(["b.jpg", "a.jpg"]), "Custom order retains its original date direction and locator fallbacks");
            check((await store.LoadAsync("account-b|photos")).Keys.Length == 0, "Arrangements are isolated by account and view");
            await Task.WhenAll(store.SaveAsync("account-a|photos", "custom", ["a", "b"]), store.SaveAsync("account-a|photos", "custom", ["b", "c"]));
            check((await store.LoadAsync("account-a|photos")).Keys.SequenceEqual(["b", "c"]), "Serialized atomic writes preserve the most recent arrangement");
            await store.SaveAsync("account-b|photos", "custom", ["other"]);
            await store.ResetAsync("account-a|photos");
            stored = await store.LoadAsync("account-a|photos");
            check(stored.Sort == "newest" && stored.BaseSort == "newest" && stored.Keys.Length == 0 && stored.Paths!.Length == 0,
                "Reset Order clears keys and locator fallbacks and restores newest first");
            check((await store.LoadAsync("account-b|photos")).Keys.SequenceEqual(["other"]), "Reset leaves other views unchanged");
            check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Completed arrangement writes leave no temporary files");
        } finally {
            if (!string.Equals(Path.GetDirectoryName(directory), temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(directory) != directoryName) throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
