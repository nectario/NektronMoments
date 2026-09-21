using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NektronMoments.Models;
using NektronMoments.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NektronMoments;

public sealed partial class MainPage
{
    private async Task VerifyGalleryFeaturesAsync(string output, string photoPath, Action<bool, string> check)
    {
        var storeBefore = _orderStore; var progressBefore = _metadataProgress;
        var themeRoot = (FrameworkElement)App.MainWindowInstance!.Content;
        var themeBefore = themeRoot.RequestedTheme;
        var choiceBefore = _sortChoice; var sortBefore = _activeSort;
        var sizesBefore = _thumbnailSize;
        try {
            _orderStore = new LibraryOrderStore(Path.Combine(output, "arrangements"));
            var items = Enumerable.Range(0, 1000).Select(index => new MediaItem {
                Key = "feature-" + index, Name = $"Moment {index:0000}.png", Path = photoPath, Paths = [photoPath],
                Captured = "2026-09-19", MediaType = "Photo",
            }).ToArray();
            ApplyThumbnailSize(240, false); Items.ReplaceAll(items); _browseThumbTracking = true;
            await Task.Delay(250); Gallery.UpdateLayout();
            check(BrowseItems.Count == 200, "Medium thumbnails retain the familiar 200-position initial range");
            ApplyThumbnailSize(144, false); _browseThumbTracking = true;
            await Task.Delay(250);
            check(BrowseItems.Count == 200, "Making thumbnails smaller does not alter the range during a held scrollbar gesture");
            _browseThumbTracking = false; QueueGalleryWork();
            await WaitForAsync(() => BrowseItems.Count >= 500);
            _browseThumbTracking = true; Gallery.UpdateLayout();
            check(BrowseItems.Count >= 500 && BrowsingBatch == 500, "Small thumbnails expand to the adaptive 500-position browsing range");
            var retained = BrowseItems.Count;
            ApplyThumbnailSize(336, false); _browseThumbTracking = true; await Task.Delay(250);
            check(BrowseItems.Count == retained && BrowsingBatch == 200, "Growing thumbnails changes future batches without shrinking the current range");
            ApplyThumbnailSize(144, false); _browseThumbTracking = true; await Task.Delay(250);
            UpdateArrangementChrome();
            var dragData = new Windows.ApplicationModel.DataTransfer.DataPackage(); PrepareReorderData(dragData);
            check(dragData.RequestedOperation == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move &&
                dragData.GetView().Contains(ReorderDataFormat) && !dragData.GetView().Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems),
                "Native drag has a private reorder payload without exposing or moving original storage items");
            check(Gallery.CanDragItems && !Gallery.CanReorderItems && Gallery.AllowDrop &&
                Gallery.ItemContainerTransitions.OfType<RepositionThemeTransition>().Any(transition => !transition.IsStaggeringEnabled),
                "Live drag previews use simultaneous native repositioning without a row-by-row cascade");
            var before = BrowseItems.ToArray(); var snapshot = Items.Capture(); var moved = before[2];
            _draggedItem = moved; _reorderActive = true;
            await VerifyReorderMotionAsync(output, before, check);
            PreviewReorder(7, true);
            check(ReferenceEquals(BrowseItems[7], moved) && Items.IndexOf(moved) == 2,
                "Hover previews rearrange visible cards immediately without committing the catalog early");
            PreviewReorder(15, true); RestoreDragOrder(before);
            check(BrowseItems.SequenceEqual(before), "Canceling a multi-row live drag restores the original order");
            PreviewReorder(7, true); _reorderActive = false;
            await CommitArrangementAsync(snapshot, before);
            _browseThumbTracking = true;
            check(ReferenceEquals(BrowseItems[7], moved) && Items.IndexOf(moved) == 7,
                "Committing the native move updates catalog navigation without replacing moved photo objects");
            var arrangement = await _orderStore.LoadAsync(ArrangementScope);
            check(arrangement.Sort == "custom" && arrangement.Keys[7] == moved.Key && arrangement.Keys.Length == BrowseItems.Count,
                "The custom arrangement is saved outside original media storage");
            check(ReferenceEquals(snapshot[2], moved) && _activeSort == "custom", "Reordering leaves in-flight snapshot readers stable and exposes Custom order");
            _thumbnailWarmingSuspended = true;
            await OpenCanvasAsync(false, moved); await WaitForAsync(() => Viewer.HasPhoto);
            check(Viewer.CurrentIndex == 7 && Viewer.CurrentItemKey == moved.Key, "Double-click/view navigation opens the moved photo's exact new position");
            var close = AssetDescendants(Viewer).OfType<Button>().Single(button => button.Name == "CloseViewerButton");
            var point = close.TransformToVisual(Viewer).TransformPoint(new Windows.Foundation.Point());
            check(close.ActualWidth >= 32 && point.X > Viewer.ActualWidth * .8 && point.Y < 60, "The photo close X stays visibly at the upper-right edge");
            check(close.Padding.Left == 0 && close.Padding.Right == 0 && close.Content is FontIcon { IsTextScaleFactorEnabled: false },
                "The close glyph has full content space and cannot be clipped by text scaling or default button padding");
            await SaveFeatureImageAsync(Viewer, Path.Combine(output, "viewer-close.png"));
            ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitForAsync(() => !Viewer.IsOpen);
            check(ReferenceEquals(Gallery.SelectedItem, moved), "Invoking the close X returns to the same arranged photo");
            await SaveFeatureImageAsync(Root, Path.Combine(output, "arrangement-small.png"));

            // Exercise the real job/window owner with a fake worker, never a CLI sync.
            Notice.IsOpen = false;
            await ResetOrderAsync(() => { Items.ReplaceAll(items); return Task.CompletedTask; });
            var resetArrangement = await _orderStore.LoadAsync(ArrangementScope);
            check(resetArrangement.Keys.Length == 0 && resetArrangement.Sort == "newest" && _activeSort == "newest",
                "Reset Order clears the current view's saved arrangement and restores the default sort");
            foreach (var option in new bool?[] { null, true, false }) {
                var setup = ShowProcessingOptionsAsync(["Verification photos"]);
                await WaitForAsync(() => _processingWindow is { IsPreparing: true } && _processingWindow.VisualRoot.IsLoaded);
                var setupWindow = _processingWindow!;
                var checkbox = AssetDescendants(setupWindow.VisualRoot).OfType<CheckBox>().Single(control => control.Name == "IncludeAiChoice");
                check(checkbox.IsChecked == true && !_importing, "Process setup defaults to AI without starting work before Start");
                check(!_dialog && _commonDialog is null, "Processing setup never opens a modal precursor");
                await SaveFeatureImageAsync(setupWindow.VisualRoot, Path.Combine(output, "processing-setup.png"));
                if (option is null) setupWindow.Hide();
                else {
                    checkbox.IsChecked = option.Value;
                    var start = AssetDescendants(setupWindow.VisualRoot).OfType<Button>().Single(button => button.Name == "StartButton");
                    ((IInvokeProvider)new ButtonAutomationPeer(start).GetPattern(PatternInterface.Invoke)).Invoke();
                }
                check((await setup)?.IncludeAi == option && !_importing,
                    $"Process setup returns the requested choice ({option?.ToString() ?? "Cancel"}) without a live provider call");
                if (option is not null) {
                    await RunMetadataJobAsync(_ => Task.CompletedTask, () => Task.CompletedTask, sourceNames: ["Verification photos"], withEnrichment: option.Value);
                    check(ReferenceEquals(setupWindow, _processingWindow) && setupWindow.IsVisible,
                        "Start transitions to progress inside the exact same processing window");
                }
                setupWindow.Hide();
            }
            var settingsItem = (NavigationViewItem)Navigation.SettingsItem;
            Navigation.SelectedItem = settingsItem;
            for (var attempt = 0; attempt < 2; attempt++) {
                var opening = ShowSettingsAsync();
                await WaitForAsync(() => SettingsHost.Visibility == Visibility.Visible && AssetDescendants(SettingsHost).OfType<ComboBox>().Any());
                check(!_dialog && AssetDescendants(SettingsHost).OfType<ComboBox>().Any(),
                    $"Settings command {attempt + 1} opens a nonmodal editable workspace");
                CloseSettings(); await opening;
            }
            MenuAction(SettingsMenu, new RoutedEventArgs());
            await WaitForAsync(() => SettingsHost.Visibility == Visibility.Visible);
            check(!_dialog && _settingsOpen, "Edit menu Settings opens the same nonmodal workspace");
            CloseSettings();
            var startupModeBefore = _startupMode;
            var aiOptionsBefore = _aiOptions;
            try {
                var savedModes = new List<StartupProcessingMode>();
                var settings = ShowStartupProcessingSettingsAsync(mode => { savedModes.Add(mode); return Task.CompletedTask; });
                await WaitForAsync(() => SettingsHost.Visibility == Visibility.Visible);
                await Task.Delay(100);
                var settingsContent = SettingsHost;
                var picker = AssetDescendants(settingsContent).OfType<ComboBox>().Single(combo => combo.Name == "StartupModePicker");
                var aiModel = AssetDescendants(settingsContent).OfType<ComboBox>().Single(combo => combo.Name == "AiModelPicker");
                var aiLimit = AssetDescendants(settingsContent).OfType<NumberBox>().Single();
                aiLimit.Value = 7; aiModel.SelectedIndex = 2;
                check(_aiOptions.Limit == 7 && _aiOptions.Model == "gpt-5.6-sol" && !_importing,
                    "AI Settings update model and limit without starting work or writing test preferences");
                check(AssetDescendants(settingsContent).OfType<TextBlock>().Any(text => text.Text.Contains("US$") && text.Text.Contains("gpt-5.6-sol")),
                    "AI Settings show a live model-specific cost estimate");
                check(picker.Items.Count == 3, "Startup settings expose exactly Full, File metadata and Off");
                picker.SelectedIndex = 2;
                picker.SelectedIndex = 0;
                check(_startupMode == StartupProcessingMode.Full && !_importing && savedModes.LastOrDefault() == StartupProcessingMode.Full,
                    "Selecting Full saves the mode without starting a paid job immediately");
                check(AssetDescendants(settingsContent).OfType<TextBlock>().Any(text => text.Text.Contains("older pending") && text.Text.Contains("64")),
                    "Full discloses pending-photo scope, API cost and the existing bounded pass");
                await SaveFeatureImageAsync(SettingsHost, Path.Combine(output, "settings-workspace.png"));
                var settingsTabs = AssetDescendants(SettingsHost).OfType<Pivot>().Single();
                settingsTabs.SelectedIndex = 1; await Task.Delay(100);
                var pricing = AssetDescendants(SettingsHost).OfType<StackPanel>().Single(panel => panel.Name == "SettingsPricing");
                check(pricing.ActualHeight > 0 && pricing.Visibility == Visibility.Visible,
                    "AI rates and cost remain visible when another Settings section is selected");
                settingsTabs.SelectedIndex = 0; await Task.Delay(100);
                picker.SelectedIndex = 1;
                check(_startupMode == StartupProcessingMode.FileMetadata, "Metadata-only startup remains selectable");
                picker.SelectedIndex = 2;
                check(_startupMode == StartupProcessingMode.None && !_importing, "Off disables automatic processing without affecting a current job");
                CloseSettings(); await settings;
            } finally {
                CloseSettings(); _startupMode = startupModeBefore; _aiOptions = aiOptionsBefore; UpdateStartupProcessingLabel();
            }
            check(HideScreenshotsCheck.Content?.ToString() == "Hide screenshots" && LibraryAutomationDisabled,
                "Screenshot control is available and diagnostic launches suppress real startup processing");
            var quietFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var quietJob = RunMetadataJobAsync(token => quietFinish.Task.WaitAsync(token), () => Task.CompletedTask, showWindow: false);
            check(_importing && _processingWindow?.IsVisible != true && _metadataProgress is not null,
                "Startup-style processing runs with tracked progress without opening or focusing a window");
            quietFinish.SetResult(); await quietJob;
            check(!_importing && _metadataProgress!.Snapshot.State == "Complete", "Quiet startup-style processing completes through the existing job owner");
            await RunMetadataJobAsync(_ => {
                _metadataProgress!.Report("Preparing explicit enrichment for up to 64 media asset(s)");
                return Task.CompletedTask;
            }, () => Task.CompletedTask, showWindow: false, withEnrichment: true);
            check(_metadataProgress!.Snapshot.EnrichmentEnabled && _metadataProgress.Snapshot.Message.Contains("may still be queued"),
                "Controlled Full jobs keep truthful server-side enrichment status without provider calls");
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = RunMetadataJobAsync(token => finish.Task.WaitAsync(token), () => Task.CompletedTask);
            await WaitForAsync(() => _processingWindow is { IsVisible: true } && _processingWindow.VisualRoot.IsLoaded);
            _metadataProgress!.Source("Generated verification photos", 1, 2);
            _metadataProgress.Report("Reading file metadata · 25/100 · 400 files/s"); PaintProcessingProgress();
            check(_processingWindow?.CurrentPercent == 25 && !_processingWindow.CurrentIndeterminate && _processingWindow.CurrentCountText.Contains("25 / 100"),
                "The processing dialog displays actual current-phase counts and percentage");
            check(!_dialog && _processingWindow!.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { IsResizable: true },
                "Processing uses a resizable modeless window without setting the library modal gate");
            await SaveFeatureImageAsync(_processingWindow!.VisualRoot, Path.Combine(output, "progress-light.png"));
            var model = _metadataProgress;
            var processingWindow = _processingWindow;
            _processingWindow!.Hide(); await WaitForAsync(() => !_processingWindow.IsVisible);
            check(_importing && !job.IsCompleted && _jobCancellation?.IsCancellationRequested == false,
                "Hiding progress does not cancel or detach the running metadata job");
            model.Report("Processing media · 75/100 · 300 files/s");
            themeRoot.RequestedTheme = ElementTheme.Dark;
            ShowActivity(this, new RoutedEventArgs()); await WaitForAsync(() => _processingWindow?.IsVisible == true);
            PaintProcessingProgress();
            check(ReferenceEquals(model, _metadataProgress) && ReferenceEquals(processingWindow, _processingWindow) && _processingWindow?.CurrentPercent == 75,
                "Activity reopens the same running job at its current progress rather than starting another job");
            check(_processingWindow!.Log.Count > 0 && _processingWindow.Operations.Count == 2,
                "The processing window shows source operations and a bounded activity log");
            await SaveFeatureImageAsync(_processingWindow.VisualRoot, Path.Combine(output, "progress-dark.png"));
            _processingWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 660));
            await Task.Delay(250);
            await SaveFeatureImageAsync(_processingWindow.VisualRoot, Path.Combine(output, "progress-small.png"));
            var stop = AssetDescendants(_processingWindow.VisualRoot).OfType<Button>().Single(button => button.Name == "StopButton");
            var operations = AssetDescendants(_processingWindow.VisualRoot).OfType<ListView>().Single(list => list.Name == "OperationsList");
            check(operations.ActualHeight >= 64, "The minimum-height processing window fits complete operation rows");
            var stopPoint = stop.TransformToVisual(_processingWindow.VisualRoot).TransformPoint(new Windows.Foundation.Point());
            check(stopPoint.X >= 0 && stopPoint.X + stop.ActualWidth <= _processingWindow.VisualRoot.ActualWidth &&
                stopPoint.Y + stop.ActualHeight <= _processingWindow.VisualRoot.ActualHeight,
                "Processing controls remain within the smaller resizable window");
            _processingWindow.Close();
            await Task.Delay(100);
            check(_importing && !job.IsCompleted && _jobCancellation?.IsCancellationRequested == false,
                "Closing the processing window never owns or cancels the metadata job");
            ShowActivity(this, new RoutedEventArgs()); await WaitForAsync(() => _processingWindow?.IsVisible == true);
            check(ReferenceEquals(model, _metadataProgress) && _processingWindow!.CurrentPercent == 75,
                "Activity restores the same job after a window-close request");
            CancelProcessing(this, new RoutedEventArgs()); await job;
            check(_metadataProgress.Snapshot.State == "Stopped" && !_importing && ProcessButton.IsEnabled,
                "Stop processing preserves a distinct stopped state and re-enables starting a job");
            _processingWindow?.Hide(); await WaitForAsync(() => _processingWindow?.IsVisible != true);
            await RunMetadataJobAsync(_ => Task.CompletedTask, () => Task.CompletedTask);
            await WaitForAsync(() => _processingWindow?.IsVisible == true);
            check(_metadataProgress!.Snapshot.State == "Complete" && _processingWindow?.CurrentPercent == 100,
                "Successful worker completion and refresh update the visible dialog to complete");
            _processingWindow!.Hide(); await WaitForAsync(() => !_processingWindow.IsVisible);
            await RunMetadataJobAsync(_ => Task.FromException(new InvalidOperationException("Generated test failure")), () => Task.CompletedTask);
            await WaitForAsync(() => _processingWindow?.IsVisible == true);
            check(_metadataProgress!.Snapshot.State == "Failed" && Notice.IsOpen,
                "Failed processing shows needs-attention status rather than false completion");
            _processingWindow!.Hide(); await WaitForAsync(() => !_processingWindow.IsVisible);
            Notice.IsOpen = false;
        } finally {
            _processingWindow?.ClosePermanently(); _processingTimer?.Stop();
            _orderStore = storeBefore; _metadataProgress = progressBefore;
            _sortChoice = choiceBefore; _activeSort = sortBefore;
            themeRoot.RequestedTheme = themeBefore;
            ApplyThumbnailSize(sizesBefore, false); _browseThumbTracking = true;
        }
        async Task WaitForAsync(Func<bool> condition) {
            var timeout = Stopwatch.StartNew();
            while (!condition() && timeout.ElapsedMilliseconds < 6000) await Task.Delay(25, _lifetime.Token);
            if (!condition()) throw new TimeoutException("Native feature verification condition timed out.");
        }
    }
    private static async Task SaveFeatureImageAsync(FrameworkElement element, string path, int settleMilliseconds = 350)
    {
        if (settleMilliseconds > 0) await Task.Delay(settleMilliseconds);
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        using var reader = DataReader.FromBuffer(pixels);
        var bytes = new byte[pixels.Length]; reader.ReadBytes(bytes);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }
}
