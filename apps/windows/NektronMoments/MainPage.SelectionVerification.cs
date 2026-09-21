using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NektronMoments.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace NektronMoments;

public sealed partial class MainPage
{
    // Opt-in native-control regression checks. Only generated fixture images are
    // used; this does not open the library/WSL, submit jobs or replay OS input.
    private async Task VerifyPhotoSelectionAsync(string output)
    {
        var errors = new List<string>();
        var checks = new List<string>();
        var sizeBefore = _thumbnailSize;
        var upscaleBefore = Viewer.AllowUpscale;
        string? progressPath = null;
        void Progress(string phase) {
            // Native XAML fail-fast errors cannot reach the managed catch below.
            // Keep the last completed probe phase in this isolated output folder.
            if (progressPath is not null) File.AppendAllText(progressPath, $"{DateTime.UtcNow:O} {phase}\n");
        }
        void Check(bool passed, string description) {
            checks.Add(description);
            if (!passed) errors.Add(description);
            Progress((passed ? "PASS " : "FAIL ") + description);
        }
        try {
            if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Selection-check output must be absolute.");
            Directory.CreateDirectory(output);
            progressPath = Path.Combine(output, "selection-progress.log");
            Progress("Generating isolated fixture images");
            var first = await CreateFixtureAsync("fixture-landscape.png", 96, 64, 38, 142, 210);
            var second = await CreateFixtureAsync("fixture-portrait.png", 64, 96, 210, 116, 38);
            var corrupt = Path.Combine(output, "fixture-corrupt.png");
            await File.WriteAllTextAsync(corrupt, "Intentionally invalid generated fixture; not a user photo.");
            Busy.Visibility = EmptyState.Visibility = Visibility.Collapsed;
            AddFolderButton.IsEnabled = ProcessButton.IsEnabled = false;
            LibrarySubtitle.Text = "Photo opening verification · Generated fixtures · No library connection";
            Items.Clear();
            Progress("Populating 100 fixture gallery records");
            for (var i = 0; i < 100; i++) Items.Add(new MediaItem {
                Key = "fixture-" + i, Name = "Fixture photo " + i, Path = i % 2 == 0 ? first.Path : second.Path,
                MediaType = "Photo", Captured = "2026-09-14", Metadata = JsonSerializer.SerializeToElement(new { }),
            });
            Progress("Arranging initial fixture gallery and attaching controller");
            Gallery.UpdateLayout(); GalleryLoaded(this, new RoutedEventArgs());
            Viewer.SetAllowUpscale(false);
            foreach (var size in new[] { 112d, 144, 240, 432 }) {
                Progress($"{size}: returning to gallery and applying thumbnail size");
                ReturnToGallery(); DetailsSplit.IsPaneOpen = false;
                ApplyThumbnailSize(size, false); await Task.Delay(220);
                Progress($"{size}: scrolling fixture item40 into view");
                Gallery.ScrollIntoView(Items[40]); await Task.Delay(150);
                Progress($"{size}: arranging thumbnail target");
                Gallery.UpdateLayout();
                var wrap = (ItemsWrapGrid)Gallery.ItemsPanelRoot;
                var targetIndex = Math.Min(Items.Count - 1, Math.Max(0, wrap.FirstVisibleIndex) + 1);
                var container = Gallery.ContainerFromIndex(targetIndex) as GridViewItem;
                Check(container is not null, $"{size}: a small-tile target is realized");
                if (container is null) continue;
                var nested = AssetDescendants(container).OfType<Image>().FirstOrDefault();
                var target = GalleryItemFromElement(nested ?? (DependencyObject)container);
                Check(target?.Key == Items[targetIndex].Key, $"{size}: nested thumbnail resolves its exact item");
                if (target is null) continue;
                var width = Gallery.ActualWidth;
                var before = container.TransformToVisual(Gallery).TransformPoint(new Windows.Foundation.Point());
                await SelectGalleryItemAsync(target);
                await Task.Delay(150); Gallery.UpdateLayout();
                var after = container.TransformToVisual(Gallery).TransformPoint(new Windows.Foundation.Point());
                Check(!DetailsSplit.IsPaneOpen && Math.Abs(Gallery.ActualWidth - width) < .5,
                    $"{size}: first click does not shrink the gallery");
                Check(Math.Abs(after.X - before.X) < .5 && Math.Abs(after.Y - before.Y) < .5,
                    $"{size}: double-click hit target does not move after first click");
                Progress($"{size}: opening resolved thumbnail in canvas");
                await OpenCanvasAsync(false, target); await Task.Delay(220);
                Check(Viewer.CurrentItemKey == target.Key && Viewer.HasPhoto && Viewer.CurrentFit.Width > 0 && Viewer.CurrentFit.Height > 0,
                    $"{size}: exact selected photo is displayed on first open");
                ReturnToGallery(); await Task.Delay(80);
                Progress($"{size}: reopening the same resolved thumbnail");
                await OpenCanvasAsync(false, target); await Task.Delay(220);
                Check(Viewer.CurrentItemKey == target.Key && Viewer.HasPhoto, $"{size}: the same photo can be reopened");
            }
            ReturnToGallery();
            Check(GalleryItemFromElement(Gallery) is null && GalleryItemFromElement(SortButton) is null,
                "Empty gallery and non-photo controls do not resolve a stale selected photo");
            await OpenCanvasAsync(false, new MediaItem { Key = "removed-fixture" });
            Check(!Viewer.IsOpen, "A removed target never silently opens the first photo");

            // Complete A after B to exercise the former shared-file assignment race.
            Progress("Testing old/new original file completion order");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Viewer.FileResolverForVerification = async path => {
                if (path == first.Path) { entered.TrySetResult(); await release.Task; }
                return await StorageFile.GetFileFromPathAsync(path);
            };
            var older = OpenCanvasAsync(false, Items[0]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await OpenCanvasAsync(false, Items[1]);
            release.TrySetResult(); await older; await Task.Delay(220);
            Check(Viewer.CurrentItemKey == Items[1].Key && Viewer.CurrentOriginalPath == second.Path && Viewer.HasPhoto,
                "A late older file completion cannot replace the newer photo or original path");
            Viewer.SetAllowUpscale(true); await Task.Delay(220);
            Check(Viewer.CurrentItemKey == Items[1].Key && Viewer.CurrentOriginalPath == second.Path && Viewer.HasPhoto,
                "Resizing after a late completion still decodes the newer photo");

            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Progress("Testing close during original file lookup");
            var closing = OpenCanvasAsync(false, Items[0]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ReturnToGallery(); release.TrySetResult(); await closing;
            Check(!Viewer.IsOpen && !Viewer.HasPhoto && Viewer.CurrentOriginalPath is null,
                "Closing during file lookup cannot reopen or populate the closed viewer");
            Viewer.FileResolverForVerification = null;
            Progress("Testing missing and undecodable original feedback");
            var missing = new MediaItem { Key = "missing-fixture", Name = "Missing fixture", MediaType = "Photo", Path = Path.Combine(output, "does-not-exist.png") };
            Items.Add(missing);
            await OpenCanvasAsync(false, missing);
            Check(Viewer.CurrentItemKey == missing.Key && !Viewer.HasPhoto && !string.IsNullOrWhiteSpace(Viewer.UnavailableMessage),
                "Missing original reports an explicit unavailable message instead of a blank photo");
            var invalid = new MediaItem { Key = "corrupt-fixture", Name = "Corrupt fixture", MediaType = "Photo", Path = corrupt };
            Items.Add(invalid);
            await OpenCanvasAsync(false, invalid);
            Check(Viewer.CurrentItemKey == invalid.Key && !Viewer.HasPhoto && !string.IsNullOrWhiteSpace(Viewer.UnavailableMessage),
                "An undecodable original reports an explicit error instead of a blank photo");
            await OpenCanvasAsync(false, Items[1]); await Task.Delay(220);
            Check(Viewer.HasPhoto && Viewer.UnavailableMessage is null,
                "A valid photo opens normally after a missing or corrupt original");
            await CaptureAssetShellAsync((FrameworkElement)App.MainWindowInstance!.Content, output, "selection-photo.png");
        } catch (Exception error) { errors.Add(error.ToString()); Progress("Managed failure: " + error); }
        finally {
            Progress("Restoring fixture preferences and gallery");
            Viewer.FileResolverForVerification = null;
            Viewer.SetAllowUpscale(upscaleBefore);
            ReturnToGallery(); ApplyThumbnailSize(sizeBefore, false);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "selection.json"), JsonSerializer.Serialize(new {
            passed = errors.Count == 0, checks, errors, libraryConnected = false, osInputReplay = false,
            version = typeof(App).Assembly.GetName().Version?.ToString(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Progress("Photo-opening probe complete");
        StatusText.Text = errors.Count == 0 ? "Photo selection and asynchronous opening verified" : "Photo selection verification failed";

        async Task<StorageFile> CreateFixtureAsync(string name, uint width, uint height, byte red, byte green, byte blue) {
            var folder = await StorageFolder.GetFolderFromPathAsync(output);
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = blue; pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255; }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
            await encoder.FlushAsync(); return file;
        }
    }
}
