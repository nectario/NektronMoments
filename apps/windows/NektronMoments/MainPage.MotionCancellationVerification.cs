using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace NektronMoments;

public sealed partial class MainPage
{
    // Short synthetic regression for native request ownership. No library, image
    // decoding, OS-input replay, resize stress or fallback-sized waits are involved.
    private async Task VerifyMotionCancellationAsync(string output)
    {
        var errors = new List<string>();
        var cycles = new List<object>();
        var sizeBefore = _thumbnailSize;
        var hitTestBefore = IsHitTestVisible;
        var dialogBefore = _dialog;
        try {
            // The controller is driven by this canary. Live mouse/touch input into
            // its window would create unrelated native destinations in the trace.
            IsHitTestVisible = false; _dialog = true;
            if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Motion-check output must be absolute.");
            Directory.CreateDirectory(output);
            Busy.Visibility = EmptyState.Visibility = Visibility.Collapsed;
            LibrarySubtitle.Text = "10,000 synthetic moments · Motion cancellation verification · No library connection";
            Gallery.ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Grid Background="{ThemeResource MomentsSurfaceBrush}">
                        <TextBlock Text="{Binding}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Grid>
                </DataTemplate>
                """);
            Gallery.ItemsSource = Enumerable.Range(1, 10000).Select(i => $"Moment {i:N0}").ToArray();
            ApplyThumbnailSize(240, false);
            Gallery.UpdateLayout(); GalleryLoaded(this, new RoutedEventArgs());
            var controller = _pixelScroll ?? throw new InvalidOperationException("Motion check has no native controller.");
            var scroll = controller.Scroll;
            controller.WheelDistance = 64; controller.ScrollbarGlideMs = 120;
            // Let first-window layout/SizeChanged finish before starting timed input.
            // Interruption deadlines below are unchanged and start only afterward.
            await Task.Delay(250, _lifetime.Token);
            if (!controller.NativeWheelAnimationEnabled) errors.Add("Motion check must run the native shipping path.");
            foreach (var name in new[] { "reverse-stop-jump1500", "upwheel-jump300", "wheel-drag1200", "stop-at-current", "rapid-latest-jump2000" }) {
                CancelScrollbarGesture();
                controller.JumpTo(500);
                var positionWait = Stopwatch.StartNew();
                while ((controller.IsAnimating || Math.Abs(scroll.VerticalOffset - 500) > 1) && positionWait.ElapsedMilliseconds < 500)
                    await Task.Delay(16, _lifetime.Token);
                if (controller.IsAnimating || Math.Abs(scroll.VerticalOffset - 500) > 1)
                    errors.Add($"{name}: setup jump failed, offset={scroll.VerticalOffset}, target={controller.Target}, mode={controller.ActiveInputMode}.");

                var watch = Stopwatch.StartNew();
                var traces = new List<object>();
                var cancellationsBefore = controller.NativeCancellationAttempts;
                var handledBefore = controller.NativeCancellationsHandled;
                var immediateBefore = controller.NativeImmediateSubmissions;
                var fallbackBefore = controller.NativeScrollFallbacks;
                double? actionAt = null;
                double? firstSettledAt = null;
                double expected = double.NaN;
                void Snapshot(string kind, bool? intermediate = null) => traces.Add(new {
                    milliseconds = watch.Elapsed.TotalMilliseconds, kind, intermediate,
                    offset = scroll.VerticalOffset, target = controller.Target,
                    mode = controller.ActiveInputMode, active = controller.IsAnimating,
                    waitingForCancellation = controller.WaitingForNativeCancellation,
                    scrollbar = GalleryScrollbar.Value,
                });
                void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => Snapshot("view", args.IsIntermediate);
                scroll.ViewChanged += ViewChanged;
                try {
                    Snapshot("begin");
                    if (name == "reverse-stop-jump1500") {
                        controller.QueueWheel(-120);
                        await Task.Delay(35, _lifetime.Token);
                        Snapshot("before-reversal");
                        controller.QueueWheel(120);
                        var reversalWait = Stopwatch.StartNew();
                        while (controller.IsAnimating && reversalWait.ElapsedMilliseconds < 800)
                            await Task.Delay(16, _lifetime.Token);
                        if (controller.IsAnimating) errors.Add("Reversal did not finish within the native motion window.");
                        controller.QueueWheel(-120);
                        await Task.Delay(25, _lifetime.Token);
                        actionAt = watch.Elapsed.TotalMilliseconds;
                        expected = 1500;
                        controller.Stop(); controller.JumpTo(expected);
                    } else if (name == "upwheel-jump300") {
                        controller.QueueWheel(120);
                        await Task.Delay(25, _lifetime.Token);
                        actionAt = watch.Elapsed.TotalMilliseconds;
                        expected = 300;
                        controller.JumpTo(expected);
                    } else if (name == "wheel-drag1200") {
                        controller.QueueWheel(-120);
                        await Task.Delay(25, _lifetime.Token);
                        actionAt = watch.Elapsed.TotalMilliseconds;
                        expected = 1200;
                        controller.SeekFromScrollbar(expected);
                    } else if (name == "stop-at-current") {
                        controller.QueueWheel(-120);
                        await Task.Delay(25, _lifetime.Token);
                        actionAt = watch.Elapsed.TotalMilliseconds;
                        expected = scroll.VerticalOffset;
                        controller.Stop();
                    } else {
                        controller.QueueWheel(-120);
                        await Task.Delay(25, _lifetime.Token);
                        actionAt = watch.Elapsed.TotalMilliseconds;
                        expected = 2000;
                        controller.Stop(); controller.JumpTo(1500); controller.JumpTo(expected);
                    }
                    Snapshot("new-owner");
                    var gateMilliseconds = name == "wheel-drag1200" ? 800 : 400;
                    var gateChecked = false;
                    while (watch.Elapsed.TotalMilliseconds - actionAt.Value < 1000) {
                        await Task.Delay(25, _lifetime.Token);
                        Snapshot("sample");
                        var sinceAction = watch.Elapsed.TotalMilliseconds - actionAt.Value;
                        var atExpected = Math.Abs(scroll.VerticalOffset - expected) <= 1 && !controller.IsAnimating;
                        if (atExpected) firstSettledAt ??= sinceAction;
                        if (!gateChecked && sinceAction >= gateMilliseconds) {
                            gateChecked = true;
                            if (!atExpected)
                                errors.Add($"{name}: target {expected:0.###} not settled by {gateMilliseconds}ms; actual={scroll.VerticalOffset:0.###}, target={controller.Target:0.###}, mode={controller.ActiveInputMode}, active={controller.IsAnimating}.");
                        }
                        if (firstSettledAt is not null && !atExpected)
                            errors.Add($"{name}: motion revived after settling; actual={scroll.VerticalOffset:0.###}, target={expected:0.###}, mode={controller.ActiveInputMode}.");
                    }
                    if (controller.NativeScrollFallbacks != fallbackBefore)
                        errors.Add($"{name}: native cancellation required a fallback; this is not a successful native fix.");
                    if (Math.Abs(scroll.VerticalOffset - expected) > 1 || controller.IsAnimating)
                        errors.Add($"{name}: final offset {scroll.VerticalOffset:0.###} is not settled at {expected:0.###}.");
                    cycles.Add(new {
                        name, expected, actionAt, firstSettledAt,
                        finalOffset = scroll.VerticalOffset, finalTarget = controller.Target,
                        finalMode = controller.ActiveInputMode, finalActive = controller.IsAnimating,
                        cancellationAttempts = controller.NativeCancellationAttempts - cancellationsBefore,
                        cancellationsHandled = controller.NativeCancellationsHandled - handledBefore,
                        immediateSubmissions = controller.NativeImmediateSubmissions - immediateBefore,
                        nativeFallbacks = controller.NativeScrollFallbacks - fallbackBefore,
                        viewsDuringCycle = traces,
                    });
                } finally { scroll.ViewChanged -= ViewChanged; }
            }
            await File.WriteAllTextAsync(Path.Combine(output, "motion.json"), JsonSerializer.Serialize(new {
                passed = errors.Count == 0, errors, cycles,
                itemCount = Gallery.Items.Count, libraryConnected = false, osInputReplay = false,
                controller.NativeCancellationAttempts, controller.NativeCancellationsHandled,
                controller.NativeImmediateSubmissions, controller.NativeScrollFallbacks,
                controller.NativeCancellationCompletions,
                controller.NativeWheelFallbacks, controller.SoftwareWheelFrames,
                measurement = "App-internal native motion ownership; short explicit deadlines before watchdog fallback",
            }));
        } catch (Exception error) {
            errors.Add(error.ToString());
            await File.WriteAllTextAsync(Path.Combine(output, "motion.json"), JsonSerializer.Serialize(new { passed = false, errors, cycles }));
        } finally {
            CancelScrollbarGesture(); _pixelScroll?.Stop();
            _dialog = dialogBefore;
            if (!_lifetime.IsCancellationRequested) { IsHitTestVisible = hitTestBefore; ApplyThumbnailSize(sizeBefore, false); }
        }
    }
}
