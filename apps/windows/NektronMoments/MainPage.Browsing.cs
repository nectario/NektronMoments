using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NektronMoments.Controls;
using NektronMoments.Models;

namespace NektronMoments;

public sealed partial class MainPage
{
    // The full catalog and prepared-image caches do not define scrollbar extent.
    // Only this growing prefix is presented to the native GridView.
    public BulkObservableCollection<MediaItem> BrowseItems { get; } = [];
    private bool _browseThumbTracking, _extendingBrowsing, _thumbnailWarmingSuspended;
    private int _catalogReportedTotal, _browseVersion;
    private Task? _browsePreparation;
    private int _browsePreparationVersion = -1;
    private bool UsesBrowseProjection => ReferenceEquals(Gallery.ItemsSource, BrowseItems);
    private int BrowsingBatch => BrowsingPolicy.BatchFor(_thumbnailSize);

    private void CatalogItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Add && args.NewStartingIndex >= BrowseItems.Count) {
            // Incremental catalog additions (including small local fixtures) must
            // not reset the current native view. Large snapshots use Reset below.
            AppendBrowsing(Math.Min(Items.PreparedPrefixCount, BrowsingPolicy.InitialCount(Items.Count, BrowsingBatch)));
        } else {
            _reorderMotion?.Stop();
            CancelScrollbarGesture();
            ++_browseVersion;
            _thumbnailPrefetch?.Cancel();
            CancelDisplayWarm();
            _warmStart = -1; _warmGeneration = -1;
            _highestVisible = 0; _browseDirection = 1;
            BrowseItems.ReplaceAll(Items.Take(Math.Min(Items.PreparedPrefixCount, BrowsingPolicy.InitialCount(Items.Count, BrowsingBatch))));
        }
        _catalogReportedTotal = Items.Count;
        UpdateBrowseCounter();
        QueueGalleryWork();
    }

    private void AppendBrowsing(int count)
    {
        count = Math.Clamp(count, 0, Items.Count);
        if (count > Items.PreparedPrefixCount)
            throw new InvalidOperationException("Prepare catalog rows on a worker before publishing them.");
        // WinUI observes individual additions without a Reset. Layout is coalesced
        // after this UI turn, and the existing item identity/viewport is retained.
        for (var index = BrowseItems.Count; index < count; index++) BrowseItems.Add(Items[index]);
    }

    private bool TryExtendBrowsing()
    {
        if (!UsesBrowseProjection || _extendingBrowsing || _isThumbnailSizing || _thumbnailLayoutCommit || _reorderActive || _orderCommitActive ||
            Viewer.IsOpen || LibraryCanvas.Visibility != Visibility.Visible || _lifetime.IsCancellationRequested ||
            Gallery.ItemsPanelRoot is not ItemsWrapGrid panel) return false;
        var visible = Math.Max(1, panel.LastVisibleIndex - panel.FirstVisibleIndex + 1);
        if (_browseThumbTracking || GalleryThumb?.IsDragging == true) return false;
        var minimum = BrowsingPolicy.InitialCount(Items.Count, BrowsingBatch);
        if (BrowseItems.Count >= minimum && !BrowsingPolicy.ShouldExtend(Items.Count, BrowseItems.Count, panel.LastVisibleIndex, visible,
            false, BrowsingBatch)) return false;
        var next = BrowseItems.Count < minimum ? minimum : BrowsingPolicy.NextCount(Items.Count, BrowseItems.Count, BrowsingBatch);
        if (Items.PreparedPrefixCount < next) { PrepareNextBrowsing(); return false; }
        _extendingBrowsing = true;
        try {
            AppendBrowsing(next);
            UpdateBrowseCounter();
        } finally { _extendingBrowsing = false; }
        return true;
    }

    private void EnsureBrowseIncludes(int catalogIndex)
    {
        if (catalogIndex < 0 || catalogIndex >= Items.Count) return;
        var count = (int)Math.Min(Items.Count, ((long)catalogIndex / BrowsingBatch + 1) * BrowsingBatch);
        AppendBrowsing(count);
        UpdateBrowseCounter();
    }

    // Parse just the next browsing batch ahead of input. Decoded-image lookahead
    // still independently prepares thousands of photos, without expanding the bar.
    private void PrepareNextBrowsing()
    {
        var count = BrowsingPolicy.NextCount(Items.Count, BrowseItems.Count, BrowsingBatch);
        if (count <= Items.PreparedPrefixCount || _lifetime.IsCancellationRequested) return;
        if (_browsePreparationVersion == _browseVersion && _browsePreparation is { IsCompleted: false }) return;
        _browsePreparationVersion = _browseVersion;
        _browsePreparation = PrepareAsync(_browseVersion);
        async Task PrepareAsync(int version) {
            try {
                await Items.PreparePrefixAsync(count, _lifetime.Token);
                if (version == _browseVersion)
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, QueueGalleryWork);
            } catch (OperationCanceledException) { }
            catch (Exception ex) { if (version == _browseVersion) ShowError(ex); }
        }
    }

    private void UpdateBrowseCounter()
    {
        if (_lifetime.IsCancellationRequested) return;
        ResultCount.Text = $"{_catalogReportedTotal:N0} items · {BrowseItems.Count:N0} browsing";
        var ready = MediaThumbnail.Decoded.Count;
        if (ready > 0) ResultCount.Text += $" · {ready:N0} cached previews";
    }
}
