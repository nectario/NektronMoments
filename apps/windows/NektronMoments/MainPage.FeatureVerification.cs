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
            var dragErrors = new List<string>();
            await MeasureAnimatedThumbMotionAsync(dragErrors);
            check(dragErrors.Count == 0, "Animated thumb destinations traverse intermediate photo positions and settle: " + string.Join("; ", dragErrors));
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
                var setupBody = AssetDescendants(setupWindow.VisualRoot).OfType<ScrollViewer>().Single(control => control.Name == "ProcessingBody");
                check(setupBody.ScrollableHeight <= 1, "Compact processing setup shows every section without outer scrolling");
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
                // Visibility precedes Pivot template materialization. Wait for
                // the controls under test, not an arbitrary 100-ms delay.
                await WaitForAsync(() => AssetDescendants(SettingsHost).OfType<ComboBox>().Any(combo => combo.Name == "StartupModePicker") &&
                    AssetDescendants(SettingsHost).OfType<ComboBox>().Any(combo => combo.Name == "AiModelPicker") &&
                    AssetDescendants(SettingsHost).OfType<NumberBox>().Any());
                var settingsContent = SettingsHost;
                var picker = AssetDescendants(settingsContent).OfType<ComboBox>().Single(combo => combo.Name == "StartupModePicker");
                var aiModel = AssetDescendants(settingsContent).OfType<ComboBox>().Single(combo => combo.Name == "AiModelPicker");
                var aiLimit = AssetDescendants(settingsContent).OfType<NumberBox>().Single();
                aiLimit.Value = 7; aiModel.SelectedIndex = 2;
                check(_aiOptions.Limit == 7 && _aiOptions.Model == "gpt-5.6-sol" && !_importing,
                    "AI Settings update model and limit without starting work or writing test preferences");
                check(AssetDescendants(settingsContent).OfType<TextBlock>().Any(text => text.Text.Contains("US$") && text.Text.Contains("gpt-5.6-sol")),
                    "AI Settings show a live model-specific cost estimate");
                var priceTable = AssetDescendants(settingsContent).OfType<Grid>().Single(grid => grid.Name == "SettingsPricingTable");
                check(priceTable.ColumnDefinitions.Count == 3 && priceTable.RowDefinitions.Count == AiProcessingOptions.PricingModels.Length + 1,
                    "AI pricing has aligned Model, Input and Output columns with one row per model");
                check(priceTable.Children.OfType<TextBlock>().Any(text => text.Text == "$0.20"),
                    "USD rates retain cents and clear decimal precision");
                check(picker.Items.Count == 3, "Startup settings expose exactly Full, File metadata and Off");
                picker.SelectedIndex = 2;
                picker.SelectedIndex = 0;
                check(_startupMode == StartupProcessingMode.Full && !_importing && savedModes.LastOrDefault() == StartupProcessingMode.Full,
                    "Selecting Full saves the mode without starting a paid job immediately");
                check(AssetDescendants(settingsContent).OfType<TextBlock>().Any(text => text.Text.Contains("older pending") && text.Text.Contains("64")),
                    "Full discloses pending-photo scope, API cost and the existing bounded pass");
                await SaveFeatureImageAsync(SettingsHost, Path.Combine(output, "settings-workspace.png"));
                var pricingTheme = themeRoot.RequestedTheme;
                var settingsWidth = SettingsHost.Width;
                try {
                    themeRoot.RequestedTheme = ElementTheme.Light;
                    await SaveFeatureImageAsync(SettingsHost, Path.Combine(output, "pricing-light.png"));
                    themeRoot.RequestedTheme = ElementTheme.Dark;
                    await SaveFeatureImageAsync(SettingsHost, Path.Combine(output, "pricing-dark.png"));
                    SettingsHost.Width = 650;
                    await Task.Delay(200);
                    var runEstimate = AssetDescendants(SettingsHost).OfType<TextBlock>().Single(text => text.Name == "SettingsRunEstimate");
                    var runPanel = (StackPanel)runEstimate.Parent;
                    check(Grid.GetRow(runPanel) == 1 && Grid.GetColumn(runPanel) == 0,
                        "Narrow pricing stacks the estimate beneath the rate table");
                    await SaveFeatureImageAsync(SettingsHost, Path.Combine(output, "pricing-narrow.png"));
                } finally { themeRoot.RequestedTheme = pricingTheme; SettingsHost.Width = settingsWidth; }
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
            _processingWindow?.Hide();
            var quietJob = RunMetadataJobAsync(token => quietFinish.Task.WaitAsync(token), () => Task.CompletedTask, showProgress: false);
            check(_importing && _metadataProgress is not null && Busy.Visibility == Visibility.Collapsed && _processingWindow?.IsVisible != true,
                "Startup-style processing stays in the background without opening progress or the top busy stripe");
            SetBusy(true);
            check(Busy.Visibility == Visibility.Collapsed && ProcessButton.IsEnabled && ProcessButton.Label == "View progress",
                "Processing keeps the stripe hidden during refresh and offers View progress in the ribbon");
            var startupProgress = _metadataProgress; var startupWindow = _processingWindow;
            await ProcessPhotosAsync();
            check(ReferenceEquals(startupProgress, _metadataProgress) && ReferenceEquals(startupWindow, _processingWindow) && _processingWindow?.IsVisible == true && !quietJob.IsCompleted,
                "The processing button reopens the same startup job without starting new work");
            quietFinish.SetResult(); await quietJob;
            check(!_importing && _metadataProgress!.Snapshot.State == "Complete" && ProcessButton.Label == "Process metadata", "Startup-style processing completes and restores the normal command label");
            await RunMetadataJobAsync(_ => {
                _metadataProgress!.Report("Preparing explicit enrichment for up to 64 media asset(s)");
                return Task.CompletedTask;
            }, () => Task.CompletedTask, withEnrichment: true);
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
            // Screenshot-reference fixture: no provider calls, source scans or paid work.
            var referenceWindow = new ProcessingWindow(App.MainWindowInstance!, ElementTheme.Light);
            try {
                var referenceProgress = new MetadataProgress();
                referenceProgress.ConfigureSources(["My Photos"], true); referenceProgress.Source("My Photos", 1, 1);
                referenceProgress.Report("Scanning /mnt/d/Pictures");
                referenceProgress.Report("Discovering media with 32 directory workers");
                referenceProgress.Report("Discovering media · 9,792 files found");
                referenceWindow.Show(referenceProgress, true, ElementTheme.Light, new AiProcessingOptions(10000));
                referenceWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 923));
                await Task.Delay(300);
                await SaveFeatureImageAsync(referenceWindow.VisualRoot, Path.Combine(output, "processing-reference-light.png"));
                var compactBody = AssetDescendants(referenceWindow.VisualRoot).OfType<ScrollViewer>().Single(control => control.Name == "ProcessingBody");
                check(compactBody.ScrollableHeight <= 1, "The 20-percent-smaller processing window shows every section without outer scrolling");
                var expandingLog = AssetDescendants(referenceWindow.VisualRoot).OfType<ListView>().Single(control => control.Name == "LogList");
                var compactLogHeight = expandingLog.ActualHeight;
                var compactViewportHeight = compactBody.ViewportHeight;
                referenceWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 1123));
                await Task.Delay(300);
                var extraHeight = compactBody.ViewportHeight - compactViewportHeight;
                await File.WriteAllTextAsync(Path.Combine(output, "processing-log-layout.json"), System.Text.Json.JsonSerializer.Serialize(new {
                    compactLogHeight, expandedLogHeight = expandingLog.ActualHeight, extraHeight,
                    viewportHeight = compactBody.ViewportHeight, outerScrollableHeight = compactBody.ScrollableHeight,
                }));
                check(extraHeight > 20 && expandingLog.ActualHeight >= compactLogHeight + extraHeight - 2 && compactBody.ScrollableHeight <= 1,
                    "The activity log absorbs extra window height without hiding progress or creating outer scrolling");
                await SaveFeatureImageAsync(referenceWindow.VisualRoot, Path.Combine(output, "processing-expanded-log.png"));
                referenceWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 923));
                await Task.Delay(300);
                check(Math.Abs(expandingLog.ActualHeight - compactLogHeight) <= 2,
                    "The activity log shrinks back with the processing window");
                check(AssetDescendants(referenceWindow.VisualRoot).OfType<TextBlock>().Single(item => item.Name == "Heading").FontSize == 32,
                    "Compact processing retains the original heading font size");
                var estimate = AssetDescendants(referenceWindow.VisualRoot).OfType<TextBlock>().Single(item => item.Name == "EstimateAmount");
                check(estimate.Text == "$63.20", "Processing cost card uses the selected model and 10,000-photo allowance");
                var modelChoice = AssetDescendants(referenceWindow.VisualRoot).OfType<ComboBox>().Single(item => item.Name == "ModelChoice");
                check(!modelChoice.IsEnabled, "Running processing options remain locked");
                var priceRows = AssetDescendants(referenceWindow.VisualRoot).OfType<ListView>().Single(item => item.Name == "PricingRows");
                check(priceRows.Items.Count == 4 && ProcessingWindow.FormatRow(priceRows.Items[3]).Contains("$50.00"),
                    "Astra pricing is included without changing execution choices");
                foreach (var name in new[] { "ElapsedText", "AiStateText", "ModelValue", "LimitValue" })
                    check(AssetDescendants(referenceWindow.VisualRoot).OfType<TextBlock>().Single(item => item.Name == name).FontSize == (name == "ElapsedText" ? 13 : 14),
                        name + " uses the requested text size");
                var aiStatusIcon = AssetDescendants(referenceWindow.VisualRoot).OfType<FontIcon>().Single(item => item.Name == "AiStateIcon");
                check(aiStatusIcon.Glyph == "\uE73E" && aiStatusIcon.FontSize == 18,
                    "Locked AI state uses a small borderless checkmark rather than a clickable-looking checkbox");
                check(AssetDescendants(referenceWindow.VisualRoot).OfType<TextBlock>().Single(item => item.Name == "ModeBadge").FontSize == 16,
                    "Full badge uses the requested smaller text");
                var sourceRows = AssetDescendants(referenceWindow.VisualRoot).OfType<ListView>().Single(item => item.Name == "OperationsList");
                var logRows = AssetDescendants(referenceWindow.VisualRoot).OfType<ListView>().Single(item => item.Name == "LogList");
                var costRows = AssetDescendants(referenceWindow.VisualRoot).OfType<ListView>().Single(item => item.Name == "EstimateRows");
                foreach (var table in new[] { priceRows, sourceRows, logRows, costRows }) {
                    var index = table == priceRows ? 1 : 0;
                    var peer = new ListViewItemDataAutomationPeer(table.Items[index], new ListViewAutomationPeer(table));
                    if (peer.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider selection)
                        throw new InvalidOperationException(table.Name + " does not expose the native selection pattern");
                    selection.Select();
                    check(table.SelectedIndex == index, table.Name + " supports native row selection");
                }
                check(modelChoice.SelectedIndex == 0 && estimate.Text == "$6.32", "Luna row updates the estimate without changing the running Terra model");
                var astraPeer = new ListViewItemDataAutomationPeer(priceRows.Items[3], new ListViewAutomationPeer(priceRows));
                ((ISelectionItemProvider)astraPeer.GetPattern(PatternInterface.SelectionItem)).Select();
                check(estimate.Text == "$306.00" && modelChoice.SelectedIndex == 0,
                    "Astra pricing-only comparison updates the beige card without enabling Astra execution");
                check(AssetDescendants(referenceWindow.VisualRoot).OfType<Grid>().Single(item => item.Name == "ProcessingContent").Padding == new Thickness(15),
                    "Processing content has 15-pixel padding around the body and actions");
                var selectedSequence = ((ProcessingLogEntry)logRows.SelectedItem).Sequence;
                for (var update = 0; update < 20; update++) referenceProgress.Report("Discovering media · verification update " + update);
                referenceWindow.Refresh(true);
                check(estimate.Text == "$306.00", "Live progress does not overwrite the selected model comparison");
                check(sourceRows.SelectedIndex == 0 && ((ProcessingLogEntry)logRows.SelectedItem).Sequence == selectedSequence,
                    "Source and activity selections survive live collection replacement");
                check(!AssetDescendants(referenceWindow.VisualRoot).OfType<CheckBox>().Single(item => item.Name == "FollowLog").IsChecked.GetValueOrDefault(),
                    "Selecting a log entry pauses Follow latest for inspection");
                check(ProcessingWindow.FormatRow(logRows.SelectedItem).Contains(((ProcessingLogEntry)logRows.SelectedItem).Message),
                    "Selected-row copy includes the full untruncated log message");
                check(estimate.FontSize == 36 && AssetDescendants(referenceWindow.VisualRoot).OfType<TextBlock>().Single(item => item.Name == "SessionText").FontSize == 15,
                    "Marked-region typography is smaller and the price is two steps smaller");
                await Task.Delay(100);
                await SaveFeatureImageAsync(referenceWindow.VisualRoot, Path.Combine(output, "processing-selected-light.png"));
                referenceWindow.SetTheme(ElementTheme.Dark);
                await Task.Delay(200);
                await SaveFeatureImageAsync(referenceWindow.VisualRoot, Path.Combine(output, "processing-reference-dark.png"));
                referenceWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 660));
                await Task.Delay(200);
                await SaveFeatureImageAsync(referenceWindow.VisualRoot, Path.Combine(output, "processing-reference-small.png"));
            } finally { referenceWindow.ClosePermanently(); }
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
