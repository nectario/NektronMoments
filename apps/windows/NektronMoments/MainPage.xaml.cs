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
    public CatalogCollection Items { get; } = new();
    private readonly LibraryBridge _bridge = new();
    private readonly WeightedCache<CompactCatalog> _catalogSnapshots = new(PerformanceProfile.PhysicalBytes >= 96UL * 1024 * 1024 * 1024 ? 512L * 1024 * 1024 : 128L * 1024 * 1024);
    private int _catalogCacheEpoch;
    private readonly Stopwatch _startup = Stopwatch.StartNew();
    private long _libraryReadyMs;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _catalogRequest;
    private Task? _reloadTask;
    private bool _reloadIsRefresh;
    private Func<object, CancellationToken, Task<CompactCatalog>>? _catalogLoaderForVerification;
    private Func<bool, Task<LibraryOverview>>? _overviewLoaderForVerification;
    private LibraryOverview? _overview;
    private MediaItem? _selected;
    private int _generation, _selectionGeneration, _catalogLoads;
    private bool _ready, _ascending, _dialog, _importing;
    private ContentDialog? _commonDialog;
    private bool _loadingCatalog => _catalogLoads > 0;
    private string _sourceId = "", _mediaType = "";
    private readonly List<NavigationViewItem> _sourceItems = [];

    public MainPage()
    {
        InitializeComponent();
        _hideScreenshots = !LibraryAutomationDisabled && UserPreferences.Flag("hideScreenshots");
        HideScreenshotsCheck.IsChecked = _hideScreenshots;
        _startupMode = StartupProcessing.Resolve(UserPreferences.Text("startupProcessingMode"), UserPreferences.Flag("processOnStartup", true));
        UpdateStartupProcessingLabel();
        MediaThumbnail.InitializeDispatcher(DispatcherQueue);
        Items.CollectionChanged += CatalogItemsChanged;
        _controlsReady = true;
        Viewer.ItemAt = ItemAtAsync;
        Viewer.BackRequested += ReturnToGallery;
        Viewer.ItemChanged += item => { CancelSelectionDetails(); _selected = item; };
        ActualThemeChanged += (_, _) => UpdateThemeChrome();
        ApplyThumbnailSize(UserPreferences.Number("thumbnailSize", 240), false);
        if (App.MainWindowInstance != null)
            App.MainWindowInstance.Closed += (_, _) => { _lifetime.Cancel(); _search?.Cancel(); _bridge.Dispose(); };
    }
    private async void PageLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        _ready = true;
        var browsingCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_BROWSING_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(browsingCheck)) {
            await RunIsolatedVerificationAsync(() => VerifyBrowsingAsync(browsingCheck));
            return;
        }
        var motionCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_MOTION_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(motionCheck)) {
            await VerifyMotionCancellationAsync(motionCheck);
            return;
        }
        var selectionCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_SELECTION_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(selectionCheck)) {
            await RunIsolatedVerificationAsync(() => VerifyPhotoSelectionAsync(selectionCheck));
            return;
        }
        var scrollCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_SCROLL_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(scrollCheck)) {
            await RunIsolatedVerificationAsync(() => VerifyPixelScrollingAsync(scrollCheck));
            return;
        }
        // Installer regression mode exercises the real release shell without
        // reading a photo library, connecting to WSL, or starting any jobs.
        var assetCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_ASSET_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(assetCheck)) {
            await RunIsolatedVerificationAsync(() => VerifyReleaseAssetsAsync(assetCheck));
            return;
        }
        UpdateThemeChrome();
        PerformanceLabel.Text = $"{PerformanceProfile.Current.Name} · {PerformanceProfile.PhysicalBytes / (1024d * 1024 * 1024):N0} GB";
        await ReloadAsync(false);
        _libraryReadyMs = _startup.ElapsedMilliseconds;
        DiagnosticTrace.Marker("LibraryReady", Items.Count);
        _ = ExpandViewportCacheAsync();
        var reorderCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_REORDER_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(reorderCheck))
            await RunIsolatedVerificationAsync(() => VerifyRealPhotoReorderAsync(reorderCheck));
        var startupScrollCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_STARTUP_SCROLL_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(startupScrollCheck))
            await RunIsolatedVerificationAsync(() => VerifyStartupScrollingAsync(startupScrollCheck));
        var readinessCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_READINESS_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(readinessCheck))
            await RunIsolatedVerificationAsync(() => VerifyLiveThumbnailReadinessAsync(readinessCheck));
        var resizeCheck = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_RESIZE_CHECK_DIR");
        if (!string.IsNullOrWhiteSpace(resizeCheck)) await RunIsolatedVerificationAsync(() => VerifyPhotoResizeAsync(resizeCheck));
