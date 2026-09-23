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
    private bool _reconnectingForProcessing;
    private async void ProcessMetadata(object sender, RoutedEventArgs e)
    {
        try { await ProcessPhotosAsync(); }
        catch (Exception error) { ShowError(error); }
    }
    private async Task ProcessPhotosAsync()
    {
        if (_importing) { await ShowProcessingProgressAsync(); return; }
        if (_overview is null) {
            if (_reconnectingForProcessing) return;
            _reconnectingForProcessing = true;
            ProcessButton.Label = "Connecting…";
            StatusText.Text = "Connecting to your library before opening processing options…";
            try {
                // Join an existing startup load instead of racing it or forcing
                // another full catalog rebuild. This does not start processing.
                await ReloadAsync(false, invalidateThumbnails: false);
                if (_lifetime.IsCancellationRequested) return;
                if (_overview is null) {
                    ShowError(new InvalidOperationException("Processing options need your library connection. Reconnection did not finish; use Refresh to retry. No processing was started."));
                    return;
                }
            } finally {
                _reconnectingForProcessing = false;
                ProcessButton.Label = "Process metadata";
            }
        }
        var sources = _overview.Sources.Where(s => _sourceId.Length == 0 || s.Id == _sourceId).ToArray();
        if (sources.Length == 0) { StatusText.Text = "Add a local folder to process its metadata."; return; }
        var aiOptions = _aiOptions;
        var selection = await ShowProcessingOptionsAsync(sources.Select(source => source.Name).ToArray(), aiOptions);
        if (selection is null || _lifetime.IsCancellationRequested) return;
        await RunMetadataJobAsync(async token => {
            for (var index = 0; index < sources.Length; index++) {
                var source = sources[index];
                _metadataProgress!.Source(source.Name, index + 1, sources.Length);
                PaintProcessingProgress();
                await RunSyncAsync(source.Id, false, token, withEnrichment: selection.IncludeAi, aiOptions: selection.Options);
                _metadataProgress.CompleteSource();
            }
        }, sourceNames: sources.Select(source => source.Name).ToArray(), withEnrichment: selection.IncludeAi, aiOptions: selection.Options);
    }
    private Task<Models.ProcessingSelection?> ShowProcessingOptionsAsync(IReadOnlyList<string> sources, Models.AiProcessingOptions? options = null)
    {
        if (_lifetime.IsCancellationRequested) return Task.FromResult<Models.ProcessingSelection?>(null);
        EnsureProcessingWindow();
        return _processingWindow!.ShowSetup(sources, options ?? _aiOptions, ActualTheme);
    }
    private async Task RunMetadataJobAsync(Func<CancellationToken, Task> work, Func<Task>? refreshForVerification = null, IReadOnlyList<string>? sourceNames = null, bool withEnrichment = false, Models.AiProcessingOptions? aiOptions = null, bool showProgress = true)
    {
        if (_importing) return;
        _jobAiOptions = aiOptions ?? _aiOptions;
        _importing = true; AddFolderButton.IsEnabled = false; ProcessButton.IsEnabled = true;
        ProcessButton.Label = "View progress";
        ToolTipService.SetToolTip(ProcessButton, "Show the current processing job, its stages and activity log.");
        CancelProcessingButton.Visibility = Visibility.Visible; CancelProcessingButton.IsEnabled = true;
        _jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SetBusy(true); Notice.IsOpen = false;
        StartProcessingPresentation(sourceNames, withEnrichment, showProgress);
        var stopped = false;
        Exception? failure = null;
        try {
            await work(_jobCancellation.Token);
            _metadataProgress!.Refreshing(); PaintProcessingProgress();
        } catch (OperationCanceledException) { stopped = true; }
        catch (Exception error) { failure = error; }
        finally {
            _jobCancellation.Dispose(); _jobCancellation = null;
            CancelProcessingButton.Visibility = Visibility.Collapsed;
            if (!_lifetime.IsCancellationRequested) {
                if (refreshForVerification is null) await ReloadAsync(true, invalidateThumbnails: false); else await refreshForVerification();
                if (Notice.IsOpen && failure is null) failure = new InvalidOperationException("Library refresh needs attention.");
                _metadataProgress!.Finish(stopped, failure is not null); PaintProcessingProgress();
                if (failure is not null) ShowError(failure);
            }
            _importing = false; AddFolderButton.IsEnabled = true; ProcessButton.IsEnabled = true;
            ProcessButton.Label = "Process metadata";
            ToolTipService.SetToolTip(ProcessButton, "Choose processing options, including AI descriptions (on by default), then Start.");
            _processingTimer?.Stop();
            SetBusy(false);
        }
    }
    private Task<string> RunSyncAsync(string sourceId, bool fast, CancellationToken token, bool withEnrichment = false, Models.AiProcessingOptions? aiOptions = null)
    {
        if (fast && withEnrichment) throw new InvalidOperationException("Fast registration cannot request enrichment.");
        var progress = _metadataProgress;
        return _bridge.RunCliAsync(fast ? ["sync", sourceId, "--fast-add", "--no-input"] :
            Models.StartupProcessing.Arguments(withEnrichment ? Models.StartupProcessingMode.Full : Models.StartupProcessingMode.FileMetadata, sourceId, aiOptions ?? _aiOptions),
            line => progress?.Report(line), token);
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
                _metadataProgress!.Source(folder.Name, 1, 1); PaintProcessingProgress();
                StatusText.Text = "Adding " + folder.Name + "…";
                var registered = await _bridge.RunCliAsync(["source", "add", LibraryBridge.ToWslPath(folder.Path), "--json"], _ => { }, token);
                using var document = JsonDocument.Parse(registered);
                var id = document.RootElement.GetProperty("sourceId").GetString()!;
                await RunSyncAsync(id, true, token);
                await ReloadAsync(true, invalidateThumbnails: false); // File-version keys preserve previews of unchanged media.
                await RunSyncAsync(id, false, token);
                _metadataProgress.CompleteSource();
            }, sourceNames: [folder.Name]);
        } catch (Exception error) { ShowError(error); }
    }
    private void CancelProcessing(object sender, RoutedEventArgs e)
    {
        if (_jobCancellation is null) return;
        _metadataProgress?.StopRequested(); PaintProcessingProgress();
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
                case "settings": await ShowSettingsAsync(); break;
                case "about": await ShowAboutAsync(); break;
                case "performance":
                    var profile = PerformanceProfile.Current;
                    await ShowDialogAsync("Adaptive performance", new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
                        Text = $"{profile.Name} profile · {PerformanceProfile.PhysicalBytes / (1024d * 1024 * 1024):N0} GB RAM\n\n" +
                        $"{Items.Count:N0} catalog positions · {BrowseItems.Count:N0} in the browsing range\n" +
                        $"{Controls.MediaThumbnail.Decoded.Count:N0} prepared previews cached\n" +
                        $"Up to {Models.BrowsingPolicy.WarmCount(profile.ThumbnailAhead, Controls.MediaThumbnail.TargetPixels, profile.DecodedBytes / 2):N0} compressed previews ahead\n" +
                        $"Up to {Models.BrowsingPolicy.DisplayWarmCount(Models.BrowsingPolicy.WarmCount(profile.ThumbnailAhead, Controls.MediaThumbnail.TargetPixels, profile.DecodedBytes / 2), PerformanceProfile.PhysicalBytes):N0} nearby previews decoded and display-warmed\n" +
                        $"{profile.ViewportCache:N0} viewport cache length\n{profile.DecodedBytes / 1048576:N0} MiB decoded-image budget\n" +
                        $"{profile.EncodedBytes / 1048576:N0} MiB thumbnail-byte budget\n{profile.DiskBytes / 1073741824:N0} GiB disk-thumbnail budget\n\n" +
                        "Budgets are ceilings, not upfront allocations. Only nearby media is decoded; original photos are never loaded en masse." });
                    break;
            }
        } catch (Exception error) { ShowError(error); }
    }
}
