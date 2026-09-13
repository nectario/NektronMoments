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
    private bool _ready, _loading, _hasMore, _ascending, _dialog, _importing;
    private string _sourceId = "", _mediaType = "";
    private readonly List<NavigationViewItem> _sourceItems = [];

    public MainPage()
    {
        InitializeComponent();
        if (App.MainWindowInstance != null)
            App.MainWindowInstance.Closed += (_, _) => { _lifetime.Cancel(); _search?.Cancel(); _bridge.Dispose(); };
    }
    private async void PageLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        _ready = true;
        await ReloadAsync(false);
        _libraryReadyMs = _startup.ElapsedMilliseconds;
#if DEBUG
        if (File.Exists(Path.Combine(_bridge.Workspace, "build", "verify-windows-ui.flag")))
            await VerifyUiAsync();
#endif
    }
    public void Shutdown() { _lifetime.Cancel(); _search?.Cancel(); _bridge.Dispose(); }
    private async Task ReloadAsync(bool refresh)
    {
        SetBusy(true);
        Notice.IsOpen = false;
        try
        {
            _overview = await _bridge.CallAsync<LibraryOverview>(new { command = refresh ? "refresh" : "hello" }, _lifetime.Token);
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    private async Task LoadPageAsync(bool reset)
    {
        if (_overview is null || (_loading && !reset)) return;
        var generation = reset ? ++_generation : _generation;
        if (reset) { _offset = 0; Items.Clear(); }
        _loading = true;
        SetBusy(true);
        try
        {
            var page = await _bridge.CallAsync<MediaPage>(new {
                command = "page", query = SearchBox.Text, sourceId = _sourceId,
                mediaType = _mediaType, offset = _offset, limit = 120, ascending = _ascending,
            }, _lifetime.Token);
            if (generation != _generation) return;
            foreach (var item in page.Items) Items.Add(item);
            _offset = page.NextOffset; _hasMore = page.HasMore;
            UpdateResults(page.Total);
            StatusText.Text = _overview.Pending > 0
                ? $"Local originals · {_overview.Pending:N0} files awaiting content hashing · No paid enrichment started"
                : "Local originals · Thumbnails load as you browse";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { if (generation == _generation) { _loading = false; SetBusy(false); } }
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
    private void SetBusy(bool value) => Busy.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
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
    private void EscapeKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { DetailsSplit.IsPaneOpen = false; args.Handled = true; }
    private void CloseDetails(object sender, RoutedEventArgs e) => DetailsSplit.IsPaneOpen = false;
    private async void LoadMore(object sender, RoutedEventArgs e) => await LoadPageAsync(false);
    private async void GalleryContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.ItemIndex >= Items.Count - 18 && _hasMore && !_loading)
            await LoadPageAsync(false);
    }
    private async void ToggleSort(object sender, RoutedEventArgs e)
    {
        _ascending = !_ascending;
        SortButton.Content = _ascending ? "Oldest first ↑" : "Newest first ↓";
        await LoadPageAsync(true);
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
            if (sender.Text.Length > 0) StatusText.Text = "Local matches · Press Enter to search indexed descriptions and addresses";
        } catch (OperationCanceledException) { }
    }
    private async void SearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _search?.Cancel();
        if (string.IsNullOrWhiteSpace(sender.Text)) { await LoadPageAsync(true); return; }
        var generation = ++_generation;
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
        _mediaType = tag is "Photo" or "Video" ? tag : "";
        _sourceId = tag is "all" or "Photo" or "Video" ? "" : tag;
        LibraryHeading.Text = tag == "all" ? "Your moments, found." : item.Content.ToString();
        DetailsSplit.IsPaneOpen = false;
        await LoadPageAsync(true);
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
    private async void ViewMedia(object sender, RoutedEventArgs e)
    {
        if (_dialog) return;
        MediaPlayerElement? player = null;
        try
        {
            var item = _selected;
            if (item is null || await ResolveOriginalAsync() is not { } file) return;
            FrameworkElement content;
            if (item.MediaType == "Video") {
                player = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = false,
                    Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file), Height = 540, Width = 920 };
                content = player;
            } else {
                using var stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage { DecodePixelWidth = 1600 };
                await bitmap.SetSourceAsync(stream);
                content = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = Math.Max(260, ActualHeight - 160), Width = Math.Min(1000, ActualWidth - 100) };
            }
            await ShowDialogAsync(item.Name, content);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { player?.MediaPlayer?.Pause(); player?.MediaPlayer?.Dispose(); }
    }
    private void GalleryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ViewMedia(sender, new RoutedEventArgs());
    private async void AddFolder(object sender, RoutedEventArgs e)
    {
        if (_importing) return;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var path = LibraryBridge.ToWslPath(folder.Path);
            _importing = true;
            AddFolderButton.IsEnabled = false;
            StatusText.Text = "Registering " + folder.Name + "…";
            var result = await _bridge.RunCliAsync(["source", "add", path, "--json"], _ => { }, _lifetime.Token);
            using var registered = JsonDocument.Parse(result);
            var sourceId = registered.RootElement.GetProperty("sourceId").GetString()!;
            var timer = Stopwatch.StartNew();
            await _bridge.RunCliAsync(["sync", sourceId, "--fast-add", "--no-input"], line => {
                if (timer.ElapsedMilliseconds < 200) return;
                timer.Restart();
                DispatcherQueue.TryEnqueue(() => StatusText.Text = line);
            }, _lifetime.Token);
            await ReloadAsync(true);
            StatusText.Text = "Folder added · Hashing and detailed extraction can continue with the CLI's normal sync";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _importing = false; AddFolderButton.IsEnabled = true; }
    }
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
            Text = "Windows preview · Brand v1.3\n\nThis build uses your existing Ubuntu CLI sign-in and account-scoped library cache through a local adapter. No passwords are copied to Windows.\n\nUse the Theme button for light/dark mode. Original media is never uploaded by browsing.\n\nCtrl+F  Search\nF5  Refresh library\nEsc  Close details\n\nStandalone sign-in, background watching and mobile clients follow in later milestones.",
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
            UpdateSourceIcons();
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
        SearchBox.Width = Math.Clamp(ActualWidth - 620, 180, 560);
        DetailsSplit.DisplayMode = ActualWidth < 1180 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        DetailsSplit.OpenPaneLength = Math.Min(320, Math.Max(260, ActualWidth - 70));
        ResizeGallery();
    }
    private void GalleryResized(object sender, SizeChangedEventArgs e) => ResizeGallery();
    private void ResizeGallery()
    {
        if (Gallery.ItemsPanelRoot is ItemsWrapGrid wrap) {
            var width = Gallery.ActualWidth;
            if (width > 0) {
                var columns = Math.Max(1, (int)(width / 238));
                wrap.ItemWidth = Math.Max(168, (width - 16) / columns);
                wrap.ItemHeight = Math.Min(260, wrap.ItemWidth * .72 + 54);
            }
        }
    }
