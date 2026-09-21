using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace NektronMoments;

public sealed partial class MainPage
{
    private async Task VerifyRealPhotoReorderAsync(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>(); var errors = new List<string>();
        var before = BrowseItems.ToArray();
        var held = _browseThumbTracking;
        var previousSize = _thumbnailSize;
        try {
            if (before.Length < 20) throw new InvalidOperationException("The real library needs at least 20 visible entries for this test.");
            _browseThumbTracking = true; _reorderActive = true;
            Controls.MediaThumbnail.SetThumbInput(true);
            _draggedItem = before[0];
            foreach (var size in new[] { 240, 144, 480 }) {
                ApplyThumbnailSize(size, false); await Task.Delay(600);
                _pixelScroll?.JumpTo(0); Gallery.UpdateLayout(); await Task.Delay(200);
                Controls.MediaThumbnail.SetThumbInput(true);
                var sizeOutput = Path.Combine(output, size.ToString()); Directory.CreateDirectory(sizeOutput);
                await VerifyReorderMotionAsync(sizeOutput, before, (passed, description) => {
                    checks.Add(size + " px: " + description); if (!passed) errors.Add(size + " px: " + description);
                });
            }
        } catch (Exception error) { errors.Add(error.ToString()); }
        finally {
            _reorderMotion?.Stop();
            if (!BrowseItems.SequenceEqual(before)) BrowseItems.ReplaceAll(before);
            _draggedItem = null; _reorderActive = false; _browseThumbTracking = held;
            ApplyThumbnailSize(previousSize, false);
            Controls.MediaThumbnail.SetThumbInput(held); QueueGalleryWork();
        }
        await File.WriteAllTextAsync(Path.Combine(output, "real-reorder.json"), JsonSerializer.Serialize(new {
            passed = errors.Count == 0, checks, errors, realLibrary = true, catalogCount = Items.Count,
            savedOrderChanged = false, originalsChanged = false, paidJobsStarted = false,
            osPointerReplay = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private async Task VerifyReorderMotionAsync(string output, Models.MediaItem[] before, Action<bool, string> check)
    {
        // Independent animations can be throttled in an occluded/hidden window.
        // This probe must exercise a shown native surface, not just its item model.
        App.MainWindowInstance!.AppWindow.Show(); App.MainWindowInstance.Activate();
        Gallery.UpdateLayout(); await Task.Delay(500);
        var columns = GetGalleryColumns(((ItemsWrapGrid)Gallery.ItemsPanelRoot).ItemWidth);
        var peer = before[columns];
        Point Position() => ((FrameworkElement)Gallery.ContainerFromIndex(BrowseItems.IndexOf(peer))).TransformToVisual(Gallery).TransformPoint(new Point());
        var start = Position();
        var offsetBefore = _pixelScroll!.Scroll.VerticalOffset;
        var samples = new List<object>();
        var positions = new List<Point>();
        var clock = Stopwatch.StartNew();
        var sampling = true;
        void SampleFrame(object? sender, object args) {
            if (!sampling) return;
            var point = Position(); positions.Add(point);
            samples.Add(new { elapsedMs = clock.Elapsed.TotalMilliseconds, x = point.X, y = point.Y });
        }
        // Query independent-animation transforms on rendering ticks, not an
        // unrelated dispatcher timer that can observe only the final layout.
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += SampleFrame;
        try {
        PreviewReorder(columns + 2, true);
        Gallery.UpdateLayout();
        await Task.Delay(400);
        sampling = false;
        await Task.Delay(200);
        var finish = Position();
        static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var moved = Distance(start, finish) > 10;
        var intermediate = positions.Count(point => Distance(point, start) > 2 && Distance(point, finish) > 2);
        var distinctPositions = positions.Select(point => (Math.Round(point.X), Math.Round(point.Y))).Distinct().Count();
        await File.WriteAllTextAsync(Path.Combine(output, "reorder-motion.json"), JsonSerializer.Serialize(new {
            systemAnimationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
            windowVisible = App.MainWindowInstance.AppWindow.IsVisible,
            columns, start, finish, intermediate, distinctPositions, offsetBefore, offsetAfter = _pixelScroll.Scroll.VerticalOffset, samples,
            timingSource = "XAML Rendering callbacks; not GPU-present FPS"
        }, new JsonSerializerOptions { WriteIndented = true }));
        check(moved && intermediate >= 2 && distinctPositions >= 3, "A rendered peer thumbnail traverses intermediate positions while crossing a row during drag preview");
        check(_reorderMotion?.ActiveCount == 0, "Finished reorder animation releases its transforms and storyboard ownership");
        RestoreDragOrder(before); await Task.Delay(400);
        await SaveFeatureImageAsync(Gallery, Path.Combine(output, "reorder-before.png"), 0);
        PreviewReorder(columns + 2, true); await Task.Delay(80);
        var wasAnimating = _reorderMotion?.ActiveCount > 0;
        var beforeRetarget = Position();
        PreviewReorder(0, false);
        var afterRetarget = Position();
        await File.WriteAllTextAsync(Path.Combine(output, "reorder-retarget.json"), JsonSerializer.Serialize(new {
            beforeRetarget, afterRetarget, wasAnimating, distance = Distance(beforeRetarget, afterRetarget)
        }, new JsonSerializerOptions { WriteIndented = true }));
        check(wasAnimating && Distance(beforeRetarget, afterRetarget) < 40,
            "Reversing a live drag retargets from the displayed position without snapping to the previous destination");
        await Task.Delay(400);
        RestoreDragOrder(before); await Task.Delay(400);
        check(BrowseItems.SequenceEqual(before) && _reorderMotion?.ActiveCount == 0,
            "Cancel after rapid animated previews restores order and retires all thumbnail transforms");
        // Keep screenshot encoding outside the timed retarget test.
        PreviewReorder(columns + 2, true); await Task.Delay(80);
        await SaveFeatureImageAsync(Gallery, Path.Combine(output, "reorder-moving.png"), 0);
        RestoreDragOrder(before); await Task.Delay(400);
        await SaveFeatureImageAsync(Gallery, Path.Combine(output, "reorder-restored.png"), 0);
        } finally { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= SampleFrame; }
    }
}
