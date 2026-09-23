using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NektronMoments.Models;
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace NektronMoments;

public sealed partial class ProcessingWindow : Window
{
    private sealed record PricingRow(string Name, string Input, string Output, Visibility Selected);
    public BulkObservableCollection<ProcessingOperation> Operations { get; } = [];
    public BulkObservableCollection<ProcessingLogEntry> Log { get; } = [];
    public event Action? StopRequested;
    public event Action? SavedQueuesRequested;
    private MetadataProgress? _model;
    private ProcessingSnapshot? _painted;
    private long _lastSequence, _lastSecond = -1;
    private bool _closing;
    private bool _setupReady;
    private bool _updatingSelection;
    private int _setupSourceCount;
    private TaskCompletionSource<ProcessingSelection?>? _setup;
    private AiProcessingOptions _runOptions = new();
    private (AiProcessingOptions Options, int Sources, bool IncludeAi)? _pricing;
    public bool IsPreparing => _setup is not null;
    public bool IsVisible => AppWindow.IsVisible;
    internal FrameworkElement VisualRoot => Surface;
    internal double CurrentPercent => PhaseBar.Value;
    internal bool CurrentIndeterminate => PhaseBar.IsIndeterminate;
    internal string CurrentCountText => CountText.Text;
    internal bool StopEnabled => StopButton.IsEnabled;
    public ProcessingWindow(Window owner, ElementTheme theme)
    {
        InitializeComponent();
        ModelChoice.ItemsSource = AiProcessingOptions.Models.Select(model => model.Label).ToArray();
        _setupReady = true;
        OperationsList.ItemsSource = Operations; LogList.ItemsSource = Log;
        Surface.RequestedTheme = theme;
        ExtendsContentIntoTitleBar = true; SetTitleBar(ProcessingTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/Window.ico"));
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.PreferredMinimumWidth = 760; presenter.PreferredMinimumHeight = 660;
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min(1000, area.Width); var height = Math.Min(923, area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            Math.Clamp(owner.AppWindow.Position.X + (owner.AppWindow.Size.Width - width) / 2, area.X, area.X + area.Width - width),
            Math.Clamp(owner.AppWindow.Position.Y + (owner.AppWindow.Size.Height - height) / 2, area.Y, area.Y + area.Height - height), width, height));
        AppWindow.Closing += (_, args) => { if (!_closing) { args.Cancel = true; Hide(); } };
    }
    public void Show(MetadataProgress model, bool canStop, ElementTheme theme, AiProcessingOptions? options = null)
    {
        _setup?.TrySetResult(null); _setup = null;
        _runOptions = options ?? new();
        IncludeAiChoice.IsChecked = model.Snapshot.EnrichmentEnabled;
        ModelChoice.SelectedIndex = Array.FindIndex(AiProcessingOptions.Models, item => item.Id == _runOptions.Model);
        LimitChoice.Value = _runOptions.Limit;
        SetupPanel.Visibility = Visibility.Visible;
        LockedNotice.Visibility = Visibility.Visible;
        StartButton.Visibility = Visibility.Collapsed; StopButton.Visibility = Visibility.Visible;
        SetupPanel.IsHitTestVisible = false;
        IncludeAiChoice.IsEnabled = ModelChoice.IsEnabled = LimitChoice.IsEnabled = false;
        IncludeAiChoice.Visibility = ModelChoice.Visibility = LimitChoice.Visibility = Visibility.Collapsed;
        AiReadOnly.Visibility = ModelDisplay.Visibility = LimitDisplay.Visibility = Visibility.Visible;
        AiStateText.Text = model.Snapshot.EnrichmentEnabled ? "Enabled" : "Not included";
        AiStateIcon.Glyph = model.Snapshot.EnrichmentEnabled ? "\uE73E" : "\uE738";
        ModelValue.Text = AiProcessingOptions.Models.Single(item => item.Id == _runOptions.Model).Label;
        LimitValue.Text = _runOptions.Limit.ToString("N0");
        CatchUpButton.IsEnabled = false;
        HideButton.Content = "Hide"; HideButton.Style = StartButton.Style; SavedQueuesButton.IsEnabled = true;
        SetTheme(theme);
        if (!ReferenceEquals(model, _model)) { _model = model; _painted = null; _lastSequence = 0; Log.Clear(); Operations.Clear(); }
        AppWindow.Show(); Activate(); Refresh(canStop);
    }
    public void Hide() { _setup?.TrySetResult(null); _setup = null; AppWindow.Hide(); }
    public void ClosePermanently() { _setup?.TrySetResult(null); _setup = null; _closing = true; Close(); }
    public Task<ProcessingSelection?> ShowSetup(IReadOnlyList<string> sources, AiProcessingOptions options, ElementTheme theme)
    {
        SetTheme(theme); AppWindow.Show(); Activate();
        if (_setup is not null) return Task.FromResult<ProcessingSelection?>(null);
        _setup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _model = null; _painted = null; Operations.Clear(); Log.Clear();
        Operations.ReplaceAll(sources.Select((name, index) => new ProcessingOperation(index + 1, name, "Ready", "Waiting for Start", null, null)));
        _setupSourceCount = sources.Count;
        _setupReady = false;
        IncludeAiChoice.IsChecked = true;
        LimitChoice.Value = options.Limit;
        ModelChoice.SelectedIndex = Array.FindIndex(AiProcessingOptions.Models, item => item.Id == options.Model);
        _setupReady = true;
        SetupPanel.Visibility = Visibility.Visible; SetupPanel.IsHitTestVisible = true;
        LockedNotice.Visibility = Visibility.Collapsed;
        IncludeAiChoice.IsEnabled = ModelChoice.IsEnabled = LimitChoice.IsEnabled = true;
        IncludeAiChoice.Visibility = ModelChoice.Visibility = LimitChoice.Visibility = Visibility.Visible;
        AiReadOnly.Visibility = ModelDisplay.Visibility = LimitDisplay.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Visible; StopButton.Visibility = Visibility.Collapsed;
        SavedQueuesButton.IsEnabled = false; HideButton.Content = "Cancel"; HideButton.Style = null;
        Heading.Text = "Process photos"; SessionText.Text = string.Join(" · ", sources);
        ElapsedText.Text = "Not started"; PhaseText.Text = "Ready to start";
        OverallBar.IsIndeterminate = PhaseBar.IsIndeterminate = false; OverallBar.Value = PhaseBar.Value = 0;
        OverallText.Text = CountText.Text = RateText.Text = RemainingText.Text = "";
        FooterText.Text = "Choose options, then Start · No work runs until Start · Original files stay in place";
        UpdateSetup();
        return _setup.Task;
    }
    private void CatchUpClicked(object sender, RoutedEventArgs e) => LimitChoice.Value = AiProcessingOptions.CatchUpLimit;
    private ProcessingSelection? ReadSetup()
    {
        if (!double.IsFinite(LimitChoice.Value) || LimitChoice.Value != Math.Truncate(LimitChoice.Value) ||
            LimitChoice.Value < 1 || LimitChoice.Value > AiProcessingOptions.MaximumRunLimit || ModelChoice.SelectedIndex < 0) return null;
        return new(IncludeAiChoice.IsChecked == true, new((int)LimitChoice.Value, AiProcessingOptions.Models[ModelChoice.SelectedIndex].Id));
    }
    private void UpdateSetup()
    {
        if (!_setupReady || _setup is null) return;
        var selection = ReadSetup(); StartButton.IsEnabled = selection is not null;
        if (selection is not null) UpdatePricing(selection.Options, _setupSourceCount, selection.IncludeAi);
        else { EstimateAmount.Text = "—"; EstimateCount.Text = "Enter a whole number from 1 to 1,000,000."; _pricing = null; }
        ModelChoice.IsEnabled = LimitChoice.IsEnabled = IncludeAiChoice.IsChecked == true;
        CatchUpButton.IsEnabled = IncludeAiChoice.IsChecked == true;
    }
    private void UpdatePricing(AiProcessingOptions options, int sources, bool includeAi)
    {
        var pricing = (options, sources, includeAi);
        if (_pricing == pricing) return; // Never rebuild the table for each worker log event.
        _pricing = pricing;
        ModeBadge.Text = includeAi ? "Full" : "Metadata";
        var selectedPrice = PricingRows.SelectedIndex;
        PricingRows.ItemsSource = AiProcessingOptions.PricingModels.Select(item => new PricingRow(
            item.Id == "gpt-6-astra" ? item.Label : item.Label.Split(" — ")[0],
            "$" + item.InputRate.ToString("F2", CultureInfo.InvariantCulture),
            "$" + item.OutputRate.ToString("F2", CultureInfo.InvariantCulture),
            includeAi && item.Id == options.Model ? Visibility.Visible : Visibility.Collapsed
        )).ToArray();
        PricingRows.SelectedIndex = selectedPrice;
        var amount = includeAi ? options.Estimate(sources) : 0m;
        EstimateAmount.Text = "$" + amount.ToString(amount is > 0 and < .01m ? "F4" : "N2", CultureInfo.InvariantCulture);
        EstimateCount.Text = includeAi ? $"For up to {options.Limit * (long)sources:N0} photo descriptions" : "File metadata only · No new AI requests";
        EstimateModel.Text = AiProcessingOptions.Models.Single(item => item.Id == options.Model).Label.Split(" — ")[0];
        SetupEstimate.Text = includeAi ? "Actual cost varies with tokens, retries and pending jobs. Flex and cache reuse may lower costs. Excludes address lookup and AWS costs." : "AI is not included in this run. Existing queued jobs are unaffected.";
    }
    private void SurfaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (PricingGrid is null || FooterActions is null) return;
        var narrow = args.NewSize.Width < 850;
        PricingGrid.RowSpacing = narrow ? 14 : 0;
        OptionsGrid.RowSpacing = narrow ? 12 : 0;
        FooterGrid.RowSpacing = narrow ? 10 : 0;
        PricingGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        Grid.SetRow(EstimateCard, narrow ? 1 : 0); Grid.SetColumn(EstimateCard, narrow ? 0 : 1);
        Grid.SetRow(ModelChoice, narrow ? 1 : 0); Grid.SetColumn(ModelChoice, narrow ? 0 : 1);
        Grid.SetColumnSpan(ModelChoice, narrow ? 2 : 1);
        Grid.SetRow(LimitChoice, narrow ? 1 : 0);
        Grid.SetRow(ModelDisplay, narrow ? 1 : 0); Grid.SetColumn(ModelDisplay, narrow ? 0 : 1);
        Grid.SetColumnSpan(ModelDisplay, narrow ? 2 : 1); Grid.SetRow(LimitDisplay, narrow ? 1 : 0);
        Grid.SetColumnSpan(AiChoices, narrow ? 3 : 1);
        Grid.SetRow(FooterActions, narrow ? 1 : 0); Grid.SetColumn(FooterActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(FooterNotice, narrow ? 2 : 1); Grid.SetColumnSpan(FooterActions, narrow ? 2 : 1);
        // Reflow rather than shrinking typography as the window gets smaller.
    }
    private void SetupChanged(object sender, RoutedEventArgs args) => UpdateSetup();
    private void LimitChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateSetup();
    private void StartClick(object sender, RoutedEventArgs args)
    {
        if (_setup is null || ReadSetup() is not { } selection) return;
        var pending = _setup; _setup = null;
        StartButton.IsEnabled = false; SetupPanel.IsHitTestVisible = false;
        Heading.Text = "Starting…";
        pending.TrySetResult(selection);
    }
    public void SetTheme(ElementTheme theme) => Surface.RequestedTheme = theme;
    public void Refresh(bool canStop)
    {
        if (_model is null || !IsVisible) return;
        var state = _model.Snapshot;
        var changed = !ReferenceEquals(state, _painted);
        if (changed) {
            _painted = state;
            UpdatePricing(_runOptions, state.SourceCount, state.EnrichmentEnabled);
            Heading.Text = state.Running ? "Processing photos" : state.Phase;
            SessionText.Text = state.Source.Length > 0 ? state.Source + " · " + state.Phase : state.Message;
            PreserveSelection<ProcessingOperation>(OperationsList, () => Operations.ReplaceAll(state.Operations), item => item.Number);
            OverallText.Text = state.SourceCount > 0 ? $"{state.CompletedSources:N0} of {state.SourceCount:N0} sources complete" : state.State == "Complete" ? "Pass complete" : "Preparing source list";
            OverallBar.IsIndeterminate = state.Running && state.OverallPercent is null;
            OverallBar.Value = state.OverallPercent ?? 0;
            PhaseText.Text = state.Phase;
            CountText.Text = state.Percent is { } percent ? $"{state.Completed:N0} / {state.Total:N0} · {percent:0}%" : state.Running ? "Total not yet known" : state.State;
            if (state.State == "Complete") CountText.Text = "Pass complete";
            PhaseBar.IsIndeterminate = state.Running && state.Percent is null; PhaseBar.Value = state.Percent ?? 0;
            RateText.Text = state.ItemsPerSecond is { } rate ? $"{rate:N0} items/s" : "Throughput: —";
            RemainingText.Text = state.PhaseRemaining is { } remaining ? $"Estimated remaining (phase): {remaining:hh\\:mm\\:ss}" : "Remaining time: —";
            FooterText.Text = state.Running ? "The library remains usable · X or Hide keeps processing in the background" : state.Message;
            var entries = _model.LogAfter(_lastSequence, 500);
            if (entries.Length > 0) {
                var follow = FollowLog.IsChecked == true;
                PreserveSelection<ProcessingLogEntry>(LogList, () => {
                    if (entries.Length > 16) Log.ReplaceAll(Log.Concat(entries).TakeLast(500));
                    else { foreach (var entry in entries) Log.Add(entry); while (Log.Count > 500) Log.RemoveAt(0); }
                }, item => item.Sequence);
                _lastSequence = entries[^1].Sequence;
                LogHeading.Text = Log.Count >= 500 ? "Activity log · latest 500 updates" : "Activity log";
                if (follow) LogList.ScrollIntoView(Log[^1]);
            }
        }
        StopButton.IsEnabled = canStop && state.State == "Running";
        var seconds = (long)state.Elapsed.TotalSeconds;
        if (seconds != _lastSecond || changed) { _lastSecond = seconds; ElapsedText.Text = $"Elapsed {state.Elapsed:hh\\:mm\\:ss}"; }
    }
    private void HideClick(object sender, RoutedEventArgs args) => Hide();
    private void CopySelectedRows(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var focused = FocusManager.GetFocusedElement(Surface.XamlRoot) as DependencyObject;
        while (focused is not null && focused is not ListView) focused = VisualTreeHelper.GetParent(focused);
        if (focused is not ListView table || (table != PricingRows && table != EstimateRows && table != OperationsList && table != LogList)) return;
        var lines = table.Items.Cast<object>().Where(table.SelectedItems.Contains).Select(FormatRow).Where(text => text.Length > 0).ToArray();
        if (lines.Length == 0) return;
        var data = new DataPackage(); data.SetText(string.Join(Environment.NewLine, lines));
        try { Clipboard.SetContent(data); }
        catch (Exception) { FooterText.Text = "The clipboard is busy. Please try copying again."; }
        args.Handled = true;
    }
    internal static string FormatRow(object row) => row switch {
        ProcessingOperation item => string.Join('\t', item.NumberText, item.Name, item.State, item.Phase, item.CountText),
        ProcessingLogEntry item => string.Join('\t', item.TimeText, item.Stage, item.Message),
        PricingRow item => string.Join('\t', item.Name, item.Input, item.Output),
        ListViewItem { Content: Grid grid } => string.Join('\t', grid.Children.OfType<TextBlock>().Select(item => item.Text)),
        _ => ""
    };
    private void PreserveSelection<T>(ListView table, Action update, Func<T, long> key)
    {
        var selected = table.SelectedItems.OfType<T>().Select(key).ToHashSet();
        _updatingSelection = true;
        try {
            update();
            if (selected.Count > 0)
                foreach (var row in table.Items.OfType<T>())
                    if (selected.Contains(key(row)) && !table.SelectedItems.Contains(row)) table.SelectedItems.Add(row);
        } finally { _updatingSelection = false; }
    }
    private void LogSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // Selecting a row is an inspection gesture; don't scroll it away while
        // the user reads. Follow latest can be re-enabled explicitly.
        if (!_updatingSelection && args.AddedItems.Count > 0) FollowLog.IsChecked = false;
    }
    private void HideKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Hide(); args.Handled = true; }
    private void StopClick(object sender, RoutedEventArgs args) => StopRequested?.Invoke();
    private void SavedQueuesClick(object sender, RoutedEventArgs args) { Hide(); SavedQueuesRequested?.Invoke(); }
}
