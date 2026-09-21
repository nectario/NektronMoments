using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NektronMoments;

public sealed partial class MainPage
{
    // Explicit opt-in, available in Release as well as Debug. No user media is captured.
    private async Task VerifyReleaseAssetsAsync(string output)
    {
        var errors = new List<string>();
        var rendered = new List<object>();
        var commandSizes = new List<object>();
        var overflowWidths = new List<double>();
        object? compactLayout = null;
        var svgOpened = 0;
        var shell = (FrameworkElement)App.MainWindowInstance!.Content;
        var previousTheme = shell.RequestedTheme;
        try {
            if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Asset-check output must be absolute.");
            Directory.CreateDirectory(output);
            Busy.Visibility = Visibility.Collapsed;
            AddFolderButton.IsEnabled = ProcessButton.IsEnabled = false;
            LibrarySubtitle.Text = "Release asset verification · No library connection";
            StatusText.Text = "Checking installed artwork";
            var source = new NavigationViewItem { Content = "Source icon check" };
            Navigation.MenuItems.Add(source); _sourceItems.Add(source);
            LibraryCanvas.Visibility = Visibility.Collapsed;
            Viewer.Visibility = Visibility.Visible;
            // Also exercise all shipped SVG URIs (including disabled/high contrast/optical variants).
            var probe = new Image { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(probe, 2); Root.Children.Add(probe);
            try {
                var svgRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "NektronMoments", "icons", "ui", "svg");
                foreach (var file in Directory.EnumerateFiles(svgRoot, "*.svg", SearchOption.AllDirectories)) {
                    var relative = Path.GetRelativePath(AppContext.BaseDirectory, file).Replace('\\', '/');
                    var completion = new TaskCompletionSource<bool>();
                    var image = new SvgImageSource();
                    image.Opened += (_, _) => completion.TrySetResult(true);
                    image.OpenFailed += (_, _) => completion.TrySetResult(false);
                    probe.Source = image;
                    image.UriSource = new Uri("ms-appx:///" + relative);
                    if (await completion.Task.WaitAsync(TimeSpan.FromSeconds(5))) svgOpened++;
                    else errors.Add("SVG failed: " + relative);
                }
            } finally { Root.Children.Remove(probe); }
            if (svgOpened == 0) errors.Add("No shipped SVGs loaded.");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) {
                shell.RequestedTheme = theme;
                UpdateThemeChrome();
                await Task.Delay(650);
                shell.UpdateLayout();
                if (ActivityButton.Icon is not ImageIcon { Source: SvgImageSource activity } ||
                    !activity.UriSource.AbsolutePath.EndsWith("/queued.svg", StringComparison.Ordinal))
                    errors.Add($"{theme}: Activity must use the distinct branded list-and-clock icon.");
                if (ToolTipService.GetToolTip(ActivityButton)?.ToString() != "View processing queues, progress, and items that need attention.")
                    errors.Add("Processing activity tooltip is missing or incorrect.");
                CheckCommandSizes(RibbonBar, "ribbon", 24, 48, 9);
                CheckCommandSizes(Viewer, "viewer", 24, 48, 5);
                var icons = AssetDescendants(shell).OfType<ImageIcon>()
                    .Where(icon => icon.ActualWidth > 0 && icon.ActualHeight > 0 && icon.Visibility == Visibility.Visible).ToArray();
                if (icons.Length < 17) errors.Add($"{theme}: missing expected shell/viewer icon controls ({icons.Length}).");
                foreach (var icon in icons) {
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(icon);
                    using var reader = DataReader.FromBuffer(await bitmap.GetPixelsAsync());
                    var bytes = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(bytes);
                    var painted = 0;
                    for (var i = 3; i < bytes.Length; i += 4) if (bytes[i] > 16) painted++;
                    var uri = icon.Source is SvgImageSource svg ? svg.UriSource?.ToString() :
                        (icon.Source as BitmapImage)?.UriSource?.ToString();
                    rendered.Add(new { theme = theme.ToString(), uri, paintedPixels = painted });
                    if (painted < 4) errors.Add($"{theme}: blank rendered icon {uri}");
                }
                await CaptureAssetShellAsync(shell, output, theme.ToString().ToLowerInvariant() + ".png");
            }
            App.MainWindowInstance.AppWindow.Resize(new Windows.Graphics.SizeInt32(780, 820));
            await Task.Delay(650);
            RibbonBar.IsOpen = true;
            // AppWindow resize and CommandBar's overflow measure complete
            // asynchronously. Wait for the state under test, not a fixed 350ms
            // assumption that can sample the old wide layout on a busy desktop.
            var overflowDeadline = DateTime.UtcNow.AddSeconds(3);
            do {
                await Task.Delay(50);
                shell.UpdateLayout();
            } while (!RibbonBar.PrimaryCommands.OfType<AppBarButton>().Any(button => button.IsInOverflow)
                && DateTime.UtcNow < overflowDeadline);
            await Task.Delay(100);
            compactLayout = new {
                requestedWindowWidth = 780, actualWindowWidth = App.MainWindowInstance.AppWindow.Size.Width,
                pageWidth = ActualWidth, ribbonWidth = RibbonBar.ActualWidth, ribbonMaxWidth = RibbonBar.MaxWidth,
                sizePanelWidth = ThumbnailSizePanel.ActualWidth,
            };
            foreach (var button in RibbonBar.PrimaryCommands.OfType<AppBarButton>().Where(button => button.IsInOverflow)) {
                overflowWidths.Add(button.ActualWidth);
                if (button.ActualWidth < 120) errors.Add($"Overflow label has insufficient space: {button.Label}");
            }
            if (overflowWidths.Count == 0) errors.Add("Compact ribbon overflow was not exercised.");
            RibbonBar.IsOpen = false;
            App.MainWindowInstance.AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 960));
        } catch (Exception error) { errors.Add(error.Message); }
        finally { shell.RequestedTheme = previousTheme; }
        await File.WriteAllTextAsync(Path.Combine(output, "assets.json"), JsonSerializer.Serialize(new {
            passed = errors.Count == 0, svgOpened, rendered, commandSizes, overflowWidths, compactLayout, errors,
            baseDirectory = AppContext.BaseDirectory,
            version = typeof(App).Assembly.GetName().Version?.ToString(),
            libraryConnected = false,
        }));
        StatusText.Text = errors.Count == 0 ? "Release artwork verified" : "Release artwork check failed";
        void CheckCommandSizes(DependencyObject scope, string name, double iconSize, double height, int expected) {
            var commands = AssetDescendants(scope).OfType<AppBarButton>()
                .Where(button => !button.IsInOverflow && button.Icon is not null).ToArray();
            if (commands.Length != expected) errors.Add($"{name}: expected {expected} visible commands, found {commands.Length}.");
            foreach (var button in commands) {
                var box = AssetDescendants(button).OfType<Viewbox>().FirstOrDefault(view => view.Name == "ContentViewbox");
                commandSizes.Add(new { scope = name, button.Label, iconHeight = box?.ActualHeight, button.ActualHeight, button.ActualWidth });
                if (box is null || Math.Abs(box.ActualHeight - iconSize) > 1 || Math.Abs(button.ActualHeight - height) > 1)
                    errors.Add($"{name}: incorrect command size for {button.Label}.");
                if (name == "ribbon" && button.LabelPosition == Microsoft.UI.Xaml.Controls.CommandBarLabelPosition.Collapsed && Math.Abs(button.ActualWidth - 40) > 1)
                    errors.Add($"{name}: incorrect compact width for {button.Label}.");
            }
        }
    }
    private static IEnumerable<DependencyObject> AssetDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in AssetDescendants(child)) yield return nested;
        }
    }
    private static async Task CaptureAssetShellAsync(FrameworkElement shell, string directory, string name)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(shell);
        using var reader = DataReader.FromBuffer(await bitmap.GetPixelsAsync());
        var bytes = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(bytes);
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }
}
