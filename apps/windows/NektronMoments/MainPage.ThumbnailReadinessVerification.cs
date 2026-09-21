using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NektronMoments.Controls;
using NektronMoments.Services;

namespace NektronMoments;

public sealed partial class MainPage
{
    // Opt-in observation after normal library loading. This method does not
    // navigate, force layout, prepare thumbnails, open files or call the bridge.
    // Image.Source assignment is readiness evidence, not a display-present metric.
    private async Task VerifyLiveThumbnailReadinessAsync(string output)
    {
        if (!Path.IsPathFullyQualified(output))
            throw new ArgumentException("Thumbnail-readiness output must be absolute.");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var samples = new List<object>();
        var errors = new List<string>();
        var clock = Stopwatch.StartNew();
        var startedUtc = DateTime.UtcNow;
        var cancelled = false;
        var preparationStart = MediaThumbnail.PreparationCount;
        var readyHitsStart = MediaThumbnail.ReadyCacheHits;
        try {
            foreach (var dueSeconds in new[] { 0d, 1, 5, 15 }) {
                var remaining = dueSeconds * 1000 - clock.Elapsed.TotalMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), _lifetime.Token);
                _lifetime.Token.ThrowIfCancellationRequested();
                samples.Add(CaptureSnapshot(dueSeconds));
            }
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            cancelled = true;
        } catch (Exception error) {
            // Diagnostic reports contain counts and indices only. In particular,
            // do not serialize exception messages that can contain media paths.
            errors.Add(error.GetType().Name);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "readiness.json"), JsonSerializer.Serialize(new {
            completed = samples.Count == 4 && errors.Count == 0 && !cancelled,
            cancelled, errors, startedUtc, elapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
            sampleScheduleSeconds = new[] { 0, 1, 5, 15 }, samples,
            bitmapPreparationsDuringObservation = MediaThumbnail.PreparationCount - preparationStart,
            readyCacheHitsDuringObservation = MediaThumbnail.ReadyCacheHits - readyHitsStart,
            realLibrary = true, libraryCallsByObservation = 0,
            automaticallyScrolled = false, automaticallyResized = false,
            thumbnailPreparationRequestedByObservation = false,
            paidJobsStarted = false, uploadsStarted = false, screenshotsCaptured = false,
            measurement = "Read-only readiness observations after normal library loading. Assigned image sources and cache counts are not display-present FPS, a cold-cache benchmark, or a matched comparison with an earlier build.",
            initialLayoutGrace = "The first sample may precede the first arranged viewport. Such a sample is marked layoutPending; it is not a failure or a missing-photo count.",
            version = typeof(App).Assembly.GetName().Version?.ToString(),
        }, new JsonSerializerOptions { WriteIndented = true }));

        object CaptureSnapshot(double scheduledSeconds)
        {
            var panel = Gallery.ItemsPanelRoot as ItemsWrapGrid;
            var firstReported = panel?.FirstVisibleIndex ?? -1;
            var lastReported = panel?.LastVisibleIndex ?? -1;
            var layoutPending = panel is null || !Gallery.IsLoaded || Gallery.ActualWidth <= 0 || Gallery.ActualHeight <= 0 ||
                (Gallery.Items.Count > 0 && (firstReported < 0 || lastReported < firstReported));
            var visiblePositions = 0;
            var realizedVisibleContainers = 0;
            var visibleThumbnailControls = 0;
            var visibleTilesWithAssignedImage = 0;
            var visibleTilesWithoutAssignedImage = 0;
            var visiblePlaceholderControls = 0;
            if (!layoutPending && Gallery.Items.Count > 0) {
                var first = Math.Clamp(firstReported, 0, Gallery.Items.Count - 1);
                var last = Math.Clamp(lastReported, first, Gallery.Items.Count - 1);
                visiblePositions = last - first + 1;
                for (var index = first; index <= last; index++) {
                    if (Gallery.ContainerFromIndex(index) is not GridViewItem container) continue;
                    ++realizedVisibleContainers;
                    foreach (var thumbnail in AssetDescendants(container).OfType<MediaThumbnail>()) {
                        if (!thumbnail.IsLoaded || thumbnail.Visibility != Visibility.Visible) continue;
                        ++visibleThumbnailControls;
                        var descendants = AssetDescendants(thumbnail).ToArray();
                        var picture = descendants.OfType<Image>().FirstOrDefault(image => image.Name == "Picture");
                        if (picture?.Source is not null) ++visibleTilesWithAssignedImage;
                        else ++visibleTilesWithoutAssignedImage;
                        if (descendants.OfType<FrameworkElement>().Any(control =>
                            control.Name == "Placeholder" && control.Visibility == Visibility.Visible))
                            ++visiblePlaceholderControls;
                    }
                }
            }
            return new {
                scheduledSeconds,
                observationElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                millisecondsSinceLibraryReady = Math.Max(0, _startup.ElapsedMilliseconds - _libraryReadyMs),
                catalogCount = Items.Count, browsingCount = BrowseItems.Count, galleryCount = Gallery.Items.Count,
                materializedCatalogItems = Items.MaterializedCount, managedHeapBytes = GC.GetTotalMemory(false),
                firstVisibleIndex = firstReported, lastVisibleIndex = lastReported, layoutPending,
                visiblePositions, realizedVisibleContainers, visibleThumbnailControls,
                visibleTilesWithAssignedImage, visibleTilesWithoutAssignedImage, visiblePlaceholderControls,
                decodedCount = MediaThumbnail.Decoded.Count, decodedBytes = MediaThumbnail.Decoded.Bytes,
                encodedBytes = ThumbnailService.Shared.EncodedBytes,
                pendingLoads = MediaThumbnail.PendingLoads,
                preparationCount = MediaThumbnail.PreparationCount, readyCacheHits = MediaThumbnail.ReadyCacheHits,
                targetThumbnailPixels = MediaThumbnail.TargetPixels,
                galleryWidth = Gallery.ActualWidth, galleryHeight = Gallery.ActualHeight,
                beyondExposedRange = CaptureBeyondRange(),
            };
        }

        object? CaptureBeyondRange()
        {
            var index = BrowseItems.Count;
            if (index >= Items.Count) return null;
            if (!Items.TryGetReady(index, out var item) || item is null) return new { catalogIndex = index, hasReadyBitmap = false };
            uint? readyPixels = null;
            var requestedPixels = MediaThumbnail.TargetPixels;
            foreach (var pixels in new[] { requestedPixels, 512u, 1024u }.Distinct().Where(pixels => pixels >= requestedPixels)) {
                if (!MediaThumbnail.Decoded.TryGet(ThumbnailService.Shared.Key(item, pixels), out _)) continue;
                readyPixels = pixels;
                break;
            }
            return new {
                catalogIndex = index,
                hasReadyBitmap = readyPixels.HasValue,
                readyPixels,
                hasGalleryContainer = Gallery.ContainerFromItem(item) is not null,
            };
        }
    }
}
