using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NektronMoments.Controls;
using NektronMoments.Models;
using NektronMoments.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace NektronMoments;

public sealed partial class MainPage
{
    private LibraryOrderStore _orderStore = new();
    private string? _sortChoice;
    private string _activeSort = "newest";
    private bool _reorderActive, _orderCommitActive;
    private TaskCompletionSource? _arrangementFinished;
    private MediaItem[]? _dragOriginal;
    private CatalogView? _dragCatalog;
    private MediaItem? _draggedItem;
    private bool _dropAccepted;
    private Windows.Foundation.Point? _dragPoint;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragScrollTimer;
    private long _dragScrollStamp;
    private ThumbnailReorderMotion? _reorderMotion;
    private const string ReorderDataFormat = "NektronMoments.LibraryReorder";
    private static void PrepareReorderData(DataPackage data) {
        // This custom drag transfers order intent, never original storage items.
        data.SetData(ReorderDataFormat, "local-view"); data.RequestedOperation = DataPackageOperation.Move;
    }
    private void MoveBrowseItem(int from, int to) => (_reorderMotion ??= new(Gallery, _pixelScroll?.Scroll)).Move(BrowseItems, from, to);
    private string ArrangementScope => (_overview?.LibraryId is { Length: > 0 } id ? id : _bridge.Workspace) + "|" + _sourceId + "|" + _mediaType;
    private void UpdateArrangementChrome()
    {
        SortButton.Content = SearchBox.Text.Length > 0 ? "Search results" : _activeSort == "custom" ? "Custom order" : _activeSort == "oldest" ? "Oldest first ↑" : "Newest first ↓";
        var allowed = SearchBox.Text.Length == 0 && !_hideScreenshots && !_loadingCatalog && !_isThumbnailSizing;
        Gallery.CanDragItems = allowed;
        Gallery.CanReorderItems = false; // We preview the observable order live, rather than waiting for a native drop.
        ToolTipService.SetToolTip(SortButton, allowed ? "Drag photos to arrange them. Your custom order is saved on this computer." : "Clear the search and show screenshots to rearrange the complete view.");
    }
    private async void ChooseSort(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sort }) await SetSortAsync(sort);
    }
    private async Task SetSortAsync(string sort)
    {
        if (_reorderActive || _orderCommitActive) return;
        _sortChoice = sort;
        try { await _orderStore.SaveAsync(ArrangementScope, sort); }
        catch (Exception) { StatusText.Text = "Sort changed for this session; the preference could not be saved."; }
        await LoadCatalogAsync();
    }
    private void GalleryDragStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (_hideScreenshots || _loadingCatalog || _orderCommitActive || _isThumbnailSizing || Viewer.IsOpen || SearchBox.Text.Length != 0 || args.Items.Count != 1) { args.Cancel = true; return; }
        _dragOriginal = BrowseItems.ToArray(); _dragCatalog = Items.Capture();
        _draggedItem = (MediaItem)args.Items[0]; _dropAccepted = false; _dragPoint = null;
        _reorderActive = true;
        _arrangementFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pixelScroll?.YieldToNativeInput(); _thumbnailPrefetch?.Cancel();
        MediaThumbnail.SetThumbInput(true);
        PrepareReorderData(args.Data);
        _dragScrollTimer ??= DispatcherQueue.CreateTimer();
        _dragScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _dragScrollTimer.Tick -= ScrollDuringReorder; _dragScrollTimer.Tick += ScrollDuringReorder;
        _dragScrollStamp = System.Diagnostics.Stopwatch.GetTimestamp(); _dragScrollTimer.Start();
        StatusText.Text = "Drag to arrange · Release to save · Esc to cancel · Originals stay in place";
    }
    private void GalleryDragOver(object sender, DragEventArgs args)
    {
        if (!_reorderActive || !args.DataView.Contains(ReorderDataFormat)) { args.AcceptedOperation = DataPackageOperation.None; args.Handled = true; }
        else {
            args.AcceptedOperation = DataPackageOperation.Move; args.Handled = true;
            _dragPoint = args.GetPosition(Gallery); PreviewReorderAt(_dragPoint.Value);
        }
    }
    private void GalleryDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (!_reorderActive || !args.DataView.Contains(ReorderDataFormat)) { args.AcceptedOperation = DataPackageOperation.None; return; }
        PreviewReorderAt(args.GetPosition(Gallery));
        _dropAccepted = true; args.AcceptedOperation = DataPackageOperation.Move;
    }
    private void GalleryDragLeave(object sender, DragEventArgs args) { _dragPoint = null; }
    private void PreviewReorderAt(Windows.Foundation.Point point)
    {
        if (_draggedItem is null || BrowseItems.Count == 0 || Gallery.ItemsPanelRoot is not ItemsWrapGrid panel || panel.ItemWidth <= 0 || panel.ItemHeight <= 0) return;
        if (point.X < 0 || point.X >= Gallery.ActualWidth - 28 || point.Y < 0 || point.Y >= Gallery.ActualHeight) return;
        var columns = GetGalleryColumns(panel.ItemWidth);
        var x = Math.Max(0, point.X - Gallery.Padding.Left);
        var y = Math.Max(0, point.Y + (_pixelScroll?.Scroll.VerticalOffset ?? 0) - Gallery.Padding.Top);
        var raw = (int)Math.Floor(y / panel.ItemHeight) * columns + Math.Min(columns - 1, (int)(x / panel.ItemWidth));
        var target = Math.Clamp(raw, 0, BrowseItems.Count - 1);
        PreviewReorder(target, raw >= BrowseItems.Count || x % panel.ItemWidth > panel.ItemWidth / 2);
    }
    private void PreviewReorder(int target, bool after)
    {
        if (_draggedItem is null) return;
        var from = BrowseItems.IndexOf(_draggedItem);
        var to = ReorderPolicy.Destination(from, target, after, BrowseItems.Count);
        if (to >= 0 && to != from) MoveBrowseItem(from, to);
    }
    private void ScrollDuringReorder(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_reorderActive || _dragPoint is not { } point || _pixelScroll is null || Gallery.ActualHeight <= 0) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var seconds = Math.Min(.05, System.Diagnostics.Stopwatch.GetElapsedTime(_dragScrollStamp, now).TotalSeconds); _dragScrollStamp = now;
        var edge = Math.Min(48, Gallery.ActualHeight / 4);
        var speed = point.Y < edge ? -360 * (1 - Math.Max(0, point.Y) / edge) :
            point.Y > Gallery.ActualHeight - edge ? 360 * (1 - Math.Max(0, Gallery.ActualHeight - point.Y) / edge) : 0;
        if (speed == 0 || point.X < 0 || point.X >= Gallery.ActualWidth - 28) return;
        var scroll = _pixelScroll.Scroll;
        scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + speed * seconds, 0, scroll.ScrollableHeight), null, true);
        PreviewReorderAt(point);
    }
    private void RestoreDragOrder(MediaItem[] before)
    {
        if (_draggedItem is { } item) {
            var from = BrowseItems.IndexOf(item); var to = Array.IndexOf(before, item);
            if (from >= 0 && to >= 0 && from != to) MoveBrowseItem(from, to);
        }
        if (!BrowseItems.SequenceEqual(before)) BrowseItems.ReplaceAll(before);
    }
    private async void GalleryDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var before = _dragOriginal; var catalog = _dragCatalog;
        _dragOriginal = null; _dragCatalog = null; _reorderActive = false;
        MediaThumbnail.SetThumbInput(_browseThumbTracking);
        if (before is null || catalog is null) { FinishArrangement(); return; }
        if (!_dropAccepted || args.DropResult != DataPackageOperation.Move || !ReferenceEquals(catalog, Items.Capture())) {
            if (ReferenceEquals(catalog, Items.Capture())) RestoreDragOrder(before);
            FinishArrangement(); QueueGalleryWork(); return;
        }
        if (BrowseItems.SequenceEqual(before)) { FinishArrangement(); QueueGalleryWork(); return; }
        await CommitArrangementAsync(catalog, before);
    }
    private async Task CommitArrangementAsync(CatalogView catalog, MediaItem[] before)
    {
        _orderCommitActive = true;
        _arrangementFinished ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        var desired = BrowseItems.ToArray(); var scope = ArrangementScope; var generation = _generation;
        var baseSort = _ascending ? "oldest" : "newest";
        try {
            var reordered = await Task.Run(() => catalog.WithPrefix(desired));
            if (generation != _generation || !ReferenceEquals(Items.Capture(), catalog)) return;
            Items.ReplaceView(reordered, notify: false); // Native drag already moved only the affected containers.
            ++_browseVersion; _thumbnailPrefetch?.Cancel(); _warmGeneration = -1;
            _activeSort = _sortChoice = "custom";
            UpdateArrangementChrome();
            StatusText.Text = "Custom order · Saving arrangement…";
            await _orderStore.SaveAsync(scope, "custom", desired.Select(item => item.Key).ToArray(), desired.Select(item => item.Path).ToArray(), baseSort);
            if (scope == ArrangementScope) StatusText.Text = "Custom order saved · Originals have not moved";
        } catch (Exception) {
            if (ReferenceEquals(catalog, Items.Capture())) BrowseItems.ReplaceAll(before);
            StatusText.Text = "The arrangement could not be saved. Your originals are unchanged.";
        } finally { _orderCommitActive = false; FinishArrangement(); QueueGalleryWork(); }
    }
    private void FinishArrangement() {
        _dragScrollTimer?.Stop(); _dragPoint = null; _draggedItem = null; _dropAccepted = false;
        var finished = _arrangementFinished; _arrangementFinished = null; finished?.TrySetResult();
    }
    private async void GalleryReorderKey(object sender, KeyRoutedEventArgs args)
    {
        if (_hideScreenshots || _reorderActive || _orderCommitActive || _loadingCatalog || _isThumbnailSizing || SearchBox.Text.Length > 0) return;
        Func<VirtualKey, CoreVirtualKeyStates> input = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
        if ((input(VirtualKey.Control) & CoreVirtualKeyStates.Down) == 0 || (input(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == 0) return;
        var delta = args.Key is VirtualKey.Left or VirtualKey.Up ? -1 : args.Key is VirtualKey.Right or VirtualKey.Down ? 1 : 0;
        if (delta == 0 || Gallery.SelectedItem is not MediaItem item) return;
        var index = BrowseItems.IndexOf(item); var target = index + delta;
        if (index < 0 || target < 0 || target >= BrowseItems.Count) return;
        args.Handled = true;
        var before = BrowseItems.ToArray(); var catalog = Items.Capture();
        MoveBrowseItem(index, target);
        await CommitArrangementAsync(catalog, before);
        Gallery.SelectedItem = item; Gallery.ScrollIntoView(item);
    }
}
