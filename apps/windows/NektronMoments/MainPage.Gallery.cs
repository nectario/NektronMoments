using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NektronMoments.Controls;
using NektronMoments.Models;
using NektronMoments.Services;

namespace NektronMoments;

public sealed partial class MainPage
{
    private readonly SemaphoreSlim _catalogGate = new(1);
    private PixelWheelScroller? _pixelScroll;
    private NativeGalleryWheelBridge? _nativeWheel;

    private void GalleryLoaded(object sender, RoutedEventArgs e)
    {
        if (_pixelScroll is not null) return;
        var scroll = AssetDescendants(Gallery).OfType<ScrollViewer>().FirstOrDefault();
        if (scroll?.Content is UIElement) {
            foreach (var bar in AssetDescendants(scroll).OfType<ScrollBar>().Where(bar => bar.Orientation == Orientation.Vertical)) {
                _galleryScrollbar = bar;
                bar.Width = bar.MinWidth = 24;
                bar.ApplyTemplate();
                foreach (var thumb in AssetDescendants(bar).OfType<Thumb>().Where(thumb => thumb.Name == "VerticalThumb")) {
                    _galleryThumb = thumb;
                    thumb.DragStarted += (_, _) => {
                        _browseThumbTracking = true; MediaThumbnail.SetThumbInput(true);
                        DiagnosticTrace.Thumb("Start", scroll.VerticalOffset);
                    };
                    thumb.DragDelta += (_, _) => DiagnosticTrace.Thumb("Delta", scroll.VerticalOffset);
                    thumb.DragCompleted += (_, _) => {
                        DiagnosticTrace.Thumb("Stop", scroll.VerticalOffset);
                        _browseThumbTracking = false; MediaThumbnail.SetThumbInput(false); QueueGalleryWork();
                    };
                    thumb.Width = thumb.MinWidth = 14; thumb.MinHeight = 48;
                    thumb.Template = (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Thumb\"><Border CornerRadius=\"7\" Background=\"{TemplateBinding Background}\"/></ControlTemplate>");
                }
                foreach (var panning in AssetDescendants(bar).OfType<Border>().Where(part => part.Name == "VerticalPanningThumb")) {
                    panning.Width = panning.MinWidth = 12; panning.MinHeight = 48; panning.Margin = new Thickness(6, 0, 6, 0);
                }
            }
            _pixelScroll = new PixelWheelScroller(scroll) {
                WheelDistance = UserPreferences.Number("wheelPixelsPerNotch", PixelScrollMotion.PixelsPerNotch),
            };
            _pixelScroll.Settled += GalleryMotionSettled;
            scroll.ViewChanged += (_, args) => {
                MediaThumbnail.NotifyScrollInput();
                QueueGalleryWork(); if (!args.IsIntermediate) GalleryMotionSettled();
            };
            scroll.SizeChanged += (_, _) => QueueGalleryWork();
            if (App.MainWindowInstance is { } window)
                _nativeWheel = new NativeGalleryWheelBridge(window, GalleryScrollHost,
                    () => IsHitTestVisible && !Viewer.IsOpen && !_dialog && LibraryCanvas.Visibility == Visibility.Visible,
                    QueueGalleryWheel);
        }
    }
    private bool _controlsReady, _settingSize;
    private bool _expandedViewportCache;
    private double _thumbnailSize = 240;
    private int _highestVisible, _warmStart = -1, _warmEnd, _warmGeneration = -1;
    private int _browseDirection = 1, _warmDirection, _warmExposedCount;
    private uint _warmPixels;
    private CancellationTokenSource? _thumbnailPrefetch, _sizeChange;
    private bool _galleryWorkQueued;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewCounterTimer;
    private void StartPreviewCounter()
    {
        if (_previewCounterTimer is null) {
            _previewCounterTimer = DispatcherQueue.CreateTimer();
            _previewCounterTimer.Interval = TimeSpan.FromMilliseconds(500);
            _previewCounterTimer.Tick += (_, _) => {
                if (!_browseThumbTracking && !_isThumbnailSizing && _pixelScroll?.IsAnimating != true) UpdateBrowseCounter();
                if (_lifetime.IsCancellationRequested || (_thumbnailPrefetch is null && MediaThumbnail.PendingLoads == 0))
                    _previewCounterTimer.Stop();
            };
        }
        if (!_previewCounterTimer.IsRunning) _previewCounterTimer.Start();
    }
    private void QueueGalleryWheel(int delta)
    {
        if (_isThumbnailSizing || _pixelScroll is null || _reorderActive || _orderCommitActive) return;
        MediaThumbnail.NotifyScrollInput();
        CancelScrollbarGesture(); _pixelScroll.QueueWheel(delta);
        GalleryMotionSettled();
    }
    private async void QueueGalleryWork()
    {
        if (_galleryWorkQueued || !_ready || !UsesBrowseProjection || Viewer.IsOpen || _browseThumbTracking || _isThumbnailSizing || _reorderActive || _orderCommitActive ||
            Items.Count == 0 || _lifetime.IsCancellationRequested) return;
        _galleryWorkQueued = true;
        var version = _browseVersion;
        var extended = false;
        try {
            await Task.Delay(100, _lifetime.Token);
            if (!UsesBrowseProjection || Viewer.IsOpen || _browseThumbTracking || _isThumbnailSizing || _reorderActive || _orderCommitActive || version != _browseVersion) return;
            ResizeGallery();
            if (Gallery.ItemsPanelRoot is ItemsWrapGrid panel) {
                var first = Math.Max(0, panel.FirstVisibleIndex);
                if (first != _highestVisible) _browseDirection = first > _highestVisible ? 1 : -1;
                _highestVisible = first;
            }
            extended = TryExtendBrowsing();
            PrepareNextBrowsing();
            ScheduleThumbnailWarm();
            if (!_browseThumbTracking && _pixelScroll?.IsAnimating != true) UpdateBrowseCounter();
        } catch (OperationCanceledException) { }
        finally {
            _galleryWorkQueued = false;
            // Recheck AFTER layout: a maximized viewport may fit more than 200
            // small tiles. Visible indices, never cache completion, drive growth.
            if ((extended || version != _browseVersion) && !_lifetime.IsCancellationRequested)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, QueueGalleryWork);
        }
    }

    private async Task ExpandViewportCacheAsync()
    {
        try {
            await Task.Delay(300, _lifetime.Token);
            while (MediaThumbnail.IsInputActive) await Task.Delay(100, _lifetime.Token);
            _expandedViewportCache = true;
            ResizeGallery();
        } catch (OperationCanceledException) { }
    }

    private void ScheduleThumbnailWarm()
    {
        if (!_ready || !UsesBrowseProjection || _thumbnailWarmingSuspended || Viewer.IsOpen || _reorderActive || _orderCommitActive ||
            _isThumbnailSizing || Items.Count == 0 || _lifetime.IsCancellationRequested) return;
        var panel = Gallery.ItemsPanelRoot as ItemsWrapGrid;
        var first = Math.Clamp(panel?.FirstVisibleIndex ?? _highestVisible, 0, Items.Count - 1);
        var last = Math.Clamp(panel?.LastVisibleIndex ?? first, first, Items.Count - 1);
        var pixels = MediaThumbnail.TargetPixels;
        var advanceThreshold = Math.Clamp((last - first + 1) / 2, 16, 64);
        if (_warmGeneration == _generation && _warmPixels == pixels && _warmDirection == _browseDirection &&
            _warmExposedCount == BrowseItems.Count && Math.Abs(first - _warmStart) < advanceThreshold &&
            Math.Abs(last - _warmEnd) < advanceThreshold) return;
        _warmStart = first; _warmEnd = last; _warmPixels = pixels; _warmGeneration = _generation;
        _warmDirection = _browseDirection; _warmExposedCount = BrowseItems.Count;
        _thumbnailPrefetch?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _thumbnailPrefetch = cancellation;
        StartPreviewCounter();
        var profile = PerformanceProfile.Current;
        var maximum = BrowsingPolicy.WarmCount(profile.ThumbnailAhead, pixels, profile.DecodedBytes / 2);
        var catalog = Items.Capture();
        var exposedCount = BrowseItems.Count;
        var direction = _browseDirection;
        _ = Task.Run(WarmAsync);
        async Task WarmAsync() {
            try {
                var indexes = BrowsingPolicy.PrefetchIndexes(catalog.Count, first, last, exposedCount, direction, maximum);
                await Parallel.ForEachAsync(indexes, new ParallelOptions {
                    MaxDegreeOfParallelism = profile.PrefetchWorkers,
                    CancellationToken = cancellation.Token,
                }, async (index, token) => {
                    try {
                        var item = catalog[index];
                        // Ready images are cached independently of XAML tile creation
                        // and independently of the 200-photo scrollbar projection.
                        await MediaThumbnail.PrepareAsync(item, pixels, token, prefetch: true);
                    } catch (OperationCanceledException) { }
                    catch (Exception) { /* One unsupported original must not stop useful lookahead. */ }
                });
            } catch (OperationCanceledException) { }
            finally {
                // Only this plan's waiters are cancelled. Shared work already
                // useful to a visible tile is allowed to finish and enter cache.
                if (!DispatcherQueue.TryEnqueue(() => {
                    if (ReferenceEquals(_thumbnailPrefetch, cancellation)) _thumbnailPrefetch = null;
                    cancellation.Dispose();
                    if (!_browseThumbTracking) UpdateBrowseCounter();
                })) cancellation.Dispose();
            }
        }
    }
    private Task<MediaItem?> ItemAtAsync(int index)
    {
        var catalog = Items.Capture();
        return Task.Run(() => index >= 0 && index < catalog.Count ? catalog[index] : null, _lifetime.Token);
    }
    private async Task OpenCanvasAsync(bool slideshow, MediaItem? requestedItem = null)
    {
        if (Items.Count == 0 || _reorderActive || _orderCommitActive) return;
        // Resolve the clicked identity before changing any layout. A recycled or
        // removed tile must never silently open the first photo instead.
        var selected = requestedItem ?? _selected;
        var index = selected is null ? 0 : Items.IndexOf(selected);
        if (index < 0) return;
        CancelScrollbarGesture();
        _pixelScroll?.Stop();
        CancelSelectionDetails();
        _selected = selected;
        _thumbnailPrefetch?.Cancel();
        DetailsSplit.IsPaneOpen = false;
        LibraryCanvas.Visibility = Visibility.Collapsed;
        await Viewer.OpenAsync(index, slideshow);
    }
    private async void ReturnToGallery()
    {
        _pixelScroll?.Stop();
        Viewer.Close(); LibraryCanvas.Visibility = Visibility.Visible;
        var version = _browseVersion;
        var generation = _generation;
        if (_selected is { } selected && Items.IndexOf(selected) is var index && index >= 0) {
            try {
                var count = (int)Math.Min(Items.Count, ((long)index / BrowsingBatch + 1) * BrowsingBatch);
                await Items.PreparePrefixAsync(count, _lifetime.Token);
                if (version != _browseVersion || generation != _generation || Viewer.IsOpen ||
                    !ReferenceEquals(_selected, selected)) return;
                var item = Items[index];
                EnsureBrowseIncludes(index);
                Gallery.SelectedItem = item; Gallery.ScrollIntoView(item);
            } catch (OperationCanceledException) { return; }
            catch (Exception ex) { ShowError(ex); return; }
        }
        _warmStart = -1; _warmGeneration = -1;
        ScheduleThumbnailWarm();
    }
    private async void StartSlideshow(object sender, RoutedEventArgs e) => await OpenCanvasAsync(true);
    private async void ToggleDetails(object sender, RoutedEventArgs e)
    {
        DetailsSplit.IsPaneOpen = !DetailsSplit.IsPaneOpen;
        if (DetailsSplit.IsPaneOpen && _selected is { } item) await LoadSelectedDetailsAsync(item);
        else CancelSelectionDetails();
    }
    private bool ViewerOwnsKeys() => Viewer.IsOpen && !_dialog &&
        FocusManager.GetFocusedElement(XamlRoot) is not TextBox and not ComboBox;
    private async void ViewerPreviousKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) {
        if (ViewerOwnsKeys()) { args.Handled = true; await Viewer.MoveAsync(-1); }
    }
    private async void ViewerNextKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) {
        if (ViewerOwnsKeys()) { args.Handled = true; await Viewer.MoveAsync(1); }
    }
    private async void ViewerPlayKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) {
        if (ViewerOwnsKeys()) { args.Handled = true; await Viewer.ToggleSlideshowAsync(); }
    }
    private void FullScreenKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) {
        args.Handled = true; ToggleFullScreen();
    }
    private void ToggleFullScreen()
    {
        var window = App.MainWindowInstance?.AppWindow;
        if (window is null) return;
        window.SetPresenter(window.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
            ? Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped : Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }
    private void SizePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_controlsReady || _settingSize) return;
        if (SizePreset.SelectedItem is ComboBoxItem choice && double.TryParse(choice.Tag?.ToString(), out var size) && size > 0)
            ApplyThumbnailSize(size, true);
    }
    private void ThumbnailSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isThumbnailSizing) { PreviewThumbnailSizing(e.NewValue); return; }
        if (_controlsReady && !_settingSize) ApplyThumbnailSize(e.NewValue, true);
    }
    private void ApplyThumbnailSize(double size, bool save)
    {
        CancelScrollbarGesture();
        _pixelScroll?.Stop();
        _thumbnailSize = ViewingPolicy.ThumbnailWidth(size);
        _settingSize = true;
        ThumbnailSlider.Value = _thumbnailSize;
        SizePreset.SelectedIndex = _thumbnailSize == 144 ? 0 : _thumbnailSize == 240 ? 1 : _thumbnailSize == 336 ? 2 : _thumbnailSize == 432 ? 3 : 4;
        _settingSize = false;
        ResizeGallery();
        _sizeChange?.Cancel();
        var cancellation = new CancellationTokenSource(); _sizeChange = cancellation;
        _ = ApplyAsync();
        async Task ApplyAsync() {
            try {
                await Task.Delay(180, cancellation.Token);
                MediaThumbnail.SetResolution(ViewingPolicy.ThumbnailPixels(_thumbnailSize, XamlRoot?.RasterizationScale ?? 1));
                _warmGeneration = -1; QueueGalleryWork(); ScheduleThumbnailWarm();
                if (save) await UserPreferences.SetAsync("thumbnailSize", _thumbnailSize);
            } catch (OperationCanceledException) { }
            catch (Exception) { }
            finally { if (ReferenceEquals(_sizeChange, cancellation)) _sizeChange = null; cancellation.Dispose(); }
        }
    }
    private void UpdateThemeChrome()
    {
        if (!_controlsReady) return;
        var dark = ActualTheme == ElementTheme.Dark;
        ThemeButton.Label = dark ? "Dark theme" : "Light theme";
        ThemeMenu.Text = dark ? "Switch to light theme" : "Switch to dark theme";
        ToolTipService.SetToolTip(ThemeButton, ThemeMenu.Text);
        _processingWindow?.SetTheme(ActualTheme);
        UpdateSourceIcons();
    }
}
