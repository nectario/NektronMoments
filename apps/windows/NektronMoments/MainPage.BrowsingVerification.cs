using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NektronMoments.Controls;
using NektronMoments.Models;
using NektronMoments.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace NektronMoments;

public sealed partial class MainPage
{
    // Exercises the real prefix collection, native layout, viewer and thumbnail
    // preparation using generated files only. The drag guard is simulated here;
    // these checks do not replay OS pointer input or measure display-present FPS.
    private async Task VerifyBrowsingAsync(string output)
    {
        if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Browsing-check output must be absolute.");
        Directory.CreateDirectory(output);
        var errors = new List<string>();
        var checks = new List<string>();
        var appendSamples = new List<object>();
        var denseViewportSamples = new List<object>();
        var presenter = App.MainWindowInstance?.AppWindow.Presenter as OverlappedPresenter;
        var windowStateBefore = presenter?.State;
        var originalItems = Items.ToArray();
        var selectedBefore = _selected;
        var sizeBefore = _thumbnailSize;
        var pixelsBefore = MediaThumbnail.TargetPixels;
        var warmingBefore = _thumbnailWarmingSuspended;
        var trackingBefore = _browseThumbTracking;
        var run = Guid.NewGuid().ToString("N");
        var progress = Path.Combine(output, "browsing-progress.log");
        var readyBefore = MediaThumbnail.Decoded.Count;
        var readyBytesBefore = MediaThumbnail.Decoded.Bytes;
        var preparationsBefore = MediaThumbnail.PreparationCount;
        var cacheHitsBefore = MediaThumbnail.ReadyCacheHits;
        var warmedOutsideRange = 0;
        var preparedWithoutControl = false;
        var reusedReadyObject = false;
        var displayedReadyObject = false;
        var warmMilliseconds = 0d;
        void Check(bool passed, string description)
        {
            checks.Add(description);
            if (!passed) errors.Add(description);
            File.AppendAllText(progress, $"{DateTime.UtcNow:O} {(passed ? "PASS" : "FAIL")} {description}\n");
        }
        try {
            _thumbnailWarmingSuspended = true;
            _thumbnailPrefetch?.Cancel();
            _browseThumbTracking = true;
            Busy.Visibility = EmptyState.Visibility = Visibility.Collapsed;
            AddFolderButton.IsEnabled = ProcessButton.IsEnabled = false;
            LibrarySubtitle.Text = "Browsing and prepared thumbnails · Generated fixtures · No library connection";
            var fixture = await CreateFixtureAsync();
            var catalog = CreateCatalog(650, "browse", fixture.Path);
            ApplyThumbnailSize(240, false);
            Items.ReplaceAll(catalog);
            // Thumbnail-size changes cancel native gestures, so reassert the
            // separate app guard before yielding to layout callbacks.
            _browseThumbTracking = true;
            Gallery.UpdateLayout(); GalleryLoaded(this, new RoutedEventArgs());
            await Task.Delay(300, _lifetime.Token);
            Gallery.UpdateLayout();
            if (_pixelScroll is null) throw new InvalidOperationException("Native gallery scroller did not attach.");
            Check(Items.Count == 650 && BrowseItems.Count == 200 && Gallery.Items.Count == 200,
                "A complete 650-item catalog exposes only the initial 200 browsing positions");
            Check(ReferenceEquals(Gallery.ItemsSource, BrowseItems), "The native gallery uses the independent browsing projection");
            Check(BrowseItems.SequenceEqual(Items.Take(200)), "Browsing positions preserve full-catalog object identity and order");

            await PositionAtExposedEndAsync();
            var maximumBefore = GalleryScrollbar.Maximum;
            var anchorBefore = CaptureAnchor();
            Check(!TryExtendBrowsing() && BrowseItems.Count == 200 && Math.Abs(GalleryScrollbar.Maximum - maximumBefore) < 1,
                "An active app thumb-drag guard prevents browsing extent changes at the boundary");
            await AppendAndCheckAsync(400, anchorBefore);
            await PositionAtExposedEndAsync();
            await AppendAndCheckAsync(600, CaptureAnchor());
            await PositionAtExposedEndAsync();
            await AppendAndCheckAsync(650, CaptureAnchor());
            await PositionAtExposedEndAsync();
            _browseThumbTracking = false;
            var grewPastEnd = TryExtendBrowsing();
            _browseThumbTracking = true;
            Check(!grewPastEnd && BrowseItems.Count == Items.Count &&
                Gallery.ItemsPanelRoot is ItemsWrapGrid finalPanel && finalPanel.LastVisibleIndex == 649,
                "The final partial batch reaches the actual last photo without adding phantom positions");

            foreach (var count in new[] { 0, 199, 200, 650 }) {
                Items.ReplaceAll(catalog.Take(count));
                Gallery.UpdateLayout();
                await Task.Delay(100, _lifetime.Token);
                Check(BrowseItems.Count == Math.Min(200, count) && Gallery.Items.Count == Math.Min(200, count),
                    $"Replacing the catalog with {count} records resets the browsing projection correctly");
                Check(BrowseItems.SequenceEqual(Items.Take(200)), $"The {count}-record replacement has no stale prior catalog positions");
            }

            // A dense native viewport can contain the entire initial batch, so
            // growth must not depend on receiving a scroll event. Exercise the
            // real queued page work, rather than calling the append helper here.
            if (presenter is null) throw new InvalidOperationException("The browsing fixture requires an overlapped native window.");
            foreach (var maximized in new[] { false, true }) {
                _browseThumbTracking = true;
                if (maximized) presenter.Maximize(); else presenter.Restore();
                ApplyThumbnailSize(112, false);
                Items.ReplaceAll(catalog);
                _browseThumbTracking = true;
                _pixelScroll.JumpTo(0);
                Gallery.UpdateLayout();
                await Task.Delay(300, _lifetime.Token);
                Gallery.UpdateLayout();
                var initialPanel = (ItemsWrapGrid)Gallery.ItemsPanelRoot;
                var initialFirst = initialPanel.FirstVisibleIndex;
                var initialLast = initialPanel.LastVisibleIndex;
                var initialVisible = Math.Max(1, initialLast - initialFirst + 1);
                var batch = BrowsingBatch;
                var expectedGrowth = BrowsingPolicy.ShouldExtend(650, batch, initialLast, initialVisible, false, batch);
                Check(BrowseItems.Count == batch && batch == 500, $"{(maximized ? "Maximized" : "Restored")} small-tile layout holds its adaptive 500 positions during the drag guard");
                _browseThumbTracking = false;
                QueueGalleryWork();
                var growthWatch = Stopwatch.StartNew();
                var stableWatch = Stopwatch.StartNew();
                var observedCount = BrowseItems.Count;
                while (growthWatch.ElapsedMilliseconds < 3000) {
                    await Task.Delay(50, _lifetime.Token);
                    if (BrowseItems.Count != observedCount) {
                        observedCount = BrowseItems.Count;
                        stableWatch.Restart();
                    }
                    if (!_galleryWorkQueued && stableWatch.ElapsedMilliseconds >= 350) break;
                }
                _browseThumbTracking = true;
                Gallery.UpdateLayout();
                var densePanel = (ItemsWrapGrid)Gallery.ItemsPanelRoot;
                var visible = Math.Max(1, densePanel.LastVisibleIndex - densePanel.FirstVisibleIndex + 1);
                var threshold = Math.Clamp(visible, 24, batch / 2);
                var boundedCount = Math.Min(650, Math.Max(batch, densePanel.LastVisibleIndex + 1 + threshold + batch));
                var stillNeedsGrowth = BrowsingPolicy.ShouldExtend(650, BrowseItems.Count, densePanel.LastVisibleIndex, visible, false, batch);
                Check((!expectedGrowth || BrowseItems.Count > batch) && !stillNeedsGrowth &&
                    (_pixelScroll.Scroll.ScrollableHeight > 0 || BrowseItems.Count == Items.Count),
                    $"{(maximized ? "Maximized" : "Restored")} small-tile queued growth creates a useful browsing buffer without a false end");
                Check(BrowseItems.Count <= boundedCount,
                    $"{(maximized ? "Maximized" : "Restored")} queued growth stays bounded by visible geometry plus one batch");
                denseViewportSamples.Add(new {
                    maximized, thumbnailSize = 112, initialFirst, initialLast, initialVisible,
                    expectedGrowth, browsingCount = BrowseItems.Count,
                    firstVisibleIndex = densePanel.FirstVisibleIndex, lastVisibleIndex = densePanel.LastVisibleIndex,
                    columns = GetGalleryColumns(densePanel.ItemWidth), itemHeight = densePanel.ItemHeight,
                    viewportHeight = _pixelScroll.Scroll.ViewportHeight,
                    scrollableHeight = _pixelScroll.Scroll.ScrollableHeight,
                    galleryWidth = Gallery.ActualWidth, threshold, boundedCount,
                    growthMilliseconds = growthWatch.Elapsed.TotalMilliseconds,
                    nativeQueuedGrowth = true, osInputReplay = false,
                });
            }
            _browseThumbTracking = true;
            presenter.Restore();
            ApplyThumbnailSize(240, false);
            Items.ReplaceAll(catalog);
            _pixelScroll.JumpTo(0);
            _browseThumbTracking = true;
            Gallery.UpdateLayout();
            await Task.Delay(300, _lifetime.Token);
            Check(BrowseItems.Count == 200, "The dense-viewport probe restores the initial 200 positions before viewer checks");

            await OpenCanvasAsync(false, Items[350]);
            await Task.Delay(180, _lifetime.Token);
            Check(Viewer.CurrentIndex == 350 && Viewer.CurrentItemKey == catalog[350].Key && Viewer.HasPhoto,
                "The canvas viewer opens a full-catalog photo beyond the exposed browsing range");
            Check(BrowseItems.Count == 200, "Viewer navigation does not unnecessarily enlarge the hidden gallery");
            ReturnToGallery();
            _browseThumbTracking = true;
            await Task.Delay(180, _lifetime.Token);
            Gallery.UpdateLayout();
            Check(BrowseItems.Count >= 351 && ReferenceEquals(Gallery.SelectedItem, catalog[350]),
                "Returning from the viewer exposes and selects its exact full-catalog photo");
            Check(Gallery.ItemsPanelRoot is ItemsWrapGrid returnPanel && returnPanel.FirstVisibleIndex <= 350 && returnPanel.LastVisibleIndex >= 350,
                "Returning from the viewer brings the newly exposed photo into the native viewport");

            // Use a new cache identity so earlier visible fixture tiles cannot
            // accidentally satisfy the no-control predecode assertion.
            Items.ReplaceAll(CreateCatalog(420, "prepare", fixture.Path));
            // Use two real production tiers regardless of monitor DPI. Requests
            // above1600 are rejected by the Windows thumbnail provider.
            MediaThumbnail.SetResolution(256);
            Gallery.UpdateLayout();
            await Task.Delay(120, _lifetime.Token);
            var preparedItem = Items[250];
            var requestedPixels = MediaThumbnail.TargetPixels;
            var preparedKey = ThumbnailService.Shared.Key(preparedItem, requestedPixels);
            Check(BrowseItems.Count == 200 && Gallery.ContainerFromItem(preparedItem) is null,
                "The predecode target has no exposed gallery position or native tile");
            var prepared = await MediaThumbnail.PrepareAsync(preparedItem, requestedPixels, _lifetime.Token, prefetch: true)
                .WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token);
            preparedWithoutControl = prepared is not null && MediaThumbnail.Decoded.TryGet(preparedKey, out var ready) &&
                ReferenceEquals(prepared, ready) && Gallery.ContainerFromItem(preparedItem) is null && BrowseItems.Count == 200;
            Check(preparedWithoutControl, "Background preparation caches a display-ready image before its gallery control exists");
            Check(prepared is not null && prepared.Source is null &&
                prepared.DecodeThreadId != Environment.CurrentManagedThreadId && MediaThumbnail.UiDecodeAttempts == 0,
                "Off-range preview decoding completes on a worker without creating a XAML source");
            var reused = await MediaThumbnail.PrepareAsync(preparedItem, requestedPixels, _lifetime.Token, prefetch: false);
            reusedReadyObject = prepared is not null && ReferenceEquals(prepared, reused);
            Check(reusedReadyObject, "A foreground request reuses the same already-prepared bitmap object");
            var decodesBeforeSmaller = MediaThumbnail.PreparationCount;
            var smaller = await MediaThumbnail.PrepareAsync(preparedItem, Math.Max(1, requestedPixels / 2), _lifetime.Token, prefetch: false);
            Check(ReferenceEquals(prepared, smaller) && MediaThumbnail.PreparationCount == decodesBeforeSmaller,
                "A smaller thumbnail request reuses the larger ready image without another decode");
            EnsureBrowseIncludes(250);
            Gallery.ScrollIntoView(preparedItem);
            var presentationWait = Stopwatch.StartNew();
            do {
                await Task.Delay(16, _lifetime.Token);
                if (Gallery.ContainerFromItem(preparedItem) is GridViewItem preparedContainer) {
                    var tile = AssetDescendants(preparedContainer).OfType<MediaThumbnail>().FirstOrDefault();
                    var picture = tile is null ? null : AssetDescendants(tile).OfType<Image>().FirstOrDefault(image => image.Name == "Picture");
                    displayedReadyObject = prepared?.Source is not null && ReferenceEquals(picture?.Source, prepared.Source);
                }
            } while (!displayedReadyObject && presentationWait.ElapsedMilliseconds < 1000);
            File.AppendAllText(progress, $"Presentation wait: {presentationWait.Elapsed.TotalMilliseconds:0.0}ms; pending={MediaThumbnail.PendingPresentations}; sourceCreated={prepared?.Source is not null}\n");
            Check(displayedReadyObject, "Revealing a previously prepared photo assigns the cached bitmap to its actual native tile");
            Check(MediaThumbnail.UiDecodeAttempts == 0, "No thumbnail decode ran on the UI thread");

            var readyContainer = Gallery.ContainerFromItem(preparedItem) as GridViewItem;
            var readyTile = readyContainer is null ? null : AssetDescendants(readyContainer).OfType<MediaThumbnail>().FirstOrDefault();
            Check(readyTile is not null && !readyTile.ObservesViewport,
                "A ready native thumbnail stops receiving per-frame viewport event wrappers");
            if (readyTile is not null) {
                var picture = AssetDescendants(readyTile).OfType<Image>().First(image => image.Name == "Picture");
                const uint upgradedPixels = 512;
                try {
                    MediaThumbnail.SetResizePreview(true);
                    MediaThumbnail.SetResolution(upgradedPixels);
                    Check(readyTile.ObservesViewport,
                        "A higher resolution explicitly re-arms the ready tile's viewport observation");
                    MediaThumbnail.SetResizePreview(false);
                    var upgradedKey = ThumbnailService.Shared.Key(preparedItem, upgradedPixels);
                    var upgradeWait = Stopwatch.StartNew();
                    while (upgradeWait.Elapsed < TimeSpan.FromSeconds(5) &&
                        (readyTile.ObservesViewport || !MediaThumbnail.Decoded.TryGet(upgradedKey, out var upgraded) ||
                         upgraded.Source is null || !ReferenceEquals(picture.Source, upgraded.Source)))
                        await Task.Delay(16, _lifetime.Token);
                    File.AppendAllText(progress, $"Upgrade tile: {JsonSerializer.Serialize(readyTile.CaptureDiagnosticState())}; rawReady={MediaThumbnail.Decoded.TryGet(upgradedKey, out _)}; pending={MediaThumbnail.PendingLoads}/{MediaThumbnail.PendingPresentations}\n");
                    Check(!readyTile.ObservesViewport && MediaThumbnail.Decoded.TryGet(upgradedKey, out var upgradeReady) &&
                        upgradeReady.Source is not null && ReferenceEquals(picture.Source, upgradeReady.Source),
                        "The re-armed native tile presents its upgraded preview and detaches again");

                    var replacement = CreateCatalog(1, "recycled", fixture.Path)[0];
                    readyTile.Item = replacement;
                    Check(readyTile.ObservesViewport && picture.Source is null,
                        "Recycling a ready native tile clears the old picture and re-arms viewport observation");
                    var replacementKey = ThumbnailService.Shared.Key(replacement, upgradedPixels);
                    var recycleWait = Stopwatch.StartNew();
                    while (recycleWait.Elapsed < TimeSpan.FromSeconds(5) &&
                        (readyTile.ObservesViewport || !MediaThumbnail.Decoded.TryGet(replacementKey, out var replacementReady) ||
                         replacementReady.Source is null || !ReferenceEquals(picture.Source, replacementReady.Source)))
                        await Task.Delay(16, _lifetime.Token);
                    File.AppendAllText(progress, $"Recycled tile: {JsonSerializer.Serialize(readyTile.CaptureDiagnosticState())}; rawReady={MediaThumbnail.Decoded.TryGet(replacementKey, out _)}; pending={MediaThumbnail.PendingLoads}/{MediaThumbnail.PendingPresentations}\n");
                    Check(!readyTile.ObservesViewport && MediaThumbnail.Decoded.TryGet(replacementKey, out var recycled) &&
                        recycled.Source is not null && ReferenceEquals(picture.Source, recycled.Source),
                        "A recycled native tile presents only its replacement preview and detaches again");
                    readyTile.Item = preparedItem;
                    Check(!readyTile.ObservesViewport && MediaThumbnail.Decoded.TryGet(upgradedKey, out var restored) &&
                        restored.Source is not null && ReferenceEquals(picture.Source, restored.Source),
                        "Recycling back to a cached photo immediately reuses its source without leaving a viewport subscription");
                } finally {
                    MediaThumbnail.SetResizePreview(false);
                    MediaThumbnail.SetResolution(requestedPixels);
                }
            }

            // Exercise the actual page scheduler, independently from the direct
            // preparation call above. It must warm beyond position 199 without
            // publishing those positions into the scrollbar's collection.
            var warmCatalog = CreateCatalog(420, "scheduled", fixture.Path);
            Items.ReplaceAll(warmCatalog);
            Gallery.UpdateLayout();
            _highestVisible = 0;
            _warmGeneration = -1;
            _thumbnailWarmingSuspended = false;
            var warmTimer = Stopwatch.StartNew();
            ScheduleThumbnailWarm();
            var scheduledKey = ThumbnailService.Shared.Key(warmCatalog[250], MediaThumbnail.TargetPixels);
            while (!MediaThumbnail.Decoded.TryGet(scheduledKey, out _) && warmTimer.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(25, _lifetime.Token);
            warmMilliseconds = warmTimer.Elapsed.TotalMilliseconds;
            _thumbnailWarmingSuspended = true;
            _thumbnailPrefetch?.Cancel();
            warmedOutsideRange = warmCatalog.Skip(200).Count(item =>
                MediaThumbnail.Decoded.TryGet(ThumbnailService.Shared.Key(item, MediaThumbnail.TargetPixels), out _));
            Check(MediaThumbnail.Decoded.TryGet(scheduledKey, out _) && warmedOutsideRange > 0,
                "The real page look-ahead scheduler prepares display-ready photos outside the first 200 positions");
            Check(BrowseItems.Count == 200 && Items.Count == 420 && Gallery.ContainerFromItem(warmCatalog[250]) is null,
                "Background look-ahead leaves the browsing extent unchanged and does not create off-range UI controls");
            Check(MediaThumbnail.Decoded.TryGet(scheduledKey, out var scheduled) && scheduled.Source is null,
                "Background look-ahead creates no XAML source for unexposed previews");

            // Exercise the production compact store, not only array-backed test
            // fixtures. It must preserve identity while rows arrive from workers.
            _thumbnailWarmingSuspended = true;
            _thumbnailPrefetch?.Cancel();
            var compact = await Task.Run(async () => {
                using var frames = Frames().GetEnumerator();
                var snapshot = await CompactCatalog.ReadStreamAsync(_ =>
                    Task.FromResult(frames.MoveNext() ? frames.Current : null));
                await snapshot.PreparePrefixAsync(200, _lifetime.Token);
                return snapshot;
                IEnumerable<string> Frames() {
                    var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                    yield return "{\"ok\":true,\"catalogStart\":{\"version\":1,\"total\":650}}";
                    foreach (var item in CreateCatalog(650, "compact", fixture.Path))
                        yield return JsonSerializer.Serialize(new { ok = true, item }, json);
                    yield return "{\"ok\":true,\"catalogEnd\":{\"count\":650}}";
                }
            });
            Items.ReplaceSnapshot(compact);
            _browseThumbTracking = true;
            Check(Items.Count == 650 && Items.MaterializedCount == 200 && BrowseItems.Count == 200,
                "The compact catalog publishes only 200 prepared objects while retaining all 650 positions");
            Check(!Items.TryGetReady(350, out _), "Unvisited compact rows are not eagerly materialized");
            var compactPhoto = await ItemAtAsync(350);
            Check(compactPhoto is not null && Items.IndexOf(compactPhoto) == 350,
                "A worker-materialized compact photo resolves by snapshot identity without a catalog scan");
            await OpenCanvasAsync(false, compactPhoto);
            await Task.Delay(180, _lifetime.Token);
            Check(Viewer.HasPhoto && Viewer.CurrentIndex == 350 && BrowseItems.Count == 200,
                "The canvas opens an unexposed compact row without enlarging the gallery");
            ReturnToGallery();
            var returnWait = Stopwatch.StartNew();
            while (BrowseItems.Count < 400 && returnWait.ElapsedMilliseconds < 3000)
                await Task.Delay(25, _lifetime.Token);
            _browseThumbTracking = true;
            Gallery.UpdateLayout();
            Check(BrowseItems.Count >= 400 && ReferenceEquals(Gallery.SelectedItem, compactPhoto),
                "Worker-prepared compact rows return to the exact selected photo");
            Check(Items.MaterializedCount < Items.Count,
                "Returning from a compact photo does not materialize the untouched remainder");
            await VerifyGalleryFeaturesAsync(output, fixture.Path, Check);
            if (Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_FLOW_AUDIT") == "1")
                await AuditInteractionsAsync(output, fixture.Path);
            await VerifyRequestResponsivenessAsync(output, fixture.Path, Check);
        } catch (Exception error) {
            errors.Add(error.ToString());
            File.AppendAllText(progress, $"{DateTime.UtcNow:O} ERROR {error}\n");
        } finally {
            _thumbnailWarmingSuspended = true;
            _thumbnailPrefetch?.Cancel();
            if (!_lifetime.IsCancellationRequested) {
                _browseThumbTracking = true;
                Viewer.Close(); LibraryCanvas.Visibility = Visibility.Visible;
                _pixelScroll?.Stop();
                Items.ReplaceAll(originalItems);
                _selected = selectedBefore;
                if (presenter is not null && windowStateBefore.HasValue) {
                    if (windowStateBefore == OverlappedPresenterState.Maximized) presenter.Maximize();
                    else if (windowStateBefore == OverlappedPresenterState.Minimized) presenter.Minimize();
                    else presenter.Restore();
                }
                ApplyThumbnailSize(sizeBefore, false);
                MediaThumbnail.SetResolution(pixelsBefore);
                _browseThumbTracking = trackingBefore;
                _thumbnailWarmingSuspended = warmingBefore;
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "browsing.json"), JsonSerializer.Serialize(new {
            passed = errors.Count == 0, errors, checks, appendSamples, denseViewportSamples,
            initialBrowseCount = 200, completeFixtureCount = 650,
            preparedWithoutControl, reusedReadyObject, displayedReadyObject, warmedOutsideRange, warmMilliseconds,
            readyCacheCountBefore = readyBefore, readyCacheCountAfter = MediaThumbnail.Decoded.Count,
            readyCacheBytesBefore = readyBytesBefore, readyCacheBytesAfter = MediaThumbnail.Decoded.Bytes,
            newBitmapPreparations = MediaThumbnail.PreparationCount - preparationsBefore,
            readyBitmapCacheHits = MediaThumbnail.ReadyCacheHits - cacheHitsBefore,
            libraryConnected = false, osInputReplay = false,
            dragGuardSimulation = true,
            measurement = "Native prefix-layout and generated-photo checks; app drag guard simulation, not physical dragging or display-present FPS",
            version = typeof(App).Assembly.GetName().Version?.ToString(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!_lifetime.IsCancellationRequested)
            StatusText.Text = errors.Count == 0 ? "Browsing and prepared thumbnail checks passed" : "Browsing verification failed";

        MediaItem[] CreateCatalog(int count, string phase, string path) => Enumerable.Range(0, count).Select(index => new MediaItem {
            Key = $"browsing-fixture-{run}-{phase}-{index}", Name = $"Generated photo {index:N0}", Path = path,
            MediaType = "Photo", Captured = "2026-09-17", Metadata = JsonSerializer.SerializeToElement(new { }),
        }).ToArray();

        async Task PositionAtExposedEndAsync()
        {
            _browseThumbTracking = true;
            _pixelScroll!.YieldToNativeInput();
            Gallery.ScrollIntoView(BrowseItems[^1]);
            await Task.Delay(150, _lifetime.Token);
            Gallery.UpdateLayout();
            Check(Gallery.ItemsPanelRoot is ItemsWrapGrid panel && panel.LastVisibleIndex == BrowseItems.Count - 1,
                $"The native viewport reaches exposed position {BrowseItems.Count - 1}");
        }

        (int Index, string Key, double Y, double Offset) CaptureAnchor()
        {
            var panel = (ItemsWrapGrid)Gallery.ItemsPanelRoot;
            var index = panel.FirstVisibleIndex;
            if (index < 0 || Gallery.ContainerFromIndex(index) is not FrameworkElement container)
                throw new InvalidOperationException("The first visible native browsing anchor was not realized.");
            return (index, BrowseItems[index].Key,
                container.TransformToVisual(Gallery).TransformPoint(new Windows.Foundation.Point()).Y,
                _pixelScroll!.Scroll.VerticalOffset);
        }

        async Task AppendAndCheckAsync(int expectedCount, (int Index, string Key, double Y, double Offset) before)
        {
            _browseThumbTracking = false;
            var watch = Stopwatch.StartNew();
            var grew = TryExtendBrowsing();
            var appendMs = watch.Elapsed.TotalMilliseconds;
            _browseThumbTracking = true;
            Gallery.UpdateLayout();
            await Task.Delay(120, _lifetime.Token);
            var after = CaptureAnchor();
            Check(grew && BrowseItems.Count == expectedCount, $"Releasing the browsing guard permits growth to {expectedCount} positions");
            Check(after.Index == before.Index && after.Key == before.Key && Math.Abs(after.Y - before.Y) <= 1.5 &&
                Math.Abs(after.Offset - before.Offset) <= 1.5,
                $"Appending through {expectedCount} preserves the visible photo identity and pixel anchor");
            Check(BrowseItems.SequenceEqual(Items.Take(expectedCount)), $"The {expectedCount}-position prefix has no duplicates, gaps or reordered photos");
            appendSamples.Add(new { count = expectedCount, appendMs,
                anchorIndexBefore = before.Index, anchorIndexAfter = after.Index,
                anchorYBefore = before.Y, anchorYAfter = after.Y,
                offsetBefore = before.Offset, offsetAfter = after.Offset });
        }

        async Task<StorageFile> CreateFixtureAsync()
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(output);
            var file = await folder.CreateFileAsync("browsing-generated-photo.png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            const uint width = 96, height = 64;
            var pixels = new byte[width * height * 4];
            for (var index = 0; index < pixels.Length; index += 4) {
                pixels[index] = 205; pixels[index + 1] = 144; pixels[index + 2] = 42; pixels[index + 3] = 255;
            }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
            await encoder.FlushAsync();
            return file;
        }
    }
}
