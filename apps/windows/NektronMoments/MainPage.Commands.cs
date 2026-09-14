using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NektronMoments.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace NektronMoments;

public sealed partial class MainPage
{
    private CancellationTokenSource? _jobCancellation;
    private async void ProcessMetadata(object sender, RoutedEventArgs e)
    {
        if (_overview is null || _importing) return;
        var sources = _overview.Sources.Where(s => _sourceId.Length == 0 || s.Id == _sourceId).ToArray();
        await RunMetadataJobAsync(async token => {
            foreach (var source in sources) {
                StatusText.Text = "Processing metadata · " + source.Name;
                await RunSyncAsync(source.Id, false, token);
            }
        });
    }
    private async Task RunMetadataJobAsync(Func<CancellationToken, Task> work)
    {
        if (_importing) return;
        _importing = true; AddFolderButton.IsEnabled = false; ProcessButton.IsEnabled = false;
        CancelProcessingButton.Visibility = Visibility.Visible; CancelProcessingButton.IsEnabled = true;
        _jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SetBusy(true); Notice.IsOpen = false;
        var stopped = false;
        Exception? failure = null;
        try {
            await work(_jobCancellation.Token);
            StatusText.Text = "Metadata processing complete · Refreshing your library…";
        } catch (OperationCanceledException) { stopped = true; }
        catch (Exception error) { failure = error; }
        finally {
            _importing = false; _jobCancellation.Dispose(); _jobCancellation = null;
            AddFolderButton.IsEnabled = true; ProcessButton.IsEnabled = true; CancelProcessingButton.Visibility = Visibility.Collapsed;
            if (!_lifetime.IsCancellationRequested) {
                await ReloadAsync(true);
                StatusText.Text = stopped ? "Stopped safely · Saved work resumes next time" :
                    "Library refreshed · No paid enrichment was started";
                if (failure is not null) ShowError(failure);
            }
            SetBusy(false);
        }
    }
    private Task<string> RunSyncAsync(string sourceId, bool fast, CancellationToken token)
    {
        var throttle = Stopwatch.StartNew();
        return _bridge.RunCliAsync(fast ? ["sync", sourceId, "--fast-add", "--no-input"] : ["sync", sourceId, "--no-input"], line => {
            if (throttle.ElapsedMilliseconds < 120) return;
            throttle.Restart();
            DispatcherQueue.TryEnqueue(() => StatusText.Text = line.Trim());
        }, token);
    }
    private async Task AddFolderAsync()
    {
        if (_importing) return;
        try {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            await RunMetadataJobAsync(async token => {
                StatusText.Text = "Adding " + folder.Name + "…";
                var registered = await _bridge.RunCliAsync(["source", "add", LibraryBridge.ToWslPath(folder.Path), "--json"], _ => { }, token);
                using var document = JsonDocument.Parse(registered);
                var id = document.RootElement.GetProperty("sourceId").GetString()!;
                await RunSyncAsync(id, true, token);
                await ReloadAsync(true); // Browse newly discovered files before detailed extraction finishes.
                await RunSyncAsync(id, false, token);
            });
        } catch (Exception error) { ShowError(error); }
    }
    private void CancelProcessing(object sender, RoutedEventArgs e)
    {
        _jobCancellation?.Cancel();
        CancelProcessingButton.IsEnabled = false;
        StatusText.Text = "Stopping safely · Keeping completed work…";
    }
    private async void MenuAction(object sender, RoutedEventArgs e)
    {
        try {
            var command = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
            if (command.StartsWith("size:") && double.TryParse(command[5..], out var size)) { ApplyThumbnailSize(size, true); return; }
            switch (command) {
                case "add": await AddFolderAsync(); break;
                case "open": OpenOriginal(sender, e); break;
                case "reveal": RevealOriginal(sender, e); break;
                case "exit": App.MainWindowInstance?.Close(); break;
                case "refresh": await ReloadAsync(true); break;
                case "process": ProcessMetadata(sender, e); break;
                case "cancel": CancelProcessing(sender, e); break;
                case "activity": ShowActivity(sender, e); break;
                case "view": await OpenCanvasAsync(false); break;
                case "gallery": ReturnToGallery(); break;
                case "details": ToggleDetails(sender, e); break;
                case "theme": ChangeTheme(sender, e); break;
                case "fullscreen": ToggleFullScreen(); break;
                case "scroll-settings": await ShowScrollingSettingsAsync(); break;
                case "slideshow": await OpenCanvasAsync(true); break;
                case "pause": await Viewer.ToggleSlideshowAsync(); break;
                case "stop-slideshow": Viewer.Stop(); break;
                case "copy-path":
                case "copy-name":
                    if (_selected is not null) {
                        var data = new DataPackage(); data.SetText(command == "copy-path" ? _selected.Path : _selected.Name);
                        Clipboard.SetContent(data); StatusText.Text = "Copied to clipboard";
                    } break;
                case "settings": case "about": await ShowSettingsAsync(); break;
                case "performance":
                    var profile = PerformanceProfile.Current;
                    await ShowDialogAsync("Adaptive performance", new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
                        Text = $"{profile.Name} profile · {PerformanceProfile.PhysicalBytes / (1024d * 1024 * 1024):N0} GB RAM\n\n" +
                        $"{profile.RecordBuffer:N0} metadata records buffered ahead\n{profile.ThumbnailAhead:N0} thumbnails prefetched ahead\n" +
                        $"{profile.ViewportCache:N0} viewport cache length\n{profile.DecodedBytes / 1048576:N0} MiB decoded-image budget\n" +
                        $"{profile.EncodedBytes / 1048576:N0} MiB thumbnail-byte budget\n{profile.DiskBytes / 1073741824:N0} GiB disk-thumbnail budget\n\n" +
                        "Budgets are ceilings, not upfront allocations. Only nearby media is decoded; original photos are never loaded en masse." });
                    break;
            }
        } catch (Exception error) { ShowError(error); }
    }
}
