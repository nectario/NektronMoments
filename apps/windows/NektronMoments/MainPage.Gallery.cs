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
    private readonly SemaphoreSlim _pagingGate = new(1);
    private PixelWheelScroller? _pixelScroll;

    private void GalleryLoaded(object sender, RoutedEventArgs e)
    {
        if (_pixelScroll is not null) return;
        var scroll = AssetDescendants(Gallery).OfType<ScrollViewer>().FirstOrDefault();
        if (scroll?.Content is UIElement) _pixelScroll = new PixelWheelScroller(scroll);
    }
    private bool _priming, _controlsReady, _settingSize;
    private bool _expandedViewportCache;
    private double _thumbnailSize = 240;
    private int _highestVisible, _warmStart = -1, _warmEnd, _warmGeneration = -1;
    private uint _warmPixels;
    private string _activeQuery = "", _activeSource = "", _activeMediaType = "";
    private bool _activeAscending;
    private CancellationTokenSource? _thumbnailPrefetch, _sizeChange;

    private async Task ExpandViewportCacheAsync()
    {
        try {
            await Task.Delay(300, _lifetime.Token);
            _expandedViewportCache = true;
            ResizeGallery();
        } catch (OperationCanceledException) { }
    }

    private async Task PrimeBufferAsync()
    {
        if (_priming || !_ready || Viewer.IsOpen || _lifetime.IsCancellationRequested) return;
        _priming = true;
        var generation = _generation;
        try {
            await Task.Delay(80, _lifetime.Token);
            while (generation == _generation && _hasMore && !Viewer.IsOpen &&
                   Items.Count - _highestVisible < PerformanceProfile.Current.RecordBuffer) {
                var before = Items.Count;
                await LoadPageAsync(false);
                if (Items.Count == before) break;
                await Task.Delay(20, _lifetime.Token);
            }
        } catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally {
            _priming = false;
            if (generation != _generation && !_lifetime.IsCancellationRequested && !Viewer.IsOpen) _ = PrimeBufferAsync();
        }
    }
    private void ScheduleThumbnailWarm()
    {
        if (!_ready || Viewer.IsOpen || Items.Count == 0) return;
        var start = Math.Max(0, _highestVisible - 96);
        var end = Math.Min(Items.Count, _highestVisible + PerformanceProfile.Current.ThumbnailAhead);
        var pixels = MediaThumbnail.TargetPixels;
        if (_warmGeneration == _generation && _warmPixels == pixels &&
            Math.Abs(start - _warmStart) < 48 && end <= _warmEnd) return;
        _warmStart = start; _warmEnd = end; _warmPixels = pixels; _warmGeneration = _generation;
        _thumbnailPrefetch?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _thumbnailPrefetch = cancellation;
        var candidates = Items.Skip(start).Take(end - start).ToArray();
        _ = WarmAsync();
        async Task WarmAsync() {
            try {
                await Parallel.ForEachAsync(candidates, new ParallelOptions {
                    MaxDegreeOfParallelism = PerformanceProfile.Current.PrefetchWorkers,
                    CancellationToken = cancellation.Token,
                }, async (item, token) => {
                    try { await ThumbnailService.Shared.LoadAsync(item, pixels, token, prefetch: true); }
                    catch (OperationCanceledException) { }
                    catch (Exception) { /* A bad thumbnail must not stop the look-ahead window. */ }
                });
            } catch (OperationCanceledException) { }
            finally { if (ReferenceEquals(_thumbnailPrefetch, cancellation)) _thumbnailPrefetch = null; cancellation.Dispose(); }
        }
    }
    private async Task<MediaItem?> ItemAtAsync(int index)
    {
        if (index < 0) return null;
        var generation = _generation;
        while (index >= Items.Count && _hasMore && generation == _generation) {
            var before = Items.Count;
            await LoadPageAsync(false);
            if (Items.Count == before) break;
        }
        return generation == _generation && index < Items.Count ? Items[index] : null;
    }
    private async Task OpenCanvasAsync(bool slideshow)
    {
        if (Items.Count == 0) return;
        _pixelScroll?.Stop();
        ++_selectionGeneration;
        _thumbnailPrefetch?.Cancel();
        DetailsSplit.IsPaneOpen = false;
        LibraryCanvas.Visibility = Visibility.Collapsed;
        var index = _selected is null ? 0 : Items.ToList().FindIndex(x => x.Key == _selected.Key);
        await Viewer.OpenAsync(Math.Max(0, index), slideshow);
    }
    private void ReturnToGallery()
    {
        _pixelScroll?.Stop();
        Viewer.Close(); LibraryCanvas.Visibility = Visibility.Visible;
        if (_selected is not null) {
            var item = Items.FirstOrDefault(x => x.Key == _selected.Key);
            if (item is not null) { Gallery.SelectedItem = item; Gallery.ScrollIntoView(item); }
        }
        _warmStart = -1; _warmGeneration = -1;
        ScheduleThumbnailWarm(); _ = PrimeBufferAsync();
    }
    private async void StartSlideshow(object sender, RoutedEventArgs e) => await OpenCanvasAsync(true);
    private void ToggleDetails(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) FillDetails(_selected, null, "Metadata reflects the latest sync.");
        DetailsSplit.IsPaneOpen = !DetailsSplit.IsPaneOpen;
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
        if (_controlsReady && !_settingSize) ApplyThumbnailSize(e.NewValue, true);
    }
    private void ApplyThumbnailSize(double size, bool save)
    {
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
                _warmGeneration = -1; ScheduleThumbnailWarm();
                if (save) UserPreferences.Set("thumbnailSize", _thumbnailSize);
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
        UpdateSourceIcons();
    }
}
