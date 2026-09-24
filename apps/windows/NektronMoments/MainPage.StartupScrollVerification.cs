using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NektronMoments.Controls;

namespace NektronMoments;

public sealed partial class MainPage
{
    // App-driven animated thumb destinations during warm-up; not OS pointer replay or GPU FPS.
    private async Task VerifyStartupScrollingAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var errors = new List<string>();
        var latency = new List<double>();
        var ticks = new List<double>();
        var watch = new Stopwatch();
        var sizeBefore = _thumbnailSize;
        var requestedSize = double.TryParse(Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_SCROLL_THUMBNAIL_SIZE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size) ? Models.ViewingPolicy.ThumbnailWidth(size) : 240;
        var trackingBefore = _browseThumbTracking;
        var window = App.MainWindowInstance!.AppWindow;
        var presenter = (OverlappedPresenter)window.Presenter;
        var stateBefore = presenter.State;
        var preparationBefore = MediaThumbnail.PreparationCount;
        var gcPauseBefore = GC.GetTotalPauseDuration();
        var gen2Before = GC.CollectionCount(2);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        EventHandler<object>? renderTick = null;
        Task? heartbeat = null;
        var nativeMoves = 0;
        var realizedMax = 0;
        try {
            _browseThumbTracking = true;
            MediaThumbnail.SetThumbInput(true);
            presenter.Maximize(); ApplyThumbnailSize(requestedSize, false);
            var range = Models.BrowsingPolicy.InitialCount(Items.Count, Models.BrowsingPolicy.BatchFor(requestedSize));
            await Items.PreparePrefixAsync(range, _lifetime.Token); AppendBrowsing(range);
            await Task.Delay(200, _lifetime.Token);
            _browseThumbTracking = true;
            Gallery.UpdateLayout();
            var idleWarmupSeconds = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_SCROLL_IDLE_WARMUP") == "1" ? 8 : 0;
            if (idleWarmupSeconds > 0) {
                MediaThumbnail.SetThumbInput(false);
                ScheduleThumbnailWarm();
                await Task.Delay(TimeSpan.FromSeconds(idleWarmupSeconds), _lifetime.Token);
                // Keep the same range while isolating display-cache warming.
                _browseThumbTracking = true;
            }
            MediaThumbnail.SetThumbInput(true); // Window resize cancels native gestures; start the measured gesture afterwards.
            var scroll = _pixelScroll!.Scroll;
            _pixelScroll.YieldToNativeInput();
            var contentExtent = scroll.ScrollableHeight;
            var rangeBefore = BrowseItems.Count;
            watch.Start();
            Services.DiagnosticTrace.Marker("AutomatedScrubStart");
            _scrollRefresh.Begin();
            renderTick = (_, _) => {
                ticks.Add(watch.Elapsed.TotalMilliseconds);
                // A bounded triangle-wave path over the initial browsing range.
                var phase = watch.Elapsed.TotalSeconds % 4 / 2;
                var percent = 10 + 75 * (phase <= 1 ? phase : 2 - phase);
                _pixelScroll.SeekFromScrollbar(contentExtent * percent / 100);
                ++nativeMoves;
                realizedMax = Math.Max(realizedMax, Gallery.ItemsPanelRoot?.Children.Count ?? 0);
            };
            CompositionTarget.Rendering += renderTick;
            var warmupSeconds = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_SCROLL_WARMUP") == "1" ? 8 : 0;
            if (warmupSeconds > 0) {
                await Task.Delay(TimeSpan.FromSeconds(warmupSeconds), _lifetime.Token);
                watch.Restart(); ticks.Clear(); nativeMoves = 0;
                preparationBefore = MediaThumbnail.PreparationCount;
                gcPauseBefore = GC.GetTotalPauseDuration(); gen2Before = GC.CollectionCount(2);
            }
            heartbeat = Task.Run(async () => {
                while (!cancellation.IsCancellationRequested) {
                    var queuedAt = Stopwatch.GetTimestamp();
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!DispatcherQueue.TryEnqueue(() => {
                        latency.Add(Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
                        completion.TrySetResult();
                    })) break;
                    await completion.Task.WaitAsync(cancellation.Token);
                    await Task.Delay(16, cancellation.Token);
                }
            });
            await Task.Delay(8000, _lifetime.Token);
            CompositionTarget.Rendering -= renderTick; renderTick = null;
            var refreshRequestAccepted = _scrollRefresh.IsHeld; _scrollRefresh.End();
            Services.DiagnosticTrace.Marker("AutomatedScrubStop");
            cancellation.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            var gaps = ticks.Zip(ticks.Skip(1), (a, b) => b - a).Order().ToArray();
            var waits = latency.Order().ToArray();
            if (nativeMoves < 20) errors.Add("Too few native scrub updates.");
            if (contentExtent <= 0) errors.Add("The real-photo probe needs a scrollable browsing range.");
            if (BrowseItems.Count != rangeBefore) errors.Add("The browsing range changed during a held scrollbar gesture.");
            if (MediaThumbnail.Decoded.Count == 0) errors.Add("The real-photo probe has no decoded thumbnails.");
            await File.WriteAllTextAsync(Path.Combine(output, "startup-scroll.json"), JsonSerializer.Serialize(new {
                completed = errors.Count == 0, errors, catalogCount = Items.Count, browsingCount = BrowseItems.Count,
                materializedCatalogItems = Items.MaterializedCount, managedHeapBytes = GC.GetTotalMemory(false),
                durationMs = watch.Elapsed.TotalMilliseconds, nativeMoves, realizedMax, contentExtent,
                animatedThumbDestinations = true,
                galleryCacheLength = (Gallery.ItemsPanelRoot as ItemsWrapGrid)?.CacheLength,
                expandedViewportCache = _expandedViewportCache, profileName = Services.PerformanceProfile.Current.Name,
                renderCallbackHz = gaps.Length > 0 ? 1000 / gaps.Average() : 0,
                warmupSeconds,
                idleWarmupSeconds,
                refreshRequestAccepted, timingSource = "XAML Rendering callbacks, not GPU-present FPS",
                thumbnailSize = _thumbnailSize, newPreparations = MediaThumbnail.PreparationCount - preparationBefore,
                overlappedPreparation = MediaThumbnail.PreparationCount > preparationBefore,
                cachedPreviews = MediaThumbnail.Decoded.Count, decodedBytes = MediaThumbnail.Decoded.Bytes,
                uiDecodeAttempts = MediaThumbnail.UiDecodeAttempts, uiImageSourceCreations = MediaThumbnail.SourceCreationCount,
                pendingPresentations = MediaThumbnail.PendingPresentations,
                maximumPresentationBatchMs = MediaThumbnail.MaximumPresentationBatchMs,
                gcPauseMilliseconds = (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds,
                gen2Collections = GC.CollectionCount(2) - gen2Before,
                garbageCollectionLatency = System.Runtime.GCSettings.LatencyMode.ToString(),
                heartbeatSamples = waits.Length, dispatcherWaitP95Ms = Percentile(waits, .95),
                dispatcherWaitP99Ms = Percentile(waits, .99), dispatcherWaitMaxMs = waits.LastOrDefault(),
                dispatcherWaitsOver50ms = waits.Count(value => value > 50),
                nativeUpdateGapP95Ms = Percentile(gaps, .95), nativeUpdateGapMaxMs = gaps.LastOrDefault(),
                osInputReplay = false, hardwarePresentTrace = false, paidJobsStarted = false,
                measurement = "App-driven native scroll requests and bounded UI-queue heartbeat while real-library thumbnails warm; not physical mouse replay, display FPS, or a controlled cold-cache benchmark",
                version = typeof(App).Assembly.GetName().Version?.ToString(),
            }, new JsonSerializerOptions { WriteIndented = true }));
        } catch (Exception error) {
            errors.Add(error.GetType().Name + ": " + error.Message);
            await File.WriteAllTextAsync(Path.Combine(output, "startup-scroll.json"), JsonSerializer.Serialize(new { completed = false, errors }));
        } finally {
            if (renderTick is not null) CompositionTarget.Rendering -= renderTick;
            _scrollRefresh.End(); cancellation.Cancel();
            if (heartbeat is not null) try { await heartbeat; } catch (OperationCanceledException) { }
            if (!_lifetime.IsCancellationRequested) {
                _browseThumbTracking = trackingBefore;
                MediaThumbnail.SetThumbInput(trackingBefore);
                if (stateBefore == OverlappedPresenterState.Maximized) presenter.Maximize(); else presenter.Restore();
                ApplyThumbnailSize(sizeBefore, false);
                _pixelScroll?.JumpTo(0);
            }
        }
        static double Percentile(double[] values, double percent) => values.Length == 0 ? 0 : values[(int)((values.Length - 1) * percent)];
    }
}
