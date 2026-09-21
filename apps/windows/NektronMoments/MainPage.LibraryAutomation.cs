using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NektronMoments.Models;
using NektronMoments.Services;

namespace NektronMoments;

public sealed partial class MainPage
{
    private bool _hideScreenshots, _startupProcessingRequested, _screenshotIndexReady;
    private Task? _screenshotIndexTask;
    private StartupProcessingMode _startupMode;
    private AiProcessingOptions _aiOptions = AiProcessingOptions.Resolve(UserPreferences.Text("aiProcessing"));
    private void UpdateStartupProcessingLabel() => StartupProcessingMenu.Text = "On startup: " + StartupProcessing.Label(_startupMode) + "…";

    private static bool LibraryAutomationDisabled =>
        Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_DISABLE_STARTUP_PROCESSING") == "1" ||
        Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(name =>
            name.StartsWith("NEKTRON_MOMENTS_", StringComparison.Ordinal) &&
            (name.EndsWith("_CHECK_DIR", StringComparison.Ordinal) || name == "NEKTRON_MOMENTS_DIAGNOSTIC_DIR") &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));

    private async void ScreenshotFilterChanged(object sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        _hideScreenshots = HideScreenshotsCheck.IsChecked == true;
        UpdateArrangementChrome();
        try { await UserPreferences.SetAsync("hideScreenshots", _hideScreenshots); }
        catch (Exception) { StatusText.Text = "Filter changed for this session; its preference could not be saved."; }
        await LoadCatalogAsync();
        StartScreenshotLookup();
    }
    private async void StartupProcessingChanged(object sender, RoutedEventArgs args)
        => await ShowStartupProcessingSettingsAsync();
    private async Task ShowStartupProcessingSettingsAsync(Func<StartupProcessingMode, Task>? saveForVerification = null,
        Func<AiProcessingOptions, Task>? saveAiForVerification = null)
    {
        var modes = new[] { StartupProcessingMode.Full, StartupProcessingMode.FileMetadata, StartupProcessingMode.None };
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = modes.Select(StartupProcessing.Label).ToArray(), SelectedIndex = Array.IndexOf(modes, _startupMode) };
        picker.Name = "StartupModePicker";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Processing on startup");
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void UpdateNote() => note.Text = modes[Math.Max(0, picker.SelectedIndex)] switch {
            StartupProcessingMode.Full => "Read file metadata, then request AI scene descriptions and address lookup for new photos and older pending photos. API charges apply. Uses the limit and model below (server ceiling: 64), subject to service quotas. Completed results are reused; this does not reprocess the entire library.",
            StartupProcessingMode.None => "Open your library without automatically processing photos. You can still process metadata manually.",
            _ => "Read dates, GPS, dimensions and file hashes, then sync metadata. No paid AI enrichment is requested.",
        };
        UpdateNote();
        picker.SelectionChanged += async (_, _) => {
            if (picker.SelectedIndex < 0) return;
            var selected = modes[picker.SelectedIndex]; UpdateNote();
            try {
                if (saveForVerification is null) await UserPreferences.SetAsync("startupProcessingMode", selected.ToString());
                else await saveForVerification(selected);
                _startupMode = selected; UpdateStartupProcessingLabel();
                StatusText.Text = "Startup setting saved · Applies on the next launch";
            } catch (Exception) { StatusText.Text = "The startup preference could not be saved."; }
        };
        var content = new StackPanel { Spacing = 14, Width = 420 };
        content.Children.Add(new TextBlock { Text = "When Nektron Moments opens", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(picker); content.Children.Add(note);
        var limit = new NumberBox { Name = "AiAssetLimit", Header = "AI assets per source per run", Minimum = 1, Maximum = 64,
            SmallChange = 1, LargeChange = 8, Value = _aiOptions.Limit, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var model = new ComboBox { Name = "AiModelPicker", Header = "Scene description model", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = AiProcessingOptions.Models.Select(item => item.Label).ToArray(),
            SelectedIndex = Array.FindIndex(AiProcessingOptions.Models, item => item.Id == _aiOptions.Model) };
        var cost = new TextBlock { Text = _aiOptions.Preview(), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        async Task SaveAiAsync()
        {
            if (!double.IsFinite(limit.Value) || limit.Value != Math.Truncate(limit.Value) || limit.Value < 1 || limit.Value > 64 || model.SelectedIndex < 0) {
                cost.Text = "Enter a whole number from 1 to 64. The previous setting is still saved."; return;
            }
            var selected = new AiProcessingOptions((int)limit.Value, AiProcessingOptions.Models[model.SelectedIndex].Id);
            try {
                if (saveAiForVerification is not null) await saveAiForVerification(selected);
                else if (saveForVerification is null) await UserPreferences.SetAsync("aiProcessing", JsonSerializer.Serialize(selected));
                _aiOptions = selected; cost.Text = selected.Preview();
            } catch (Exception) { cost.Text = "AI settings could not be saved. Please try again."; }
        }
        limit.ValueChanged += async (_, _) => await SaveAiAsync();
        model.SelectionChanged += async (_, _) => await SaveAiAsync();
        content.Children.Add(limit); content.Children.Add(model); content.Children.Add(cost);
        content.Children.Add(new HyperlinkButton { Content = "OpenAI pricing", NavigateUri = new Uri("https://developers.openai.com/api/docs/pricing") });
        content.Children.Add(new TextBlock { Text = "Saved automatically. Changing this setting does not start or alter the current processing job.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        await ShowDialogAsync("Settings", new ScrollViewer { Content = content, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
    private void StartLibraryAutomation()
    {
        if (LibraryAutomationDisabled || _lifetime.IsCancellationRequested || _overview is null) return;
        StartScreenshotLookup();
        if (_startupProcessingRequested || _startupMode == StartupProcessingMode.None) return;
        _startupProcessingRequested = true;
        var mode = _startupMode;
        var aiOptions = _aiOptions;
        // Let the initial gallery render; the CLI/scan runs on its own worker process.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () => {
            if (_lifetime.IsCancellationRequested || _importing || _overview is null || _startupMode != mode) return;
            var sources = _overview.Sources.ToArray();
            if (sources.Length == 0) return;
            await RunMetadataJobAsync(async token => {
                for (var index = 0; index < sources.Length; index++) {
                    var source = sources[index]; _metadataProgress!.Source(source.Name, index + 1, sources.Length);
                    PaintProcessingProgress();
                    await RunSyncAsync(source.Id, false, token, withEnrichment: mode == StartupProcessingMode.Full, aiOptions: aiOptions);
                    _metadataProgress.CompleteSource();
                }
            }, sourceNames: sources.Select(source => source.Name).ToArray(), showWindow: false,
                withEnrichment: mode == StartupProcessingMode.Full);
        });
    }
    private void StartScreenshotLookup()
    {
        if (!_hideScreenshots || _screenshotIndexReady || _screenshotIndexTask is { IsCompleted: false } || LibraryAutomationDisabled) return;
        _screenshotIndexTask = RefreshScreenshotIndexAsync();
    }
    private async Task RefreshScreenshotIndexAsync()
    {
        // A separate read-only API connection cannot hold up local catalog requests.
        using var indexBridge = new LibraryBridge(indexOnly: true);
        try {
            if (!_importing) StatusText.Text = "Checking existing descriptions for screenshots…";
            var matches = new HashSet<string>();
            var queryCount = 1;
            for (var query = 0; query < queryCount; query++) {
                string? cursor = null;
                var seen = new HashSet<string>();
                for (var page = 0; ; page++) {
                    if (page >= 1000) throw new InvalidOperationException("Screenshot index page limit exceeded.");
                    var result = await indexBridge.CallAsync<JsonElement>(new { command = "screenshot-page", queryIndex = query, cursor }, _lifetime.Token);
                    queryCount = result.GetProperty("queryCount").GetInt32();
                    var hashes = result.GetProperty("hashes").EnumerateArray().Select(hash => hash.GetString()!).ToArray();
                    matches.UnionWith(hashes);
                    if (matches.Count > 100000) throw new InvalidOperationException("Screenshot index capacity exceeded.");
                    cursor = result.GetProperty("nextCursor").GetString();
                    if (string.IsNullOrEmpty(cursor)) break;
                    if (!seen.Add(cursor)) throw new InvalidOperationException("Repeated screenshot index cursor.");
                }
            }
            await _bridge.CallAsync<JsonElement>(new { command = "screenshot-index-replace", hashes = matches.ToArray() }, _lifetime.Token);
            _screenshotIndexReady = true;
            ++_catalogCacheEpoch; _catalogSnapshots.Clear();
            if (_hideScreenshots) await LoadCatalogAsync();
            if (!_importing) StatusText.Text = "Screenshot filter updated · Photos without descriptions remain visible";
        } catch (OperationCanceledException) { }
        catch (Exception) {
            if (!_lifetime.IsCancellationRequested && _hideScreenshots) {
                Notice.Title = "Using cached screenshot matches";
                Notice.Message = "Existing indexed descriptions could not be refreshed. Unclassified photos remain visible; toggle the filter to retry.";
                Notice.IsOpen = true;
            }
        }
    }
}
