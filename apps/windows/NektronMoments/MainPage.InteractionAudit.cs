using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using NektronMoments.Controls;
using NektronMoments.Models;
using NektronMoments.Services;

namespace NektronMoments;

public sealed partial class MainPage
{
    private async Task AuditInteractionsAsync(string output, string path)
    {
        var samples = new List<object>();
        var root = (FrameworkElement)App.MainWindowInstance!.Content;
        var previousTheme = root.RequestedTheme;
        var previousStore = _orderStore; var previousProgress = _metadataProgress;
        var preferenceSize = UserPreferences.Number("thumbnailSize", 240);
        var previousUpscale = Viewer.AllowUpscale;
        _orderStore = new(Path.Combine(output, "audit-arrangements"));
        var rows = await Task.Run(() => Enumerable.Range(0, 1500).Select(i => new MediaItem {
            Key = "flow-" + i, Name = $"Photo {i:0000}.png", Path = path, Paths = [path], MediaType = "Photo", Captured = "2026-09-20"
        }).ToArray());
        try {
            for (var cycle = 0; cycle < 3; cycle++) {
                var currentCycle = cycle;
                await Measure("catalog publication (fixture)", () => { Items.ReplaceAll(rows); return Task.CompletedTask; });
                await Measure("size presets", async () => {
                    foreach (var size in new[] { 144d, 336, 240 }) { ApplyThumbnailSize(size, false); await Task.Delay(80); }
                });
                await Measure("thumbnail slider", async () => {
                    BeginThumbnailSizing();
                    for (var i = 0; i < 36; i++) { PreviewThumbnailSizing(112 + 320 * (.5 - .5 * Math.Cos(i * Math.PI / 9))); ApplySizingFrame(true); await Task.Delay(16); }
                    CommitThumbnailSizing();
                });
                await Measure("wheel train after resize", async () => {
                    for (var i = 0; i < 12; i++) { QueueGalleryWheel(i < 8 ? -120 : 120); await Task.Delay(20); }
                });
                await Measure("native scrollbar after wheel", async () => {
                    _pixelScroll!.YieldToNativeInput();
                    var provider = (IScrollProvider)new ScrollViewerAutomationPeer(_pixelScroll.Scroll).GetPattern(PatternInterface.Scroll);
                    foreach (var percent in new[] { 80d, 20, 65, 0 }) { provider.SetScrollPercent(-1, percent); await Task.Delay(45); }
                });
                await Measure("reorder commit", async () => {
                    var old = BrowseItems.ToArray(); var catalog = Items.Capture();
                    MoveBrowseItem(0, 9); await CommitArrangementAsync(catalog, old);
                });
                await Measure("viewer open", () => OpenCanvasAsync(false, BrowseItems[9]));
                await Measure("viewer navigation", async () => { await Viewer.MoveAsync(1); await Viewer.MoveAsync(-1); });
                await Measure("slideshow controls", async () => { await Viewer.ToggleSlideshowAsync(); Viewer.Stop(); });
                await Measure("enlargement toggle", () => { Viewer.SetAllowUpscale(!Viewer.AllowUpscale); return Task.CompletedTask; });
                await Measure("viewer close", async () => { ReturnToGallery(); await Task.Yield(); });
                await Measure("details pane layout (fixture)", async () => {
                    FillDetails(BrowseItems[0], null, "Generated metadata fixture");
                    DetailsSplit.IsPaneOpen = true; await Task.Delay(100); DetailsSplit.IsPaneOpen = false;
                });
                await Measure("theme switch", async () => {
                    root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                    await Task.Delay(80);
                });
                await Measure("processing window reopen (fixture)", async () => {
                    _metadataProgress = new(); _metadataProgress.Source("Generated photos", 1, 1);
                    _metadataProgress.Report("Processing media · 50/100 · 120 files/s");
                    var show = ShowProcessingProgressAsync(); await Task.Delay(120);
                    _processingWindow?.Hide(); await show;
                    show = ShowProcessingProgressAsync(); await Task.Delay(120); _processingWindow?.Hide(); await show;
                });
                await Measure("window resize", async () => {
                    App.MainWindowInstance.AppWindow.Resize(new Windows.Graphics.SizeInt32(980, 780)); await Task.Delay(120);
                    App.MainWindowInstance.AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 960)); await Task.Delay(120);
                });
                await Measure("settings dialog", async () => {
                    var show = ShowSettingsAsync(); await Task.Delay(120); _commonDialog?.Hide(); await show;
                });
                await Measure("menu open and close", async () => {
                    var menu = AssetDescendants(Root).OfType<MenuBarItem>().First();
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(menu);
                    var provider = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);
                    provider.Expand(); await Task.Delay(80); provider.Collapse();
                });
                await Measure("maximize and restore", async () => {
                    var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)App.MainWindowInstance.AppWindow.Presenter;
                    presenter.Maximize(); await Task.Delay(160); presenter.Restore(); await Task.Delay(160);
                });
                await Measure("fullscreen round trip", async () => {
                    ToggleFullScreen(); await Task.Delay(160); ToggleFullScreen(); await Task.Delay(160);
                });
                async Task Measure(string operation, Func<Task> work) {
                    var waits = new List<double>(); using var cancel = new CancellationTokenSource();
                    var heartbeat = Task.Run(async () => {
                        while (!cancel.IsCancellationRequested) {
                            var queued = Stopwatch.GetTimestamp(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            if (!DispatcherQueue.TryEnqueue(() => { waits.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds); done.TrySetResult(); })) break;
                            await done.Task.WaitAsync(cancel.Token); await Task.Delay(16, cancel.Token);
                        }
                    });
                    var allocated = GC.GetTotalAllocatedBytes(false); var gen2 = GC.CollectionCount(2); var gc = GC.GetTotalPauseDuration();
                    var clock = Stopwatch.StartNew(); var elapsed = 0d;
                    try { await work(); elapsed = clock.Elapsed.TotalMilliseconds; await Task.Delay(150); }
                    finally { cancel.Cancel(); try { await heartbeat; } catch (OperationCanceledException) { } }
                    waits.Sort(); using var process = Process.GetCurrentProcess();
                    samples.Add(new { cycle = currentCycle, operation, operationMs = elapsed,
                        uiWaitP95Ms = waits.Count == 0 ? (double?)null : waits[(int)((waits.Count - 1) * .95)], uiWaitMaxMs = waits.Count == 0 ? (double?)null : waits[^1],
                        gcPauseMs = (GC.GetTotalPauseDuration() - gc).TotalMilliseconds, gen2 = GC.CollectionCount(2) - gen2,
                        allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated, privateMiB = process.PrivateMemorySize64 / 1048576d,
                        browsing = BrowseItems.Count, realized = Gallery.ItemsPanelRoot?.Children.Count ?? 0,
                        sizingActive = _isThumbnailSizing, sizingVisuals = _sizingTiles.Count,
                        retiredResources = _sizingRetirement?.Retired ?? 0, pendingRetirement = _sizingRetirement?.Pending ?? 0,
                        retirementFailures = _sizingRetirement?.Failures ?? 0, maximumRetirementBatchMs = _sizingRetirement?.MaximumBatchMs ?? 0,
                        pendingPreviews = MediaThumbnail.PendingLoads, pendingPresentation = MediaThumbnail.PendingPresentations });
                }
            }
        } finally {
            CommitThumbnailSizing(); Viewer.Close(); LibraryCanvas.Visibility = Visibility.Visible;
            _processingWindow?.ClosePermanently(); _processingTimer?.Stop(); _metadataProgress = previousProgress; _orderStore = previousStore;
            root.RequestedTheme = previousTheme; Viewer.SetAllowUpscale(previousUpscale);
            await UserPreferences.SetAsync("thumbnailSize", preferenceSize);
            await UserPreferences.SetAsync("allowUpscale", previousUpscale);
            await File.WriteAllTextAsync(Path.Combine(output, "interaction-audit.json"), JsonSerializer.Serialize(new {
                samples, generatedMediaOnly = true, paidJobsStarted = false, liveSyncStarted = false,
                osPointerReplay = false, measurement = "App-driven operation and UI-queue latency with generated media; not hardware-present FPS or a physical mouse replay.",
                version = typeof(App).Assembly.GetName().Version?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
