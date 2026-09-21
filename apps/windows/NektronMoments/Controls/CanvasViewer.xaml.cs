using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NektronMoments.Models;
using NektronMoments.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Media.Playback;

namespace NektronMoments.Controls;

public sealed partial class CanvasViewer : UserControl
{
    public Func<int, Task<MediaItem?>>? ItemAt { get; set; }
    public event Action? BackRequested;
    public event Action<MediaItem>? ItemChanged;
    private readonly DispatcherTimer _slides = new();
    private CancellationTokenSource? _resize;
    private MediaItem? _item;
    private StorageFile? _file;
    private MediaPlayer? _player;
    private Windows.Media.Core.MediaSource? _videoSource;
    internal bool HasVideoSource => _player is not null && _videoSource is not null;
    internal bool VideoIsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    internal void PlayVideoForVerification() => _player?.Play();
    private int _index, _requestedIndex, _version, _decodeWidth;
    private readonly SemaphoreSlim _decodeGate = new(1);
    private string? _playIconKey;
    private double _pixelWidth, _pixelHeight;
    private bool _decodeByWidth = true;
    private bool _playing, _initialized;
    private double _interval;
    public bool IsOpen { get; private set; }
    public bool IsPlaying => _playing;
    public ImageFit CurrentFit { get; private set; }
    public bool AllowUpscale => Upscale.IsChecked == true;
    public int CurrentIndex => _index;
    internal string? CurrentItemKey => _item?.Key;
    internal string? CurrentOriginalPath => _file?.Path;
    internal bool HasPhoto => Picture.Source is BitmapImage;
    internal string? UnavailableMessage => Unavailable.Visibility == Visibility.Visible ? Unavailable.Text : null;
    // The opt-in, no-library regression runner can delay file resolution to prove
    // that an earlier open never replaces a newer photo after async I/O completes.
    internal Func<string, Task<StorageFile>>? FileResolverForVerification { get; set; }
    public void SetAllowUpscale(bool value) => Upscale.IsChecked = value;

