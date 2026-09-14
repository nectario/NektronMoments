using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace NektronMoments;

public sealed partial class MainPage
{
    // Uses the actual native gallery/controller with synthetic labels, never user media.
    private async Task VerifyPixelScrollingAsync(string output)
    {
        var errors = new List<string>();
        var samples = new List<object>();
        var intermediateOffsets = new List<double>();
        var sizeBefore = _thumbnailSize;
        try {
            if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Scroll-check output must be absolute.");
            Directory.CreateDirectory(output);
            Busy.Visibility = EmptyState.Visibility = Visibility.Collapsed;
            LibrarySubtitle.Text = "100,000 synthetic moments · Pixel-scroll verification · No library connection";
            Gallery.ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Grid Background="{ThemeResource MomentsSurfaceBrush}" CornerRadius="10">
                        <TextBlock Text="{Binding}" FontSize="18" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Grid>
                </DataTemplate>
                """);
            Gallery.ItemsSource = Enumerable.Range(1, 100000).Select(i => $"Moment {i:N0}").ToArray();
            _expandedViewportCache = true;
            Gallery.UpdateLayout(); GalleryLoaded(this, new RoutedEventArgs());
            if (_pixelScroll is null) throw new InvalidOperationException("Pixel controller was not attached to the native gallery.");
            var scroll = _pixelScroll.Scroll;
            scroll.ViewChanged += Observe;
            try {
                foreach (var size in new[] { 144d, 240, 336, 432 }) {
                    ApplyThumbnailSize(size, false); await Task.Delay(250);
                    await Position(500);
                    var start = scroll.VerticalOffset;
                    var anchorIndex = ((ItemsWrapGrid)Gallery.ItemsPanelRoot).FirstVisibleIndex;
                    var anchor = (FrameworkElement)Gallery.ContainerFromIndex(anchorIndex);
                    var anchorTop = anchor.TransformToVisual(Gallery).TransformPoint(new Windows.Foundation.Point()).Y;
                    intermediateOffsets.Clear();
                    _pixelScroll.QueueWheel(-120); await Settle();
                    var distance = scroll.VerticalOffset - start;
                    var pixelMovement = anchorTop - anchor.TransformToVisual(Gallery).TransformPoint(new Windows.Foundation.Point()).Y;
                    samples.Add(new { thumbnailSize = size, distance, pixelMovement, intermediateCount = intermediateOffsets.Distinct().Count() });
                    if (Math.Abs(distance - 48) > 1) errors.Add($"Size {size}: one notch moved {distance}, expected 48 pixels.");
                    if (Math.Abs(pixelMovement - 48) > 1) errors.Add($"Size {size}: photo moved {pixelMovement} canvas pixels, expected 48.");
                    if (intermediateOffsets.Distinct().Count() < 3) errors.Add($"Size {size}: no smooth intermediate positions.");
                }
                await Position(500);
                _pixelScroll.QueueWheel(-30); await Settle();
                if (Math.Abs(scroll.VerticalOffset - 512) > 1) errors.Add("Fractional wheel delta did not move 12 pixels.");
                await Position(500);
                for (var i = 0; i < 50; i++) _pixelScroll.QueueWheel(-120);
                await Settle();
                if (scroll.VerticalOffset > 645) errors.Add("Wheel burst accumulated runaway motion.");
                var burstEnd = scroll.VerticalOffset;
                await Position(500);
                _pixelScroll.QueueWheel(-120); await Task.Delay(35);
                var reverseAt = scroll.VerticalOffset;
                _pixelScroll.QueueWheel(120); await Settle();
                var reverseEnd = scroll.VerticalOffset;
                if (Math.Abs(reverseEnd - (reverseAt - 48)) > 1) errors.Add("Direction reversal retained the old destination.");
                _pixelScroll.QueueWheel(-120); await Task.Delay(25);
                _pixelScroll.Stop(); await Position(1500); await Task.Delay(250);
                if (Math.Abs(scroll.VerticalOffset - 1500) > 1 || _pixelScroll.IsAnimating) errors.Add("Cancelled motion pulled the viewport back.");
                await Position(0); _pixelScroll.QueueWheel(120); await Settle();
                if (scroll.VerticalOffset > 1) errors.Add("Top boundary moved unexpectedly.");
                await Position(scroll.ScrollableHeight); _pixelScroll.QueueWheel(-120); await Settle();
                if (scroll.VerticalOffset > scroll.ScrollableHeight + 1) errors.Add("Bottom boundary overscrolled.");
                if (scroll.VerticalSnapPointsType != Microsoft.UI.Xaml.Controls.SnapPointsType.None) errors.Add("Row snapping is enabled.");
                var realized = Gallery.ItemsPanelRoot?.Children.Count ?? 0;
                if (realized is <= 0 or > 1000) errors.Add("Gallery virtualization is not bounded.");
                await Position(540); ApplyThumbnailSize(240, false); await Task.Delay(200);
                await CaptureAssetShellAsync((FrameworkElement)App.MainWindowInstance!.Content, output, "pixel-scroll.png");
                await File.WriteAllTextAsync(Path.Combine(output, "scroll.json"), JsonSerializer.Serialize(new {
                    passed = errors.Count == 0, errors, samples, burstEnd, reverseAt, reverseEnd,
                    itemCount = Gallery.Items.Count, realizedContainers = realized, inputSurface = _pixelScroll.InputSurface,
                    snapPoints = scroll.VerticalSnapPointsType.ToString(), libraryConnected = false,
                }));
            } finally { scroll.ViewChanged -= Observe; }
            void Observe(object? sender, ScrollViewerViewChangedEventArgs args) => intermediateOffsets.Add(scroll.VerticalOffset);
            async Task Position(double value) {
                _pixelScroll.Stop(); scroll.ChangeView(null, value, null, true); await Task.Delay(150);
            }
            async Task Settle() {
                var until = DateTime.UtcNow.AddSeconds(2);
                while (_pixelScroll.IsAnimating && DateTime.UtcNow < until) await Task.Delay(16);
                await Task.Delay(80);
                if (_pixelScroll.IsAnimating) errors.Add("Wheel animation did not settle.");
            }
        } catch (Exception error) {
            errors.Add(error.Message);
            await File.WriteAllTextAsync(Path.Combine(output, "scroll.json"), JsonSerializer.Serialize(new { passed = false, errors }));
        } finally { _pixelScroll?.Stop(); ApplyThumbnailSize(sizeBefore, false); }
    }
}
