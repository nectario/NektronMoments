using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NektronMoments.Models;
using NektronMoments.Services;
using Windows.Storage.Streams;

namespace NektronMoments.Controls;

public sealed partial class MediaThumbnail : UserControl
{
    public static readonly WeightedCache<BitmapImage> Decoded = new(PerformanceProfile.Current.DecodedBytes);
    private CancellationTokenSource? _loading;
    private string _displayedKey = "";
    private string _displayedIdentity = "";
    private uint _displayedPixels;
    public static uint TargetPixels { get; set; } = 512;
    public static event Action? ResolutionChanged;
    private static bool _resizePreview;
    public static void SetResizePreview(bool active) { _resizePreview = active; if (!active) ResolutionChanged?.Invoke(); }
    public static void SetResolution(uint pixels) { if (TargetPixels == pixels) return; TargetPixels = pixels; ResolutionChanged?.Invoke(); }
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(MediaItem), typeof(MediaThumbnail), new PropertyMetadata(null, ItemChanged));
    public MediaItem? Item { get => (MediaItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public MediaThumbnail()
    {
        InitializeComponent();
        Loaded += (_, _) => { ResolutionChanged += Load; Load(); };
        Unloaded += (_, _) => { ResolutionChanged -= Load; _loading?.Cancel(); Picture.Source = null; _displayedKey = ""; };
    }
    private static void ItemChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (MediaThumbnail)sender;
        control._loading?.Cancel();
        control.Picture.Source = null; control._displayedKey = "";
        if (control.IsLoaded) control.Load();
    }
    private async void Load()
    {
        var item = Item;
        if (item is null) return;
        var requestedPixels = TargetPixels;
        var key = ThumbnailService.Shared.Key(item, requestedPixels);
        var identity = ThumbnailService.Shared.Key(item, 0);
        if (Picture.Source is not null && _displayedIdentity == identity && _displayedPixels >= requestedPixels) return;
        if (_displayedKey == key) return;
        _loading?.Cancel();
        if (Decoded.TryGet(key, out var ready)) {
            Picture.Source = ready; Placeholder.Visibility = Visibility.Collapsed; _displayedKey = key;
            _displayedIdentity = identity; _displayedPixels = requestedPixels;
            return;
        }
        if (_resizePreview) {
            foreach (var pixels in new uint[] { 512, 1024 }) {
                if (pixels < requestedPixels || !Decoded.TryGet(ThumbnailService.Shared.Key(item, pixels), out ready)) continue;
                Picture.Source = ready; Placeholder.Visibility = Visibility.Collapsed;
                _displayedIdentity = identity; _displayedPixels = pixels;
                return;
            }
            return; // New I/O/decode waits until the gesture ends; warm bitmaps can still appear.
        }
        var loading = new CancellationTokenSource();
        _loading = loading;
        Placeholder.Visibility = Picture.Source is null ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var bytes = await ThumbnailService.Shared.LoadAsync(item, requestedPixels, loading.Token);
            if (bytes is null || loading.IsCancellationRequested) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) {
                writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = (int)requestedPixels };
            await bitmap.SetSourceAsync(stream);
            if (loading.IsCancellationRequested || !ReferenceEquals(Item, item)) return;
            Decoded.Put(key, bitmap, Math.Max(1L, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4));
            if (_resizePreview) return;
            Picture.Source = bitmap; _displayedKey = key;
            _displayedIdentity = identity; _displayedPixels = requestedPixels;
            Placeholder.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Missing/unsupported files retain a labeled placeholder. */ }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; loading.Dispose(); }
    }
}