    public CanvasViewer()
    {
        InitializeComponent();
        _interval = Math.Clamp(UserPreferences.Number("slideshowSeconds", 5), 2, 30);
        Upscale.IsChecked = UserPreferences.Flag("allowUpscale");
        _slides.Interval = TimeSpan.FromSeconds(_interval);
        _slides.Tick += async (_, _) => { _slides.Stop(); if (_playing) await MoveAsync(1, true); };
        _initialized = true;
        ActualThemeChanged += (_, _) => UpdatePlayState();
    }
    public async Task OpenAsync(int index, bool slideshow = false)
    {
        IsOpen = true; Visibility = Visibility.Visible; _playing = slideshow; UpdatePlayState();
        await ShowAsync(Math.Max(0, index), slideshow);
    }
    public void Close()
    {
        IsOpen = false; ++_version; Stop(); _resize?.Cancel();
        _item = null; _file = null; Picture.Source = null; LoadingIndicator.IsActive = false; ReleaseVideo();
        Visibility = Visibility.Collapsed;
    }
    private void ReleaseVideo()
    {
        _player?.Pause(); Video.SetMediaPlayer(null);
        if (_player is not null) _player.Source = null;
        _player?.Dispose(); _player = null;
        _videoSource?.Dispose(); _videoSource = null;
        Video.Visibility = Visibility.Collapsed;
    }
    private async Task ShowAsync(int index, bool photosOnly, int direction = 1)
    {
        _requestedIndex = index;
        var version = ++_version;
        _resize?.Cancel();
        _slides.Stop(); LoadingIndicator.IsActive = true; Unavailable.Visibility = Visibility.Collapsed;
        try
        {
            if (ItemAt is null) return;
            MediaItem? item = await ItemAt(index);
            while (photosOnly && item is { MediaType: "Video" }) { index += direction; item = index < 0 ? null : await ItemAt(index); if (version != _version) return; }
            if (version != _version || !IsOpen) return;
            if (item is null) { _requestedIndex = _index; Stop(); PlaybackStatus.Text = "End of this view"; return; }
            _index = index; _item = item; _decodeWidth = 0;
            _requestedIndex = index;
            _pixelWidth = _pixelHeight = 0;
            Picture.Source = null; ReleaseVideo(); _file = null;
            FileTitle.Text = item.Name; ImageInfo.Text = "Opening original…"; ItemChanged?.Invoke(item);
            StorageFile? file = null;
            foreach (var path in item.Paths.Prepend(item.Path).Distinct()) {
                if (version != _version || !IsOpen) return;
                try {
                    file = FileResolverForVerification is { } resolver
                        ? await resolver(path) : await StorageFile.GetFileFromPathAsync(path);
                    break;
                } catch (Exception) { }
            }
            if (version != _version || !IsOpen) return;
            if (file is null) throw new IOException("The original is unavailable. Reconnect its drive or refresh the library.");
            // Keep the result local until the version check. A slow older request
            // must not overwrite the shared file used by a newer decode/resize.
            _file = file;
            if (item.MediaType == "Video") {
                var properties = await file.Properties.GetVideoPropertiesAsync();
                if (version != _version || !IsOpen) return;
                _pixelWidth = properties.Width; _pixelHeight = properties.Height;
                _videoSource = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
                _player = new MediaPlayer { AutoPlay = false, Source = _videoSource };
                _player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() => {
                    if (version == _version) ShowUnavailable("Windows cannot play this format here. Use Open original to try your default player.");
                });
                Video.SetMediaPlayer(_player); Video.Visibility = Visibility.Visible; Picture.Visibility = Visibility.Collapsed;
                ResizeImage();
            } else {
                Picture.Visibility = Visibility.Visible;
                using var stream = await file.OpenReadAsync();
                if (version != _version || !IsOpen) return;
                var decoder = await BitmapDecoder.CreateAsync(stream);
                if (version != _version || !IsOpen) return;
                _pixelWidth = decoder.OrientedPixelWidth; _pixelHeight = decoder.OrientedPixelHeight;
                _decodeByWidth = decoder.PixelWidth >= decoder.PixelHeight;
                // The formerly collapsed viewer can still have a zero-sized
                // viewport on its first open; arrange it before fitting/decoding.
                if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) UpdateLayout();
                ResizeImage();
                await DecodeAsync(version);
            }
            if (version != _version || !IsOpen) return;
            UpdatePlayState();
            if (_playing) _slides.Start();
        }
        catch (Exception error) {
            if (version == _version) { Stop(); ShowUnavailable(error is IOException ? error.Message : "This image cannot be decoded by Windows. Use Open original to view it externally."); }
        }
        finally { if (version == _version) LoadingIndicator.IsActive = false; }
    }
    private void ShowUnavailable(string message) { Unavailable.Text = message; Unavailable.Visibility = Visibility.Visible; }
    public async Task MoveAsync(int step, bool? photosOnly = null)
    {
        if (!IsOpen) return;
        await ShowAsync(Math.Max(0, _requestedIndex + step), photosOnly ?? _playing, Math.Sign(step));
    }
    public async Task ToggleSlideshowAsync()
    {
        if (!IsOpen) return;
        _playing = !_playing; _slides.Stop(); UpdatePlayState();
        if (_playing && _item?.MediaType == "Video") await MoveAsync(1, true);
        else if (_playing && !LoadingIndicator.IsActive) _slides.Start();
    }
    public void Stop() { _playing = false; _slides.Stop(); UpdatePlayState(); }
    private void UpdatePlayState()
    {
        if (!_initialized) return;
        PlayButton.Label = _playing ? "Pause slideshow" : "Start slideshow";
        var theme = ActualTheme == ElementTheme.Dark ? "dark" : "light";
        var icon = theme + "/" + (_playing ? "pause" : "play");
        if (icon != _playIconKey) { PlayIcon.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/NektronMoments/icons/ui/svg/{icon}.svg")); _playIconKey = icon; }
        PlaybackStatus.Text = _playing ? $"Slideshow · {_interval:0}s" : "Item " + (_index + 1).ToString("N0");
    }
    private void ResizeImage()
    {
        if (!_initialized || !IsOpen) return;
        var raster = XamlRoot?.RasterizationScale ?? 1;
        CurrentFit = ViewingPolicy.Fit(_pixelWidth, _pixelHeight, Viewport.ActualWidth, Viewport.ActualHeight, raster, AllowUpscale);
        Picture.Width = Video.Width = CurrentFit.Width;
        Picture.Height = Video.Height = CurrentFit.Height;
        ImageInfo.Text = $"{_pixelWidth:N0} × {_pixelHeight:N0} px · {CurrentFit.Scale:P0}" + (!AllowUpscale ? " · Original-size limit" : " · Enlargement allowed");
    }
    private async Task DecodeAsync(int version)
    {
        await _decodeGate.WaitAsync();
        try { await DecodeCoreAsync(version); }
        finally { _decodeGate.Release(); }
    }
    private async Task DecodeCoreAsync(int version)
    {
        if (version != _version || !IsOpen || _file is not { } file || _item?.MediaType != "Photo") return;
        if (CurrentFit.Width <= 0 || CurrentFit.Height <= 0) return;
        var required = Math.Max(1, (int)Math.Ceiling(Math.Min(Math.Max(_pixelWidth, _pixelHeight),
            Math.Max(CurrentFit.Width, CurrentFit.Height) * (XamlRoot?.RasterizationScale ?? 1))));
        if (_decodeWidth >= required) return;
        var decodeByWidth = _decodeByWidth;
        using var stream = await file.OpenReadAsync();
        if (version != _version || !IsOpen) return;
        var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Physical };
        if (decodeByWidth) bitmap.DecodePixelWidth = required;
        else bitmap.DecodePixelHeight = required;
        await bitmap.SetSourceAsync(stream);
        if (version != _version || !IsOpen || required < _decodeWidth) return;
        Picture.Source = bitmap; _decodeWidth = required;
    }
    private async void ViewerResized(object sender, SizeChangedEventArgs e)
    {
        ResizeImage(); _resize?.Cancel();
        if (!IsOpen) return;
        var resize = new CancellationTokenSource(); _resize = resize; var version = _version;
        try { await Task.Delay(180, resize.Token); if (version == _version) await DecodeAsync(version); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Keep the already displayed image when a resize decode fails. */ }
        finally { if (ReferenceEquals(_resize, resize)) _resize = null; resize.Dispose(); }
    }
    private async void UpscaleChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ResizeImage(); ViewerResized(this, null!);
        try { await UserPreferences.SetAsync("allowUpscale", AllowUpscale); } catch (Exception) { }
    }
    private void BackClick(object sender, RoutedEventArgs e) { Close(); BackRequested?.Invoke(); }
    private async void PreviousClick(object sender, RoutedEventArgs e) => await MoveAsync(-1);
    private async void NextClick(object sender, RoutedEventArgs e) => await MoveAsync(1);
    private async void PlayClick(object sender, RoutedEventArgs e) => await ToggleSlideshowAsync();
    private void StopClick(object sender, RoutedEventArgs e) => Stop();
    private async void IntervalClick(object sender, RoutedEventArgs e)
    {
        if (sender is AppBarButton button && double.TryParse(button.Tag?.ToString(), out var seconds)) {
            _interval = seconds; _slides.Interval = TimeSpan.FromSeconds(seconds);
            UpdatePlayState();
            try { await UserPreferences.SetAsync("slideshowSeconds", seconds); } catch (Exception) { }
        }
    }
}
