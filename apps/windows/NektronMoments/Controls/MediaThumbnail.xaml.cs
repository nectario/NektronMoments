using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NektronMoments.Models;
using NektronMoments.Services;
using Windows.Storage.Streams;

namespace NektronMoments.Controls;

public sealed partial class MediaThumbnail : UserControl
{
    private static readonly ThumbnailService Thumbnails = new();
    private CancellationTokenSource? _loading;
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(MediaItem), typeof(MediaThumbnail), new PropertyMetadata(null, ItemChanged));
    public MediaItem? Item { get => (MediaItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public MediaThumbnail()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => { _loading?.Cancel(); Picture.Source = null; };
    }
    private static void ItemChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (MediaThumbnail)sender;
        control.Picture.Source = null;
        if (control.IsLoaded) control.Load();
    }
    private async void Load()
    {
        _loading?.Cancel();
        var loading = new CancellationTokenSource();
        _loading = loading;
        var item = Item;
        if (item is null) return;
        Picture.Source = null;
        Placeholder.Visibility = Visibility.Visible;
        try
        {
            var bytes = await Thumbnails.LoadAsync(item, 400, loading.Token);
            if (bytes is null || loading.IsCancellationRequested) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) {
                writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (loading.IsCancellationRequested || !ReferenceEquals(Item, item)) return;
            Picture.Source = bitmap;
            Placeholder.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Unsupported/missing media is represented by the type icon. */ }
        finally { loading.Dispose(); if (ReferenceEquals(_loading, loading)) _loading = null; }
    }
}
