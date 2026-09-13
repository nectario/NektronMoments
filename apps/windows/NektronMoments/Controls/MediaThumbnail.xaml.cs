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
    public static uint TargetPixels { get; set; } = 512;
    public static event Action? ResolutionChanged;
    public static void SetResolution(uint pixels) { TargetPixels = pixels; ResolutionChanged?.Invoke(); }
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
        var key = ThumbnailService.Shared.Key(item, TargetPixels);
        if (_displayedKey == key) return;
        _loading?.Cancel();
        if (Decoded.TryGet(key, out var ready)) {
            Picture.Source = ready; Placeholder.Visibility = Visibility.Collapsed; _displayedKey = key;
            return;
        }
        var loading = new CancellationTokenSource();
        _loading = loading;
        Placeholder.Visibility = Picture.Source is null ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var bytes = await ThumbnailService.Shared.LoadAsync(item, TargetPixels, loading.Token);
            if (bytes is null || loading.IsCancellationRequested) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) {
                writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = (int)TargetPixels };
            await bitmap.SetSourceAsync(stream);
            if (loading.IsCancellationRequested || !ReferenceEquals(Item, item)) return;
            Decoded.Put(key, bitmap, Math.Max(1L, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4));
            Picture.Source = bitmap; _displayedKey = key;
            Placeholder.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Missing/unsupported files retain a labeled placeholder. */ }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; loading.Dispose(); }
    }
}
