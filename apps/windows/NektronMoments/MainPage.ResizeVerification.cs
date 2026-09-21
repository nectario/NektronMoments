using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NektronMoments;

public sealed partial class MainPage
{
    private async Task<object> MeasureThumbnailSizingAsync()
    {
        var original = _thumbnailSize;
        var starts = new List<double>();
        var columnCounts = new HashSet<int>();
        var renderCosts = new List<double>();
        var completed = new TaskCompletionSource();
        var watch = Stopwatch.StartNew();
        var startQpcMs = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
        TimeSpan previousTarget = TimeSpan.MinValue;
        var mutations = _thumbnailLayoutChanges;
        BeginThumbnailSizing();
        void Rendering(object? sender, object args) {
            var target = ((RenderingEventArgs)args).RenderingTime;
            if (target == previousTarget) return;
            previousTarget = target;
            var elapsed = watch.Elapsed.TotalMilliseconds;
            if (elapsed >= 4300) { completed.TrySetResult(); return; }
            ThumbnailSlider.Value = 112 + 368 * (.5 - .5 * Math.Cos(elapsed / 1800 * Math.PI));
            if (Gallery.ItemsPanelRoot is Microsoft.UI.Xaml.Controls.ItemsWrapGrid wrap)
                columnCounts.Add(GetGalleryColumns(wrap.ItemWidth));
            if (elapsed >= 300) starts.Add(elapsed); // Initial setup isn't steady-state dragging.
        }
        void Rendered(object? sender, RenderedEventArgs args) {
            if (watch.ElapsedMilliseconds >= 300) renderCosts.Add(args.FrameDuration.TotalMilliseconds);
        }
        CompositionTarget.Rendering += Rendering;
        CompositionTarget.Rendered += Rendered;
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { CompositionTarget.Rendering -= Rendering; CompositionTarget.Rendered -= Rendered; }
        var mutationsDuringDrag = _thumbnailLayoutChanges - mutations;
        var endQpcMs = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
        ThumbnailSlider.Value = 112; // Include the expensive many-column release case.
        CommitThumbnailSizing();
        var commitMs = _lastThumbnailCommitMs;
        await Task.Delay(450);
        ApplyThumbnailSize(original, false);
        Services.UserPreferences.Set("thumbnailSize", original);
        var gaps = starts.Zip(starts.Skip(1), (a, b) => b - a).Order().ToArray();
        var cadence = starts.Count > 1 ? (starts.Count - 1) * 1000 / (starts[^1] - starts[0]) : 0;
        return new {
            updates = starts.Count, updateCadenceHz = cadence,
            frameGapP95Ms = gaps.Length == 0 ? 0 : gaps[(int)(.95 * (gaps.Length - 1))],
            frameGapMaxMs = gaps.LastOrDefault(), renderCostMaxMs = renderCosts.Count == 0 ? 0 : renderCosts.Max(),
            mutationsDuringDrag, commitMs, galleryItems = Gallery.Items.Count, startQpcMs, endQpcMs, sliderEventsAttached = _sliderEventsAttached,
            liveColumnCounts = _liveColumns.Order().ToArray(), liveReflowUpdates = _liveReflowUpdates,
            visibleContainers = Gallery.ItemsPanelRoot?.Children.Count,
            // This is app update/render instrumentation, not a hardware presentation trace.
            measurement = "Distinct XAML rendering targets and XAML frame costs; not display-present FPS",
        };
    }
    private async Task VerifyPhotoResizeAsync(string output)
    {
        Directory.CreateDirectory(output);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_loadingCatalog && DateTime.UtcNow < deadline) await Task.Delay(150);
        await Task.Delay(2000);
        if (File.Exists(Path.Combine(output, "wait-for-profiler.flag"))) {
            await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { processId = Environment.ProcessId }));
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(Path.Combine(output, "start.flag")) && DateTime.UtcNow < until) await Task.Delay(100);
        }
        try {
            var scrollErrors = new List<string>();
            var photoScrolling = await MeasureWideScrollingAsync(scrollErrors);
            await File.WriteAllTextAsync(Path.Combine(output, "photo-scrolling.json"), JsonSerializer.Serialize(new { errors = scrollErrors, result = photoScrolling }));
            var result = await MeasureThumbnailSizingAsync();
            await File.WriteAllTextAsync(Path.Combine(output, "resize.json"), JsonSerializer.Serialize(result));
            var savedSize = _thumbnailSize;
            BeginThumbnailSizing();
            foreach (var size in new[] { 144, 240, 336, 432 }) {
                ThumbnailSlider.Value = size;
                await Task.Delay(220);
                await CaptureAssetShellAsync((FrameworkElement)App.MainWindowInstance!.Content, output, $"live-{size}.png");
            }
            CommitThumbnailSizing(); ApplyThumbnailSize(savedSize, false); Services.UserPreferences.Set("thumbnailSize", savedSize);
        } catch (Exception error) {
            CommitThumbnailSizing();
            await File.WriteAllTextAsync(Path.Combine(output, "resize.json"), JsonSerializer.Serialize(new { error = error.Message }));
        }
    }
}
