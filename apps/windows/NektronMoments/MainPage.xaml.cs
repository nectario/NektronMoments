using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NektronMoments.Models;
using NektronMoments.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using NektronMoments.Controls;

namespace NektronMoments;

public sealed partial class MainPage : Page
{
    public ObservableCollection<MediaItem> Items { get; } = [];
    private readonly LibraryBridge _bridge = new();
    private readonly Stopwatch _startup = Stopwatch.StartNew();
    private long _libraryReadyMs;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _search;
    private LibraryOverview? _overview;
    private MediaItem? _selected;
    private int _generation, _selectionGeneration, _offset;
    private bool _ready, _hasMore, _ascending, _dialog, _importing;
    private string _sourceId = "", _mediaType = "";
    private readonly List<NavigationViewItem> _sourceItems = [];

    public MainPage()
    {
        InitializeComponent();
        _controlsReady = true;
        Viewer.ItemAt = ItemAtAsync;
        Viewer.BackRequested += ReturnToGallery;
        Viewer.ItemChanged += item => { ++_selectionGeneration; _selected = item; };
        ActualThemeChanged += (_, _) => UpdateThemeChrome();
        ApplyThumbnailSize(UserPreferences.Number("thumbnailSize", 240), false);
        if (App.MainWindowInstance != null)
            App.MainWindowInstance.Closed += (_, _) => { _lifetime.Cancel(); _search?.Cancel(); _bridge.Dispose(); };
    }
    private async void PageLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        _ready = true;
        var scrollCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_SCROLL_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(scrollCheck)) {
            await VerifyPixelScrollingAsync(scrollCheck);
            return;
        }
        // Installer regression mode exercises the real release shell without
        // reading a photo library, connecting to WSL, or starting any jobs.
        var assetCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_ASSET_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(assetCheck)) {
            await VerifyReleaseAssetsAsync(assetCheck);
            return;
        }
        UpdateThemeChrome();
        PerformanceLabel.Text = $"{PerformanceProfile.Current.Name} · {PerformanceProfile.PhysicalBytes / (1024d * 1024 * 1024):N0} GB";
        await ReloadAsync(false);
        _libraryReadyMs = _startup.ElapsedMilliseconds;
        _ = ExpandViewportCacheAsync();
        var resizeCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_RESIZE_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(resizeCheck)) await VerifyPhotoResizeAsync(resizeCheck);
#if DEBUG
        if (File.Exists(Path.Combine(_bridge.Workspace, "build", "verify-windows-ui.flag")))
            await VerifyUiAsync();
