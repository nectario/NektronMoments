#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NektronMoments.Controls;
using NektronMoments.Services;
using Windows.Storage;

namespace NektronMoments;

public sealed partial class MainPage
{
    private async Task VerifyUiAsync()
    {
        var sizeBefore = _thumbnailSize;
        var upscaleBefore = Viewer.AllowUpscale;
        var themeBefore = ActualTheme;
        var output = Path.Combine(_bridge.Workspace, "build", "windows-verification-013");
        Directory.CreateDirectory(output);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (_priming && DateTime.UtcNow < deadline) await Task.Delay(250);
            await Task.Delay(1200);
            await CaptureAsync("ribbon-light.png");
            var ribbonIcons = CommandMetrics(RibbonBar);
            var buffered = Items.Count;
            _selected = Items.FirstOrDefault(i => i.MediaType == "Photo" &&
                i.Metadata.TryGetProperty("widthPixels", out var w) && w.GetInt32() is > 32 and < 500 &&
                i.Metadata.TryGetProperty("heightPixels", out var h) && h.GetInt32() is > 32 and < 500)
                ?? Items.FirstOrDefault(i => i.MediaType == "Photo");
            await OpenCanvasAsync(false);
            Viewer.SetAllowUpscale(false);
            await Task.Delay(700);
            var bounded = Viewer.CurrentFit;
            await CaptureAsync("canvas-original-limit.png");
            var viewerIcons = CommandMetrics(Viewer);
            Viewer.SetAllowUpscale(true);
            await Task.Delay(500);
            var expanded = Viewer.CurrentFit;
            await CaptureAsync("canvas-enlargement.png");
            var originalIndex = Viewer.CurrentIndex;
            await Viewer.ToggleSlideshowAsync();
            await Task.Delay(11000); // Enough for the default 5s and persisted 10s intervals.
            var slideAdvanced = Viewer.CurrentIndex != originalIndex;
            Viewer.Stop();
            ChangeTheme(this, new RoutedEventArgs());
            await Task.Delay(500);
            await CaptureAsync("canvas-dark.png");
            ReturnToGallery();
            ApplyThumbnailSize(144, false);
            await Task.Delay(900);
            await CaptureAsync("thumbnails-small.png");
            ApplyThumbnailSize(432, false);
            await Task.Delay(900);
            await CaptureAsync("thumbnails-xlarge.png");
            ApplyThumbnailSize(240, false);
            await Task.Delay(500);
            var scroll = FindScroll(Gallery);
            var frameGaps = new List<double>();
            var clock = Stopwatch.StartNew();
            var previous = clock.Elapsed.TotalMilliseconds;
            void Frame(object? sender, object args) {
                var now = clock.Elapsed.TotalMilliseconds;
                if (now - previous > 0) frameGaps.Add(now - previous);
                previous = now;
            }
            CompositionTarget.Rendering += Frame;
            try {
                for (var i = 1; i <= 6; i++) {
                    scroll?.ChangeView(null, Math.Min(scroll.ScrollableHeight, i * 1000), null, false);
                    await Task.Delay(500);
                }
            } finally { CompositionTarget.Rendering -= Frame; }
            await CaptureAsync("buffered-scroll.png");
            App.MainWindowInstance!.AppWindow.Resize(new Windows.Graphics.SizeInt32(780, 820));
            await Task.Delay(900);
            await CaptureAsync("compact-dark.png");
            App.MainWindowInstance.AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 960));
            frameGaps.Sort();
            var metrics = new {
                total = _overview?.Total, bufferedItems = buffered, visibleRecords = Items.Count,
                realizedContainers = Gallery.ItemsPanelRoot?.Children.Count,
                profile = PerformanceProfile.Current.Name, ramGiB = PerformanceProfile.PhysicalBytes / 1073741824d,
                libraryReadyMs = _libraryReadyMs, originalSizeScale = bounded.Scale, enlargedScale = expanded.Scale,
                slideAdvanced, frameSamples = frameGaps.Count,
                frameGapP95Ms = frameGaps.Count == 0 ? 0 : frameGaps[(int)(.95 * (frameGaps.Count - 1))],
                decodedMiB = MediaThumbnail.Decoded.Bytes / 1048576d,
                encodedMiB = ThumbnailService.Shared.EncodedBytes / 1048576d,
                ribbonIcons, viewerIcons,
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
        }
        catch (Exception error) { DiagnosticLog.Write(error); }
        finally {
            Viewer.SetAllowUpscale(upscaleBefore); ReturnToGallery();
            ApplyThumbnailSize(sizeBefore, false);
            if (ActualTheme != themeBefore) ChangeTheme(this, new RoutedEventArgs());
        }
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    private static object[] CommandMetrics(DependencyObject root)
    {
        return Descendants(root).OfType<AppBarButton>()
            .Where(button => !button.IsInOverflow && button.Icon is not null)
            .Select(button => new {
                button.Label, button.ActualWidth, button.ActualHeight,
                iconHeight = Descendants(button).OfType<Viewbox>()
                    .FirstOrDefault(box => box.Name == "ContentViewbox")?.ActualHeight,
            }).Cast<object>().ToArray();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
#endif
