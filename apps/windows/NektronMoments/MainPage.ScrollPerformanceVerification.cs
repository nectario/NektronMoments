using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using NektronMoments.Controls;

namespace NektronMoments;

public sealed partial class MainPage
{
    // Native range/automation checks are intentionally not described as mouse-drag replay.
    private async Task<object> MeasureWideScrollingAsync(List<string> errors)
    {
        var window = App.MainWindowInstance!.AppWindow;
        var presenter = (OverlappedPresenter)window.Presenter;
        var controller = _pixelScroll!;
        var oldState = presenter.State;
        var oldSize = _thumbnailSize;
        var oldOffset = controller.Scroll.VerticalOffset;
        var errorsBefore = errors.Count;
        var nativeRanges = new List<object>();
        var wheelTrains = new List<object>();
        try {
            foreach (var maximized in new[] { false, true }) {
                if (maximized) presenter.Maximize(); else presenter.Restore();
                await Task.Delay(350, _lifetime.Token);
                foreach (var size in new[] { 112d, 240 }) {
                    ApplyThumbnailSize(size, false);
                    await Task.Delay(300, _lifetime.Token);
                    Gallery.UpdateLayout();
                    nativeRanges.Add(await MeasureNativeScrollbarAsync(errors));
                    wheelTrains.Add(await MeasureWheelTrainsAsync(errors));
                }
            }
        } finally {
            if (!_lifetime.IsCancellationRequested) {
                controller.Stop();
                if (oldState == OverlappedPresenterState.Maximized) presenter.Maximize(); else presenter.Restore();
                ApplyThumbnailSize(oldSize, false);
                await Task.Delay(250, _lifetime.Token);
                controller.JumpTo(Math.Clamp(oldOffset, 0, controller.Scroll.ScrollableHeight));
                await Task.Delay(150, _lifetime.Token);
                if (oldState == OverlappedPresenterState.Minimized) presenter.Minimize();
            }
        }
        return new {
            passed = errors.Count == errorsBefore, nativeRanges, wheelTrains,
            itemCount = Gallery.Items.Count, osInputReplay = false,
            measurement = "Native ScrollViewer automation, built-in thumb geometry, and wheel trains; not physical dragging or display FPS",
        };
    }

