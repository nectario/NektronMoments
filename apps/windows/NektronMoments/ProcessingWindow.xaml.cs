using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NektronMoments.Models;

namespace NektronMoments;

public sealed partial class ProcessingWindow : Window
{
    public BulkObservableCollection<ProcessingOperation> Operations { get; } = [];
    public BulkObservableCollection<ProcessingLogEntry> Log { get; } = [];
    public event Action? StopRequested;
    public event Action? SavedQueuesRequested;
    private MetadataProgress? _model;
    private ProcessingSnapshot? _painted;
    private long _lastSequence, _lastSecond = -1;
    private bool _closing;
    public bool IsVisible => AppWindow.IsVisible;
    internal FrameworkElement VisualRoot => Surface;
    internal double CurrentPercent => PhaseBar.Value;
    internal bool CurrentIndeterminate => PhaseBar.IsIndeterminate;
    internal string CurrentCountText => CountText.Text;
    internal bool StopEnabled => StopButton.IsEnabled;
    public ProcessingWindow(Window owner, ElementTheme theme)
    {
        InitializeComponent();
        OperationsList.ItemsSource = Operations; LogList.ItemsSource = Log;
        Surface.RequestedTheme = theme;
        ExtendsContentIntoTitleBar = true; SetTitleBar(ProcessingTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/Window.ico"));
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.PreferredMinimumWidth = 760; presenter.PreferredMinimumHeight = 660;
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min(1080, area.Width); var height = Math.Min(760, area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            Math.Clamp(owner.AppWindow.Position.X + (owner.AppWindow.Size.Width - width) / 2, area.X, area.X + area.Width - width),
            Math.Clamp(owner.AppWindow.Position.Y + (owner.AppWindow.Size.Height - height) / 2, area.Y, area.Y + area.Height - height), width, height));
        AppWindow.Closing += (_, args) => { if (!_closing) { args.Cancel = true; Hide(); } };
    }
    public void Show(MetadataProgress model, bool canStop, ElementTheme theme)
    {
        SetTheme(theme);
        if (!ReferenceEquals(model, _model)) { _model = model; _painted = null; _lastSequence = 0; Log.Clear(); Operations.Clear(); }
        AppWindow.Show(); Activate(); Refresh(canStop);
    }
    public void Hide() => AppWindow.Hide();
    public void ClosePermanently() { _closing = true; Close(); }
    public void SetTheme(ElementTheme theme) => Surface.RequestedTheme = theme;
    public void Refresh(bool canStop)
    {
        if (_model is null || !IsVisible) return;
        var state = _model.Snapshot;
        var changed = !ReferenceEquals(state, _painted);
        if (changed) {
            _painted = state;
            Heading.Text = state.Running ? state.EnrichmentEnabled ? "Processing photos — Full" : "Processing metadata" : state.Phase;
            SessionText.Text = state.Source.Length > 0 ? state.Source + " · " + state.Phase : state.Message;
            Operations.ReplaceAll(state.Operations);
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
                if (entries.Length > 16) Log.ReplaceAll(Log.Concat(entries).TakeLast(500));
                else { foreach (var entry in entries) Log.Add(entry); while (Log.Count > 500) Log.RemoveAt(0); }
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
    private void HideKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Hide(); args.Handled = true; }
    private void StopClick(object sender, RoutedEventArgs args) => StopRequested?.Invoke();
    private void SavedQueuesClick(object sender, RoutedEventArgs args) { Hide(); SavedQueuesRequested?.Invoke(); }
}
