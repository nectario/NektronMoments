using Microsoft.UI.Xaml;
using NektronMoments.Models;

namespace NektronMoments;

public sealed partial class MainPage
{
    private MetadataProgress? _metadataProgress;
    private ProcessingWindow? _processingWindow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _processingTimer;
    private ProcessingSnapshot? _lastProgressPaint;
    private AiProcessingOptions _jobAiOptions = new();
    private void StartProcessingPresentation(IReadOnlyList<string>? sourceNames = null, bool withEnrichment = false, bool showProgress = true, bool retryFailedPhotos = false)
    {
        _metadataProgress = new(); _metadataProgress.ConfigureSources(sourceNames ?? [], withEnrichment, retryFailedPhotos); _lastProgressPaint = null;
        _processingTimer ??= DispatcherQueue.CreateTimer(); _processingTimer.Interval = TimeSpan.FromMilliseconds(150);
        _processingTimer.Tick -= ProcessingTick; _processingTimer.Tick += ProcessingTick; _processingTimer.Start();
        if (showProgress) _ = ShowProcessingProgressAsync();
    }
    private void ProcessingTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => PaintProcessingProgress();
    private void PaintProcessingProgress()
    {
        if (_metadataProgress is null || _lifetime.IsCancellationRequested) return;
        var state = _metadataProgress.Snapshot;
        if (!ReferenceEquals(state, _lastProgressPaint)) { _lastProgressPaint = state; StatusText.Text = state.Message; }
        _processingWindow?.Refresh(_jobCancellation is not null);
        if (!state.Running) _processingTimer?.Stop();
    }
    private Task ShowProcessingProgressAsync()
    {
        if (_metadataProgress is null || _lifetime.IsCancellationRequested || App.MainWindowInstance is null) return Task.CompletedTask;
        EnsureProcessingWindow();
        _processingWindow!.VisualRoot.IsHitTestVisible = IsHitTestVisible;
        _processingWindow.Show(_metadataProgress, _jobCancellation is not null, ActualTheme, _jobAiOptions);
        return Task.CompletedTask;
    }
    private void EnsureProcessingWindow()
    {
        if (_processingWindow is null && App.MainWindowInstance is not null) {
            var window = new ProcessingWindow(App.MainWindowInstance, ActualTheme); _processingWindow = window;
            window.StopRequested += () => { CancelProcessing(this, new RoutedEventArgs()); PaintProcessingProgress(); };
            window.SavedQueuesRequested += async () => { App.MainWindowInstance.Activate(); await ShowSavedActivityAsync(); };
            window.Closed += (_, _) => { if (ReferenceEquals(_processingWindow, window)) _processingWindow = null; };
        }
    }
}