#if DEBUG
        if (File.Exists(Path.Combine(_bridge.Workspace, "build", "verify-windows-ui.flag")))
            await VerifyUiAsync();
#endif
        StartLibraryAutomation();
    }
    private async Task RunIsolatedVerificationAsync(Func<Task> verification)
    {
        var hitTestBefore = IsHitTestVisible;
        IsHitTestVisible = false;
        try { await verification(); }
        finally { if (!_lifetime.IsCancellationRequested) IsHitTestVisible = hitTestBefore; }
    }
    public void Shutdown() {
        CancelDisplayWarm();
        _reorderMotion?.Dispose();
        _lifetime.Cancel(); _sizeChange?.Cancel(); _catalogRequest?.Cancel();
        if (_isThumbnailSizing) CommitThumbnailSizing();
        _sizingRetirement?.Close();
        _processingTimer?.Stop(); _processingWindow?.ClosePermanently(); _commonDialog?.Hide(); _dragScrollTimer?.Stop();
        _previewCounterTimer?.Stop();
        MediaThumbnail.ShutdownPresentation();
        Items.CollectionChanged -= CatalogItemsChanged;
        CancelScrollbarGesture();
        CancelSelectionDetails();
        _animatedThumb?.Dispose(); _nativeWheel?.Dispose(); _pixelScroll?.Dispose(); _lifetime.Cancel(); _sizingFinished?.TrySetCanceled();
        _search?.Cancel(); _thumbnailPrefetch?.Cancel(); _jobCancellation?.Cancel(); Viewer.Close(); _bridge.Dispose();
    }
    private async Task ReloadAsync(bool refresh, bool invalidateThumbnails = true)
    {
        if (_reloadTask is { IsCompleted: false } active) {
            var activeRefresh = _reloadIsRefresh;
            await active;
            if (refresh && !activeRefresh && !_lifetime.IsCancellationRequested) await ReloadAsync(true, invalidateThumbnails);
            return;
        }
        _reloadIsRefresh = refresh;
        var task = ReloadCoreAsync(refresh, invalidateThumbnails); _reloadTask = task;
        try { await task; }
        finally { if (ReferenceEquals(_reloadTask, task)) _reloadTask = null; }
    }
    private async Task ReloadCoreAsync(bool refresh, bool invalidateThumbnails)
    {
        if (refresh) { ++_catalogCacheEpoch; _catalogSnapshots.Clear(); _catalogRequest?.Cancel(); }
        SuspendGalleryMotion();
        if (Viewer.IsOpen) ReturnToGallery();
        SetBusy(true);
        Notice.IsOpen = false;
        try
        {
            _overview = _overviewLoaderForVerification is { } overviewLoader ? await overviewLoader(refresh) :
                await _bridge.CallAsync<LibraryOverview>(new { command = refresh ? "refresh" : "hello" }, _lifetime.Token);
            if (refresh && invalidateThumbnails) { ThumbnailService.Shared.ClearMemory(); MediaThumbnail.Decoded.Clear(); }
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
            await LoadCatalogAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    private async Task LoadCatalogAsync()
    {
        if (_arrangementFinished is { } pendingArrangement)
            try { await pendingArrangement.Task.WaitAsync(_lifetime.Token); } catch (OperationCanceledException) { return; }
        SuspendGalleryMotion();
        if (_overview is null) return;
        var generation = ++_generation;
        var query = SearchBox.Text; var source = _sourceId; var mediaType = _mediaType; var ascending = _ascending; var hideScreenshots = _hideScreenshots;
        var batch = BrowsingBatch;
        var scope = ArrangementScope;
        var choice = _sortChoice;
        _catalogRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _catalogRequest = request; var token = request.Token; var entered = false;
        try
        {
            await _catalogGate.WaitAsync(token); entered = true; ++_catalogLoads;
            if (generation != _generation) return;
            CancelSelectionDetails(); _selected = null;
            _highestVisible = 0; _thumbnailPrefetch?.Cancel();
            SetBusy(true); StatusText.Text = "Preparing your complete library…";
            var arrangement = await _orderStore.LoadAsync(scope, token);
            var sort = choice ?? arrangement.Sort;
            ascending = sort == "oldest" || sort == "custom" && arrangement.BaseSort == "oldest";
            var catalogRequest = new {
                command = "catalog-stream", query, sourceId = source, mediaType, ascending, hideScreenshots,
            };
            var cacheEpoch = _catalogCacheEpoch;
            var cacheKey = JsonSerializer.Serialize(new { library = _overview.LibraryId, source, mediaType, ascending, hideScreenshots });
            CompactCatalog catalog;
            if (query.Length != 0 || !_catalogSnapshots.TryGet(cacheKey, out catalog!)) {
                catalog = _catalogLoaderForVerification is { } loader ? await loader(catalogRequest, token) :
                    await _bridge.LoadCatalogAsync(catalogRequest, token);
                token.ThrowIfCancellationRequested();
                if (query.Length == 0 && cacheEpoch == _catalogCacheEpoch)
                    _catalogSnapshots.Put(cacheKey, catalog, catalog.StoredBytes + catalog.Count * 12L + 16L * 1024 * 1024);
            }
            if (generation != _generation) return;
            var view = await Task.Run(() => CatalogView.RestoreAsync(catalog,
                sort == "custom" && query.Length == 0 ? arrangement.Keys : [], batch, token,
                sort == "custom" && query.Length == 0 ? arrangement.Paths : null), token);
            if (generation != _generation) return;
            if (_isThumbnailSizing && _sizingFinished is { } sizing) await sizing.Task.WaitAsync(token);
            if (generation != _generation) return;
            SuspendGalleryMotion();
            Items.ReplaceView(view);
            _ascending = ascending; _activeSort = sort; UpdateArrangementChrome();
            _pixelScroll?.JumpTo(0);
            UpdateResults(catalog.Count);
            ScheduleThumbnailWarm();
            if (!_importing) StatusText.Text = _overview.Pending > 0
                ? $"Local originals · {_overview.Pending:N0} files awaiting content hashing"
                : $"Local originals · {Items.Count:N0} moments available";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _generation && !request.IsCancellationRequested) ShowError(ex); }
        finally {
            if (entered) { --_catalogLoads; _catalogGate.Release(); }
            if (ReferenceEquals(_catalogRequest, request)) _catalogRequest = null;
            if (generation == _generation) { SetBusy(false); UpdateArrangementChrome(); }
        }
    }
    private void UpdateResults(int total)
    {
        _catalogReportedTotal = total;
        UpdateBrowseCounter();
        EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = "No photos here yet";
        EmptyMessage.Text = SearchBox.Text.Length > 0
            ? "Try another filename, or press Enter to search indexed descriptions and addresses."
            : "Add a folder, or choose another source. Originals stay on your computer.";
    }
    // Processing owns the detailed window; don't duplicate it with a permanent
    // indeterminate stripe over the photo canvas (including refreshes mid-run).
    private void SetBusy(bool value) => Busy.Visibility = value && !_importing ? Visibility.Visible : Visibility.Collapsed;
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
    private void EscapeKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (_dialog) return; if (_settingsOpen) CloseSettings(); else if (Viewer.IsOpen) ReturnToGallery(); else DetailsSplit.IsPaneOpen = false; args.Handled = true; }
    private void CloseDetails(object sender, RoutedEventArgs e) => DetailsSplit.IsPaneOpen = false;
    private void GalleryContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is GridViewItem container) _reorderMotion?.ContainerChanging(container, args.Item, args.InRecycleQueue);
        if (args.InRecycleQueue || Viewer.IsOpen || !_ready) return;
        QueueGalleryWork();
    }
    private async void ToggleSort(object sender, RoutedEventArgs e)
    {
        await SetSortAsync(_ascending ? "newest" : "oldest");
    }
    private async void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_ready || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ++_generation; _catalogRequest?.Cancel();
        _search?.Cancel();
        var search = new CancellationTokenSource();
        _search = search;
        try {
            await Task.Delay(300, search.Token);
            await LoadCatalogAsync();
            if (!search.IsCancellationRequested && sender.Text.Length > 0) StatusText.Text = "Local matches · Press Enter to search indexed descriptions and addresses";
        } catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_search, search)) _search = null; search.Dispose(); }
    }
    private async void SearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        SuspendGalleryMotion();
        _search?.Cancel();
        if (string.IsNullOrWhiteSpace(sender.Text)) { await LoadCatalogAsync(); return; }
        var generation = ++_generation;
        _catalogRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _catalogRequest = request;
        CancelSelectionDetails(); _selected = null;
        _thumbnailPrefetch?.Cancel();
        SetBusy(true);
        try
        {
            var result = await _bridge.CallAsync<MediaPage>(new { command = "search", query = sender.Text, sourceId = _sourceId, mediaType = _mediaType, hideScreenshots = _hideScreenshots }, request.Token);
            if (generation != _generation) return;
            Items.ReplaceAll(result.Items);
            UpdateArrangementChrome();
            _pixelScroll?.JumpTo(0);
            UpdateResults(result.Total);
            StatusText.Text = result.Bounded
                ? "First 200 indexed matches searched · Narrow your search for more precise results"
                : "Indexed search · Matches available on this workstation";
            if (!string.IsNullOrWhiteSpace(result.Notice)) {
                Notice.Title = "Showing local matches"; Notice.Message = result.Notice; Notice.IsOpen = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _generation && !request.IsCancellationRequested) ShowError(ex); }
        finally { if (ReferenceEquals(_catalogRequest, request)) _catalogRequest = null; if (generation == _generation) SetBusy(false); }
    }
    private async void NavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!args.IsSettingsInvoked) return;
        try { await ShowSettingsAsync(); }
        catch (Exception error) { ShowError(error); }
    }
    private async void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_restoringSettingsSelection) return;
        if (!_ready) return;
        if (args.IsSettingsSelected) return;
        if (args.SelectedItem is not NavigationViewItem item) return;
        CloseSettings(restoreSelection: false);
        _lastLibraryNavigation = item;
        var tag = item.Tag?.ToString() ?? "all";
        _sortChoice = null;
        ReturnToGallery();
        _mediaType = tag is "Photo" or "Video" ? tag : "";
        _sourceId = tag is "all" or "Photo" or "Video" ? "" : tag;
        LibraryHeading.Text = tag == "all" ? "Your moments, found." : item.Content.ToString();
        DetailsSplit.IsPaneOpen = false;
        await LoadCatalogAsync();
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
            using var launched = await Task.Run(() => Process.Start(info));
        } catch (Exception ex) { ShowError(ex); }
    }
    private async void AddFolder(object sender, RoutedEventArgs e) => await AddFolderAsync();
    private async void ShowActivity(object sender, RoutedEventArgs e)
    {
        if (_processingWindow?.IsPreparing == true) { _processingWindow.Activate(); return; }
        if (_metadataProgress is not null) { await ShowProcessingProgressAsync(); return; }
        await ShowSavedActivityAsync();
    }
    private async Task ShowSavedActivityAsync()
    {
        if (_dialog) return;
        try
        {
            var activity = await _bridge.CallAsync<JsonElement>(new { command = "activity" }, _lifetime.Token);
            var lines = new List<string> { "Saved CLI processing activity", "" };
            foreach (var queue in activity.GetProperty("queues").EnumerateObject()) {
                lines.Add(queue.Name switch { "ManifestOutbox" => "Metadata batches", "BulkManifestOutbox" => "Bulk imports", "ByokAnalysis" => "Direct AI (BYOK)", _ => "Legacy scene previews" });
                foreach (var count in queue.Value.EnumerateObject()) lines.Add($"  {count.Name}: {count.Value}");
                if (!queue.Value.EnumerateObject().Any()) lines.Add("  No saved work");
                lines.Add("");
            }
            if (activity.TryGetProperty("failedPhotos", out var failures) && failures.GetArrayLength() > 0) {
                lines.Add("Failed photos (latest 100)");
                lines.Add("Isolated from processing. Uncertain means the provider may have billed the call; no automatic paid retry.");
                foreach (var failure in failures.EnumerateArray())
                    lines.Add($"  {failure.GetProperty("file").GetString()} · {failure.GetProperty("reason").GetString()} · {failure.GetProperty("state").GetString()}");
                lines.Add("");
            }
            lines.Add("Full startup mode can request paid AI/address enrichment, including older pending photos. Metadata-only mode does not. Originals remain on their source drive.");
            await ShowDialogAsync("Saved queues", new ScrollViewer { MaxHeight = 540, Content = new TextBlock { Text = string.Join("\n", lines), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 680 } });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private Task ShowSettingsAsync() => ShowStartupProcessingSettingsAsync();
    private async Task ShowAboutAsync()
    {
        await ShowDialogAsync("Nektron Moments", new TextBlock {
            Text = $"Nektron Moments {typeof(App).Assembly.GetName().Version?.ToString(3)} · Brand v1.3\n\nProcess metadata reads available dates, GPS, dimensions and hashes, then updates the library. Library > On startup lets you choose Full (metadata + paid AI/address enrichment, including pending photos), File metadata only, or Off.\n\nDouble-click: open in canvas\nCtrl+F: search · F5: refresh\nEsc: return to library\nLeft / Right: previous / next\nSpace: pause / resume slideshow\nF11: full screen\n\nThe mouse wheel browses in gentle pixel increments. Thumbnail size and enlargement preferences are remembered. The slideshow interval is in the viewer's overflow menu.\n\nThis preview reuses your existing Ubuntu CLI sign-in without copying passwords to Windows.",
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
            _commonDialog = dialog;
            await dialog.ShowAsync();
        } finally { _commonDialog = null; _dialog = false; }
    }
    private async void ChangeTheme(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance?.Content is FrameworkElement root) {
            root.RequestedTheme = ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            UpdateThemeChrome();
            try { await UserPreferences.SetAsync("theme", root.RequestedTheme.ToString()); }
            catch (Exception) { StatusText.Text = "Theme changed for this session; the preference could not be saved."; }
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
    private void GalleryResized(object sender, SizeChangedEventArgs e) { CancelScrollbarGesture(); _pixelScroll?.Stop(); ResizeGallery(); }
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
                var cacheLength = _thumbnailLayoutCommit || _isThumbnailSizing ? .5 : _expandedViewportCache
                    ? GalleryWorkPolicy.CacheLength(GetGalleryColumns(wrap.ItemWidth), wrap.ItemHeight,
                        _pixelScroll?.Scroll.ViewportHeight ?? Gallery.ActualHeight, PerformanceProfile.Current.ViewportCache) : .5;
                if (Math.Abs(wrap.CacheLength - cacheLength) > .001) wrap.CacheLength = cacheLength;
            }
        }
    }

}