#if DEBUG
    private async Task VerifyUiAsync()
    {
        // Explicit opt-in, local-only diagnostic for development. No cloud queries
        // or simulated uploads; captures the actual rendered gallery and its cache.
        try
        {
            var output = Path.Combine(_bridge.Workspace, "build", "windows-verification");
            Directory.CreateDirectory(output);
            await Task.Delay(4000);
            await CaptureAsync("library-light.png");
            var sample = Items.FirstOrDefault(i => i.MediaType == "Photo" && !i.Name.Contains("regression"));
            if (sample != null) {
                _selected = sample;
                FillDetails(sample, null, "Local cached metadata");
                DetailsSplit.IsPaneOpen = true;
            }
            await Task.Delay(2000);
            await CaptureAsync("details-light.png");
            ChangeTheme(this, new RoutedEventArgs());
            await Task.Delay(1000);
            await CaptureAsync("details-dark.png");
            App.MainWindowInstance!.AppWindow.Resize(new Windows.Graphics.SizeInt32(780, 820));
            DetailsSplit.IsPaneOpen = false;
            await Task.Delay(1000);
            await CaptureAsync("compact-dark.png");
            App.MainWindowInstance.AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 960));
            ChangeTheme(this, new RoutedEventArgs());
            DetailsSplit.IsPaneOpen = false;
            await Task.Delay(800);
            var metrics = new {
                total = _overview?.Total, visibleItems = Items.Count,
                galleryWidth = Gallery.ActualWidth, galleryHeight = Gallery.ActualHeight,
                realizedContainers = Gallery.ItemsPanelRoot?.Children.Count,
                libraryReadyMs = _libraryReadyMs,
                errors = Notice.IsOpen ? Notice.Message : null,
            };
            await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(metrics));
            async Task CaptureAsync(string name) {
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(Root);
                var pixels = await bitmap.GetPixelsAsync();
                var folder = await StorageFolder.GetFolderFromPathAsync(output);
                var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                using var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels);
                var bytes = new byte[pixels.Length]; reader.ReadBytes(bytes);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
                await encoder.FlushAsync();
            }
        } catch (Exception error) { DiagnosticLog.Write(error); }
    }
#endif
}