#endif
    }
    public void Shutdown() {
        if (_pixelScroll is { } scroller) {
            scroller.Scroll.UnregisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, _scrollExtentToken);
            scroller.Scroll.UnregisterPropertyChangedCallback(ScrollViewer.ViewportHeightProperty, _scrollViewportToken);
        }
        _nativeWheel?.Dispose(); _pixelScroll?.Dispose(); _lifetime.Cancel(); _sizingFinished?.TrySetCanceled();
        _search?.Cancel(); _thumbnailPrefetch?.Cancel(); _jobCancellation?.Cancel(); Viewer.Close(); _bridge.Dispose();
    }
    private async Task ReloadAsync(bool refresh)
    {
        if (Viewer.IsOpen) ReturnToGallery();
        SetBusy(true);
        Notice.IsOpen = false;
        try
        {
            _overview = await _bridge.CallAsync<LibraryOverview>(new { command = refresh ? "refresh" : "hello" }, _lifetime.Token);
            if (refresh) { ThumbnailService.Shared.ClearMemory(); MediaThumbnail.Decoded.Clear(); }
            foreach (var old in _sourceItems) Navigation.MenuItems.Remove(old);
            _sourceItems.Clear();
            foreach (var source in _overview.Sources)
            {
                var nav = new NavigationViewItem { Content = source.Name, Tag = source.Id };
                ToolTipService.SetToolTip(nav, source.Path);
                Navigation.MenuItems.Add(nav);
                _sourceItems.Add(nav);
            }
            UpdateSourceIcons();
            LibrarySubtitle.Text = $"{_overview.Photos:N0} photos · {_overview.Videos:N0} videos · {_overview.Sources.Count:N0} local " + (_overview.Sources.Count == 1 ? "source" : "sources");
            await LoadPageAsync(reset: true);
            _ = PrimeBufferAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    private async Task LoadPageAsync(bool reset)
    {
        if (_overview is null) return;
        var generation = reset ? ++_generation : _generation;
        try { await _pagingGate.WaitAsync(_lifetime.Token); } catch (OperationCanceledException) { return; }
        if (generation != _generation) { _pagingGate.Release(); return; }
        if (!reset && !_hasMore) { _pagingGate.Release(); return; }
        if (reset) {
            _pixelScroll?.Stop();
            _offset = 0; _highestVisible = 0; _thumbnailPrefetch?.Cancel();
            _activeQuery = SearchBox.Text; _activeSource = _sourceId;
            _activeMediaType = _mediaType; _activeAscending = _ascending;
        }
        if (reset) SetBusy(true);
        try
        {
            var page = await _bridge.CallAsync<MediaPage>(new {
                command = "page", query = _activeQuery, sourceId = _activeSource,
                mediaType = _activeMediaType, offset = _offset, limit = reset ? 120 : PerformanceProfile.Current.PageSize, ascending = _activeAscending,
            }, _lifetime.Token);
            if (generation != _generation) return;
            if (_isThumbnailSizing && _sizingFinished is { } sizing) await sizing.Task.WaitAsync(_lifetime.Token);
            if (generation != _generation) return;
            if (reset) Items.Clear();
            var added = 0;
            foreach (var item in page.Items) {
                if (_isThumbnailSizing && _sizingFinished is { } activeSizing) await activeSizing.Task.WaitAsync(_lifetime.Token);
                if (generation != _generation) return;
                Items.Add(item);
                if (++added % 64 == 0) await Task.Delay(1);
            }
            _offset = page.NextOffset; _hasMore = page.HasMore;
            UpdateResults(page.Total);
            ScheduleThumbnailWarm();
            if (!_importing) StatusText.Text = _overview.Pending > 0
                ? $"Local originals · {_overview.Pending:N0} files awaiting content hashing · No paid enrichment started"
                : $"Local originals · {Items.Count:N0} items buffered";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _pagingGate.Release(); if (reset) SetBusy(false); }
    }
    private void UpdateResults(int total)
    {
        ResultCount.Text = $"{total:N0} items" + (Items.Count < total ? $" · {Items.Count:N0} loaded" : "");
        EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = "No photos here yet";
        EmptyMessage.Text = SearchBox.Text.Length > 0
            ? "Try another filename, or press Enter to search indexed descriptions and addresses."
            : "Add a folder, or choose another source. Originals stay on your computer.";
        LoadMoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetBusy(bool value) => Busy.Visibility = value || _importing ? Visibility.Visible : Visibility.Collapsed;
    private void ShowError(Exception error)
    {
        if (_lifetime.IsCancellationRequested) return;
        var message = error is InvalidOperationException ? error.Message : "The library connection is unavailable. Check Ubuntu and your CLI sign-in, then refresh.";
        Notice.Title = "Library needs attention";
        Notice.Message = message;
        Notice.IsOpen = true;
        StatusText.Text = message;
        if (Items.Count == 0) { EmptyTitle.Text = "Your library is within reach"; EmptyMessage.Text = message; }
    }
    private async void RefreshLibrary(object sender, RoutedEventArgs e) => await ReloadAsync(true);
    private async void RefreshKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await ReloadAsync(true); }
    private void FocusSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { SearchBox.Focus(FocusState.Keyboard); args.Handled = true; }
    private void EscapeKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (Viewer.IsOpen) ReturnToGallery(); else DetailsSplit.IsPaneOpen = false; args.Handled = true; }
    private void CloseDetails(object sender, RoutedEventArgs e) => DetailsSplit.IsPaneOpen = false;
    private async void LoadMore(object sender, RoutedEventArgs e) => await LoadPageAsync(false);
    private async void GalleryContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || Viewer.IsOpen || !_ready) return;
        if (Gallery.ItemsPanelRoot is ItemsWrapGrid panel) _highestVisible = Math.Max(0, panel.FirstVisibleIndex);
        ScheduleThumbnailWarm();
        if (_hasMore && Items.Count - _highestVisible < PerformanceProfile.Current.LowWater) await PrimeBufferAsync();
    }
    private async void ToggleSort(object sender, RoutedEventArgs e)
    {
        _ascending = !_ascending;
        SortButton.Content = _ascending ? "Oldest first ↑" : "Newest first ↓";
        await LoadPageAsync(true);
        _ = PrimeBufferAsync();
    }
    private async void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_ready || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _search?.Cancel();
        var search = new CancellationTokenSource();
        _search = search;
        try {
            await Task.Delay(300, search.Token);
            await LoadPageAsync(true);
            _ = PrimeBufferAsync();
            if (sender.Text.Length > 0) StatusText.Text = "Local matches · Press Enter to search indexed descriptions and addresses";
        } catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_search, search)) _search = null; search.Dispose(); }
    }
    private async void SearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _search?.Cancel();
        if (string.IsNullOrWhiteSpace(sender.Text)) { await LoadPageAsync(true); return; }
        var generation = ++_generation;
        _hasMore = false; _thumbnailPrefetch?.Cancel();
        SetBusy(true);
        try
        {
            var result = await _bridge.CallAsync<MediaPage>(new { command = "search", query = sender.Text, sourceId = _sourceId, mediaType = _mediaType }, _lifetime.Token);
            if (generation != _generation) return;
            Items.Clear();
            foreach (var item in result.Items) Items.Add(item);
            _hasMore = false;
            UpdateResults(result.Total);
            StatusText.Text = result.Bounded
                ? "First 200 indexed matches searched · Narrow your search for more precise results"
                : "Indexed search · Matches available on this workstation";
            if (!string.IsNullOrWhiteSpace(result.Notice)) {
                Notice.Title = "Showing local matches"; Notice.Message = result.Notice; Notice.IsOpen = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    private async void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready) return;
        if (args.IsSettingsSelected) { await ShowSettingsAsync(); return; }
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag?.ToString() ?? "all";
        ReturnToGallery();
        _mediaType = tag is "Photo" or "Video" ? tag : "";
        _sourceId = tag is "all" or "Photo" or "Video" ? "" : tag;
        LibraryHeading.Text = tag == "all" ? "Your moments, found." : item.Content.ToString();
        DetailsSplit.IsPaneOpen = false;
        await LoadPageAsync(true);
        _ = PrimeBufferAsync();
    }
    private async void MediaClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not MediaItem item) return;
        var generation = ++_selectionGeneration;
        _selected = item;
        FillDetails(item, null, "Loading indexed details…");
        DetailsSplit.IsPaneOpen = true;
        try
        {
            var detail = await _bridge.CallAsync<MediaDetail>(new { command = "detail", key = item.Key }, _lifetime.Token);
            if (generation == _selectionGeneration) { _selected = detail.Item; FillDetails(detail.Item, detail.Remote, detail.Notice); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (generation == _selectionGeneration) DetailNotice.Text = "Showing cached metadata. Indexed details are unavailable."; }
    }
    private static string Text(JsonElement? element, string property)
        => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var field) && field.ValueKind != JsonValueKind.Null ? field.ToString() : "";
    private void FillDetails(MediaItem item, JsonElement? remote, string notice)
    {
        DetailHeading.Text = item.MediaType == "Video" ? "Video details" : "Photo details";
        DetailThumbnail.Item = item;
        DetailName.Text = item.Name;
        DetailDate.Text = item.Captured.Length == 0 ? "Pending metadata extraction" :
            (DateTime.TryParse(item.Captured, out var date) ? date.ToString("dddd, dd MMMM yyyy · HH:mm") : item.Captured)
            + (item.DateSource == "FileMtime" ? "\nFile modification time (capture date unknown)" : "");
        DetailDescription.Text = item.Description.Length > 0 ? item.Description : "Not described yet";
        var gps = item.Metadata.ValueKind == JsonValueKind.Object && item.Metadata.TryGetProperty("location", out var value) ? value : default;
        var coords = Text(gps, "latitude") is { Length: > 0 } latitude ? latitude + ", " + Text(gps, "longitude") : "";
        DetailLocation.Text = item.Address.Length > 0 ? item.Address + (coords.Length > 0 ? "\n" + coords : "") :
            coords.Length > 0 ? coords + "\nAddress not resolved yet" : "No location metadata";
        var width = Text(item.Metadata, "widthPixels");
        var height = Text(item.Metadata, "heightPixels");
        DetailFile.Text = item.MediaType + (item.ByteSize.HasValue ? $" · {item.ByteSize / 1048576d:N1} MB" : "") +
            (width.Length > 0 ? $"\n{width} × {height}" : "");
        DetailSource.Text = item.Source + "\n" + item.Path;
        DetailIdentity.Text = item.Hash.Length > 0
            ? $"Exact SHA-256 · {item.Occurrences:N0} occurrence(s)\n{item.Hash[..16]}…"
            : "Content hash pending · duplicates not yet checked";
        DetailNotice.Text = notice;
    }
    private async Task<StorageFile?> ResolveOriginalAsync()
    {
        if (_selected is null) return null;
        foreach (var path in _selected.Paths.Prepend(_selected.Path).Distinct())
            try { return await StorageFile.GetFileFromPathAsync(path); } catch (Exception) { }
        Notice.Title = "Original unavailable";
        Notice.Message = "The source drive may be disconnected, or the file has moved. No file was deleted by Moments.";
        Notice.IsOpen = true;
        return null;
    }
    private async void OpenOriginal(object sender, RoutedEventArgs e)
    {
        try { if (await ResolveOriginalAsync() is { } file) await Launcher.LaunchFileAsync(file); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void RevealOriginal(object sender, RoutedEventArgs e)
    {
        try {
            if (await ResolveOriginalAsync() is not { } file) return;
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add("/select,"); info.ArgumentList.Add(file.Path);
            Process.Start(info);
        } catch (Exception ex) { ShowError(ex); }
    }
    private async void ViewMedia(object sender, RoutedEventArgs e) => await OpenCanvasAsync(false);
    private async void GalleryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not GridViewItem) element = VisualTreeHelper.GetParent(element);
        if (element is GridViewItem container && container.Content is MediaItem item) _selected = item;
        e.Handled = true;
        await OpenCanvasAsync(false);
    }
    private async void AddFolder(object sender, RoutedEventArgs e) => await AddFolderAsync();
    private async void ShowActivity(object sender, RoutedEventArgs e)
    {
        if (_dialog) return;
        try
        {
            var activity = await _bridge.CallAsync<JsonElement>(new { command = "activity" }, _lifetime.Token);
            var lines = new List<string> { "Saved CLI processing activity", "" };
            foreach (var queue in activity.GetProperty("queues").EnumerateObject()) {
                lines.Add(queue.Name switch { "ManifestOutbox" => "Metadata batches", "BulkManifestOutbox" => "Bulk imports", _ => "Scene previews" });
                foreach (var count in queue.Value.EnumerateObject()) lines.Add($"  {count.Name}: {count.Value}");
                if (!queue.Value.EnumerateObject().Any()) lines.Add("  No saved work");
                lines.Add("");
            }
            lines.Add("This app does not start paid enrichment. Originals remain on their source drive.");
            await ShowDialogAsync("Activity", new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async Task ShowSettingsAsync()
    {
        await ShowDialogAsync("Nektron Moments", new TextBlock {
            Text = $"Nektron Moments {typeof(App).Assembly.GetName().Version?.ToString(3)} · Brand v1.3\n\nProcess metadata reads available dates, GPS, dimensions and hashes, then updates the library. Paid descriptions and address resolution remain separate.\n\nDouble-click: open in canvas\nCtrl+F: search · F5: refresh\nEsc: return to library\nLeft / Right: previous / next\nSpace: pause / resume slideshow\nF11: full screen\n\nThe mouse wheel browses in gentle pixel increments. Thumbnail size and enlargement preferences are remembered. The slideshow interval is in the viewer's overflow menu.\n\nThis preview reuses your existing Ubuntu CLI sign-in without copying passwords to Windows.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
        });
    }
    private async Task ShowDialogAsync(string title, FrameworkElement content)
    {
        if (_dialog) return;
        _dialog = true;
        try {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = content, CloseButtonText = "Close",
                RequestedTheme = ActualTheme, DefaultButton = ContentDialogButton.Close };
            dialog.Resources["ContentDialogMaxWidth"] = 1120d;
            await dialog.ShowAsync();
        } finally { _dialog = false; }
    }
    private void ChangeTheme(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance?.Content is FrameworkElement root) {
            root.RequestedTheme = ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            try { UserPreferences.Theme = root.RequestedTheme.ToString(); }
            catch (Exception) { StatusText.Text = "Theme changed for this session; the preference could not be saved."; }
            UpdateThemeChrome();
        }
    }
    private void UpdateSourceIcons()
    {
        var theme = ActualTheme == ElementTheme.Dark ? "dark" : "light";
        foreach (var nav in _sourceItems)
            nav.Icon = new ImageIcon { Source = new SvgImageSource(new Uri($"ms-appx:///Assets/NektronMoments/icons/ui/svg/{theme}/sources.svg")) };
    }
    private void PageResized(object sender, SizeChangedEventArgs e)
    {
        SearchBox.Width = Math.Clamp(ActualWidth - 630, 140, 560);
        ThumbnailSlider.Width = ActualWidth < 900 ? 84 : 148;
        RibbonBar.MaxWidth = Math.Max(180, ActualWidth - ThumbnailSizePanel.ActualWidth - 32);
        DetailsSplit.DisplayMode = ActualWidth < 1180 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        DetailsSplit.OpenPaneLength = Math.Min(320, Math.Max(260, ActualWidth - 70));
        ResizeGallery();
    }
    private void GalleryResized(object sender, SizeChangedEventArgs e) { _pixelScroll?.Stop(); ResizeGallery(); }
    private void ResizeGallery()
    {
        if (Gallery.ItemsPanelRoot is ItemsWrapGrid wrap) {
            if (_isThumbnailSizing) return;
            var width = Gallery.ActualWidth - Gallery.Padding.Left - Gallery.Padding.Right;
            if (width > 0) {
                // Continuous tile dimensions let the grid rearrange as the slider moves.
                var itemWidth = Math.Clamp(_thumbnailSize, 100, Math.Max(100, width - 16));
                if (Math.Abs(wrap.ItemWidth - itemWidth) > .01) {
                    ++_thumbnailLayoutChanges;
                    wrap.ItemWidth = itemWidth;
                    wrap.ItemHeight = itemWidth * .72 + 54;
                }
                wrap.CacheLength = _thumbnailLayoutCommit || _isThumbnailSizing ? .5 : _expandedViewportCache ? PerformanceProfile.Current.ViewportCache : 1;
            }
        }
    }

}