    private async Task<object> MeasureNativeScrollbarAsync(List<string> errors)
    {
        var beforeErrors = errors.Count;
        var controller = _pixelScroll!;
        var scroll = controller.Scroll;
        var bar = GalleryScrollbar;
        // Test windows have hit testing disabled, so no pointer enters to reveal
        // WinUI's mouse indicator. Show that native template state for geometry
        // measurement only; this does not synthesize a drag or change bar.Value.
        bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        bar.UpdateLayout();
        var thumb = GalleryThumb ?? throw new InvalidOperationException("Native vertical thumb is missing.");
        var provider = (IScrollProvider)new ScrollViewerAutomationPeer(scroll).GetPattern(PatternInterface.Scroll);
        var submissions = controller.NativeScrollSubmissions;
        var steps = new List<object>();
        var tops = new List<double>();
        var fullExtent = scroll.ScrollableHeight;
        var realizedMax = 0;
        var expectedTop = 0d;
        var thumbTravel = 0d;
        // The only vertical bar must be inside the same ScrollViewer as the photos.
        if (AssetDescendants(GalleryScrollHost).OfType<ScrollBar>().Count(b => b.Orientation == Orientation.Vertical) != 1)
            errors.Add("Gallery has a second/proxy vertical scrollbar.");
        if (scroll.IsDeferredScrollingEnabled) errors.Add("Native dragging waits for release.");
        if (bar.ActualWidth < 23 || thumb.Width < 13) errors.Add("Native scrollbar width regressed.");

        foreach (var percent in new[] { 0d, 100, 20, 30, 50, 45, 35 }) {
            controller.YieldToNativeInput();
            provider.SetScrollPercent(-1, percent);
            var target = fullExtent * percent / 100;
            var until = Stopwatch.StartNew();
            while (!Models.NativeScrollPrecision.IsAtTarget(scroll.VerticalOffset, target) && until.ElapsedMilliseconds < 1500)
                await Task.Delay(16, _lifetime.Token);
            await Task.Delay(50, _lifetime.Token);
            bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
            bar.UpdateLayout();
            var actual = scroll.VerticalOffset;
            var top = thumb.TransformToVisual(bar).TransformPoint(new Windows.Foundation.Point()).Y;
            if (percent == 0) expectedTop = top;
            if (percent == 100) thumbTravel = top - expectedTop;
            if (!Models.NativeScrollPrecision.IsAtTarget(actual, target)) errors.Add($"Native range {percent}% missed its target.");
            if (Math.Abs(bar.Value - actual) > 1) errors.Add("Windows' scrollbar value disagrees with its viewport.");
            if (bar.Minimum != 0 || Math.Abs(bar.Maximum - fullExtent) > 1) errors.Add("Native scrollbar range changed unexpectedly.");
            if (percent is > 0 and < 100 && Math.Abs(top - expectedTop - thumbTravel * percent / 100) > 2)
                errors.Add("Native thumb geometry does not follow the library position.");
            realizedMax = Math.Max(realizedMax, Gallery.ItemsPanelRoot?.Children.Count ?? 0);
            steps.Add(new { percent, target, actual, thumbTop = top, barWidth = bar.ActualWidth,
                thumbWidth = thumb.ActualWidth, thumbHeight = thumb.ActualHeight,
                indicator = bar.IndicatorMode.ToString(), milliseconds = until.Elapsed.TotalMilliseconds });
            tops.Add(top);
        }
        if (controller.NativeScrollSubmissions != submissions || controller.IsAnimating)
            errors.Add("The wheel controller submitted movement while Windows owned scrolling.");
        if (thumbTravel <= 10) errors.Add("Native thumb has no visible travel.");
        var restingAt = scroll.VerticalOffset;
        await Task.Delay(200, _lifetime.Token);
        if (Math.Abs(restingAt - scroll.VerticalOffset) > 1) errors.Add("Native viewport drifted while idle.");
        var panel = Gallery.ItemsPanelRoot as ItemsWrapGrid ?? throw new InvalidOperationException("Native virtualized panel is missing.");
        var columns = GetGalleryColumns(panel.ItemWidth);
        var visible = (int)Math.Ceiling(scroll.ViewportHeight / panel.ItemHeight + 1) * columns;
        var limit = visible + Math.Max(480, (int)Math.Ceiling(visible * .5)) + columns * 3;
        if (realizedMax > limit) errors.Add("Native gallery lost bounded virtualization.");

        // A queued wheel callback must not fire after native keyboard/bar/automation owns the view.
        controller.QueueWheel(-120);
        controller.YieldToNativeInput();
        var handedOffSubmissions = controller.NativeScrollSubmissions;
        provider.SetScrollPercent(-1, 60);
        await WaitForProbeOffsetAsync(fullExtent * .6);
        await Task.Delay(200, _lifetime.Token);
        if (controller.NativeScrollSubmissions != handedOffSubmissions || controller.IsAnimating)
            errors.Add("A stale wheel callback survived the native input handoff.");
        if (!Models.NativeScrollPrecision.IsAtTarget(scroll.VerticalOffset, fullExtent * .6))
            errors.Add("Wheel-to-native handoff overwrote the native destination.");

        return new {
            passed = beforeErrors == errors.Count, itemCount = Gallery.Items.Count, thumbnailSize = _thumbnailSize,
            windowState = (App.MainWindowInstance!.AppWindow.Presenter as OverlappedPresenter)?.State.ToString(),
            fullExtent, thumbTravel, contentPixelsPerThumbPixel = fullExtent / Math.Max(1, thumbTravel),
            realizedMax, realizedLimit = limit, steps,
            nativeScrollbar = true, addedDragAnimation = false, osInputReplay = false,
        };
    }

