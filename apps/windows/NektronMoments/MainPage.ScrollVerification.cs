using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace NektronMoments;

public sealed partial class MainPage
{
    // Native control/automation checks, not a replay of physical pointer dragging.
    private async Task VerifyPixelScrollingAsync(string output)
    {
        var errors = new List<string>();
        var samples = new List<object>();
        var intermediateOffsets = new List<double>();
        var activationEvents = new List<object>();
        var diagnosticClock = System.Diagnostics.Stopwatch.StartNew();
        void ActivationChanged(object sender, WindowActivatedEventArgs args) => activationEvents.Add(new {
            ms = diagnosticClock.Elapsed.TotalMilliseconds, state = args.WindowActivationState.ToString(),
            offset = _pixelScroll?.Scroll.VerticalOffset, target = _pixelScroll?.Target,
            input = _pixelScroll?.ActiveInputMode
        });
        var diagnosticWindow = App.MainWindowInstance!;
        var initiallyVisible = diagnosticWindow.AppWindow.IsVisible;
        diagnosticWindow.Activated += ActivationChanged;
        diagnosticWindow.AppWindow.Show(); diagnosticWindow.Activate();
        var sizeBefore = _thumbnailSize;
        try {
            if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Scroll-check output must be absolute.");
            Directory.CreateDirectory(output);
            Busy.Visibility = EmptyState.Visibility = Visibility.Collapsed;
            LibrarySubtitle.Text = "Native scrollbar verification · No library connection";
            Gallery.ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Grid Background="{ThemeResource MomentsSurfaceBrush}" CornerRadius="10">
                        <TextBlock Text="{Binding}" FontSize="18" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Grid>
                </DataTemplate>
                """);
            Gallery.ItemsSource = Enumerable.Range(1, 120).Select(i => $"Moment {i:N0}").ToArray();
            _expandedViewportCache = true;
            Gallery.UpdateLayout(); GalleryLoaded(this, new RoutedEventArgs());
            if (_pixelScroll is null || _nativeWheel is null || _nativeWheel.RegisteredWindows == 0)
                throw new InvalidOperationException("Wheel input is not attached.");
            var scroll = _pixelScroll.Scroll;
            if (scroll.IsDeferredScrollingEnabled) errors.Add("Thumb updates are deferred until release.");
            if (scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Auto) errors.Add("The built-in scrollbar is not visible.");
            var originalRange = await MeasureNativeScrollbarAsync(errors);

            Gallery.ItemsSource = Enumerable.Range(1, 100000).Select(i => $"Moment {i:N0}").ToArray();
            Gallery.UpdateLayout(); await Task.Delay(150);
            _pixelScroll.WheelDistance = 64;
            scroll.ViewChanged += Observe;
            try {
                foreach (var size in new[] { 144d, 240, 336, 432 }) {
                    ApplyThumbnailSize(size, false); await Task.Delay(250);
                    await Position(500);
                    var start = scroll.VerticalOffset;
                    var panel = (ItemsWrapGrid)Gallery.ItemsPanelRoot;
                    var anchor = (FrameworkElement)Gallery.ContainerFromIndex(panel.FirstVisibleIndex);
                    var anchorTop = anchor.TransformToVisual(Gallery).TransformPoint(new Windows.Foundation.Point()).Y;
                    intermediateOffsets.Clear();
                    _pixelScroll.QueueWheel(-120); await Settle();
                    var distance = scroll.VerticalOffset - start;
                    var pixelMovement = anchorTop - anchor.TransformToVisual(Gallery).TransformPoint(new Windows.Foundation.Point()).Y;
                    var hadIntermediatePosition = intermediateOffsets.Any(offset => offset > start + .5 && offset < start + distance - .5);
                    samples.Add(new { thumbnailSize = size, distance, pixelMovement, intermediateCount = intermediateOffsets.Distinct().Count(),
                        hadIntermediatePosition, offsets = intermediateOffsets.ToArray() });
                    if (Math.Abs(distance - 64) > 1 || Math.Abs(pixelMovement - 64) > 1)
                        errors.Add($"Size {size}: wheel did not move the photos exactly 64 pixels.");
                    // ViewChanged coalesces notifications; it is not a present/FPS
                    // counter. Require a real in-between offset, not three callbacks
                    // that could themselves all be endpoint/rounding notifications.
                    if (!hadIntermediatePosition) errors.Add($"Size {size}: wheel had no intermediate motion.");
                }
                await Position(500); _pixelScroll.QueueWheel(-30); await Settle();
                if (Math.Abs(scroll.VerticalOffset - 516) > 1) errors.Add("Fractional wheel input was lost.");
                await Position(500);
                for (var i = 0; i < 50; i++) _pixelScroll.QueueWheel(-120);
                await Settle();
                var burstEnd = scroll.VerticalOffset;
                var burstLimit = Models.NativeWheelTarget.PendingLimit(scroll.ViewportHeight, _pixelScroll.WheelDistance);
                if (burstEnd <= 692 || burstEnd > 501 + burstLimit) errors.Add("Wheel burst was truncated or unbounded.");
                await Position(500);
                _pixelScroll.QueueWheel(-120); await Task.Delay(35);
                var reverseAt = scroll.VerticalOffset;
                _pixelScroll.QueueWheel(120); await Settle();
                if (Math.Abs(scroll.VerticalOffset - (reverseAt - 64)) > 1) errors.Add("Wheel reversal retained an old destination.");
                _pixelScroll.QueueWheel(-120); await Task.Delay(25);
                _pixelScroll.Stop(); await Position(1500); await Task.Delay(250);
                if (Math.Abs(scroll.VerticalOffset - 1500) > 1 || _pixelScroll.IsAnimating) errors.Add("Stopped wheel overwrote navigation.");
                foreach (var speed in new[] { 12d, 144 }) {
                    _pixelScroll.WheelDistance = speed; await Position(500);
                    _pixelScroll.QueueWheel(-120); await Settle();
                    if (Math.Abs(scroll.VerticalOffset - 500 - speed) > 1) errors.Add($"Wheel setting {speed} was not applied.");
                }
                _pixelScroll.WheelDistance = 64;
                await Position(0); _pixelScroll.QueueWheel(120); await Settle();
                if (scroll.VerticalOffset > 1) errors.Add("Top boundary moved.");
                await Position(scroll.ScrollableHeight); _pixelScroll.QueueWheel(-120); await Settle();
                if (Gallery.ItemsPanelRoot is not ItemsWrapGrid endPanel || endPanel.LastVisibleIndex != Gallery.Items.Count - 1)
                    errors.Add("The final catalog item is unreachable.");
                if (scroll.VerticalSnapPointsType != SnapPointsType.None) errors.Add("Wheel row snapping is enabled.");
                await Position(500);
                var bounds = _nativeWheel.GalleryScreenBounds();
                if (!RouteProbeWheel(-120, new Windows.Foundation.Point(bounds.Right - 5, bounds.Y + bounds.Height / 2)))
                    errors.Add("Wheel over scrollbar was not routed.");
                await Settle();
                if (Math.Abs(scroll.VerticalOffset - 564) > 1) errors.Add("Scrollbar wheel added extra motion.");
                if (RouteProbeWheel(-120, new Windows.Foundation.Point(bounds.X - 10, bounds.Y))) errors.Add("Outside wheel intercepted.");
                if (RouteProbeWheel(-120, new Windows.Foundation.Point(bounds.X + 20, bounds.Y + 20), true)) errors.Add("Ctrl-wheel intercepted.");

                var sizing = JsonSerializer.SerializeToElement(await MeasureThumbnailSizingAsync());
                if (sizing.GetProperty("liveReflowUpdates").GetInt64() < 5 ||
                    sizing.GetProperty("mutationsDuringDrag").GetInt64() != 0 ||
                    sizing.GetProperty("liveColumnCounts").GetArrayLength() < 3) errors.Add("Live thumbnail reflow regressed.");
                var wideScrolling = await MeasureWideScrollingAsync(errors);
                if (_pixelScroll.NativeScrollFallbacks != 0) errors.Add("Native wheel animation needed a fallback.");
                var realized = Gallery.ItemsPanelRoot?.Children.Count ?? 0;
                if (realized is <= 0 or > 1000) errors.Add("Gallery virtualization is not bounded.");
                await CaptureAssetShellAsync((FrameworkElement)App.MainWindowInstance!.Content, output, "pixel-scroll.png");
                await File.WriteAllTextAsync(Path.Combine(output, "scroll.json"), JsonSerializer.Serialize(new {
                    passed = errors.Count == 0, errors, samples, originalRange,
                    itemCount = Gallery.Items.Count, realizedContainers = realized,
                    nativeScrollbar = true, addedDragAnimation = false, deferredScrolling = scroll.IsDeferredScrollingEnabled,
                    nativeInputWindows = _nativeWheel.RegisteredWindows,
                    nativeWheelSubmissions = _pixelScroll.NativeWheelSubmissions,
                    nativeWheelFallbacks = _pixelScroll.NativeWheelFallbacks,
                    softwareWheelFrames = _pixelScroll.SoftwareWheelFrames,
                    fullCatalogExtent = scroll.ScrollableHeight, sizing, wideScrolling,
                    initiallyVisible, windowVisible = diagnosticWindow.AppWindow.IsVisible, activationEvents,
                    libraryConnected = false, osInputReplay = false,
                    measurement = "Native template/range/automation and wheel checks; physical drag feel requires user comparison with 0.1.1",
                }));
            } finally { scroll.ViewChanged -= Observe; }
            void Observe(object? sender, ScrollViewerViewChangedEventArgs args) => intermediateOffsets.Add(scroll.VerticalOffset);
            bool RouteProbeWheel(int delta, Windows.Foundation.Point point, bool control = false) {
                var before = IsHitTestVisible;
                try { IsHitTestVisible = true; return _nativeWheel.RouteWheel(delta, point, control: control); }
                finally { IsHitTestVisible = before; }
            }
            async Task Position(double value) {
                CancelScrollbarGesture(); _pixelScroll.JumpTo(value); await WaitForProbeOffsetAsync(value);
            }
            async Task Settle() {
                var until = DateTime.UtcNow.AddSeconds(3);
                while (_pixelScroll.IsAnimating && DateTime.UtcNow < until) await Task.Delay(16);
                await Task.Delay(80);
                if (_pixelScroll.IsAnimating) errors.Add("Wheel animation did not settle.");
            }
        } catch (Exception error) {
            errors.Add(error.ToString());
            await File.WriteAllTextAsync(Path.Combine(output, "scroll.json"), JsonSerializer.Serialize(new { passed = false, errors }));
        } finally { diagnosticWindow.Activated -= ActivationChanged; CancelScrollbarGesture(); _pixelScroll?.Stop(); ApplyThumbnailSize(sizeBefore, false); }
    }
}