    private async Task<object> MeasureWheelTrainsAsync(List<string> errors)
    {
        var controller = _pixelScroll!;
        var scroll = controller.Scroll;
        var beforeErrors = errors.Count;
        var wheelDistance = controller.WheelDistance;
        var samples = new List<object>();
        controller.WheelDistance = 64;
        try {
            foreach (var frequency in new[] { 2, 12, 30, 60 }) {
                CancelScrollbarGesture(); controller.Stop();
                controller.JumpTo(500);
                await WaitForProbeOffsetAsync(500);

                var start = scroll.VerticalOffset;
                var submissions = controller.NativeWheelSubmissions;
                var rejected = controller.NativeWheelRejected;
                var fallbacks = controller.NativeWheelFallbacks;
                var softwareFrames = controller.SoftwareWheelFrames;
                var offsets = new List<double>();
                var eventTimes = new List<double>();
                var barProgressDuringMotion = false;
                var watch = Stopwatch.StartNew();
                void Observe(object? sender, ScrollViewerViewChangedEventArgs args) {
                    offsets.Add(scroll.VerticalOffset);
                    eventTimes.Add(watch.Elapsed.TotalMilliseconds);
                    // WinUI owns scrollbar synchronization; the test never writes Value.
                    // Observe it during motion, not only at the final destination.

                    if (controller.IsWheelMotion && Math.Abs(scroll.VerticalOffset - controller.Target) > 1 &&
                        GalleryScrollbar.Value > start + 1 && Math.Abs(GalleryScrollbar.Value - scroll.VerticalOffset) <= 1)
                        barProgressDuringMotion = true;
                }
                scroll.ViewChanged += Observe;
                double lastTarget;
                try {
                    for (var notch = 0; notch < frequency; notch++) {
                        var due = notch * 1000d / frequency;
                        var delay = due - watch.Elapsed.TotalMilliseconds;
                        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), _lifetime.Token);
                        controller.QueueWheel(-120);
                    }
                    lastTarget = controller.Target;
                    var settling = Stopwatch.StartNew();
                    while (controller.IsAnimating && settling.ElapsedMilliseconds < 3000)
                        await Task.Delay(16, _lifetime.Token);
                    await Task.Delay(80, _lifetime.Token);
                } finally { scroll.ViewChanged -= Observe; }
                var end = scroll.VerticalOffset;
                var distinct = offsets.Select(offset => Math.Round(offset, 2)).Distinct().Count();
                var gaps = eventTimes.Zip(eventTimes.Skip(1), (first, second) => second - first).Order().ToArray();
                if (distinct < 3) errors.Add($"{frequency} Hz wheel train produced no continuous intermediate movement.");
                if (!barProgressDuringMotion) errors.Add($"{frequency} Hz wheel train left the scrollbar frozen until completion.");
                if (controller.IsAnimating || Math.Abs(end - lastTarget) > 1)
                    errors.Add($"{frequency} Hz wheel train did not finish at its latest accumulated destination.");
                if (frequency >= 12 && end - start <= 192)
                    errors.Add($"{frequency} Hz wheel train retained the old three-notch motion cap.");
                if (offsets.Zip(offsets.Skip(1), (first, second) => second + 1 < first).Any(backwards => backwards))
                    errors.Add($"{frequency} Hz downward wheel train jumped backwards between updates.");
                if (controller.NativeWheelAnimationEnabled && (controller.NativeWheelSubmissions == submissions ||
                    controller.NativeWheelFallbacks != fallbacks || controller.SoftwareWheelFrames != softwareFrames))
                    errors.Add($"{frequency} Hz wheel train did not remain on the native animation path.");
                var idle = end;
                await Task.Delay(120, _lifetime.Token);
                if (Math.Abs(scroll.VerticalOffset - idle) > 1) errors.Add($"{frequency} Hz wheel train drifted after settling.");
                samples.Add(new {
                    frequencyHz = frequency, notches = frequency, start, lastTarget, end,
                    requestedTravel = frequency * 64, observedTravel = end - start,
                    distinctOffsets = distinct, barProgressDuringMotion,
                    nativeSubmissions = controller.NativeWheelSubmissions - submissions,
                    nativeRejected = controller.NativeWheelRejected - rejected,
                    nativeFallbacks = controller.NativeWheelFallbacks - fallbacks,
                    fallbackTarget = controller.LastNativeFallbackTarget,
                    fallbackOffset = controller.LastNativeFallbackOffset,
                    fallbackIntermediate = controller.LastNativeFallbackIntermediate,
                    softwareFrames = controller.SoftwareWheelFrames - softwareFrames,
                    offsetEventGapP95Ms = gaps.Length > 0 ? gaps[(int)(.95 * (gaps.Length - 1))] : 0,
                    offsetEventGapMaxMs = gaps.LastOrDefault(), elapsedMs = watch.Elapsed.TotalMilliseconds,
                });
            }
        } finally { controller.Stop(); controller.WheelDistance = wheelDistance; }
        return new {
            passed = errors.Count == beforeErrors, thumbnailSize = _thumbnailSize,
            windowState = App.MainWindowInstance!.AppWindow.Presenter is OverlappedPresenter presenter ? presenter.State.ToString() : "Unknown",
            nativeWheelAnimation = controller.NativeWheelAnimationEnabled, samples,
            measurement = "Native view-change observations; neither display-present FPS nor OS wheel replay",
        };
    }

    private async Task WaitForProbeOffsetAsync(double target)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 2000) {
            if (Models.NativeScrollPrecision.IsAtTarget(_pixelScroll!.Scroll.VerticalOffset, target)) {
                await Task.Delay(50, _lifetime.Token);
                if (Models.NativeScrollPrecision.IsAtTarget(_pixelScroll.Scroll.VerticalOffset, target)) return;
            }
            await Task.Delay(16, _lifetime.Token);
        }
        throw new TimeoutException($"The native viewport did not settle at the test prerequisite {target:0.##}.");
    }
}
