using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using NektronMoments.Controls;
using NektronMoments.Models;

namespace NektronMoments;

public sealed partial class MainPage
{
    private bool _isThumbnailSizing, _thumbnailLayoutCommit, _sliderEventsAttached;
    private double _sizingStart, _sizingPending, _sizingAnchorY;
    private int _sizingAnchorIndex, _sizingGeneration;
    private Visual? _sizingVisual;
    private TaskCompletionSource? _sizingFinished;
    private long _thumbnailLayoutChanges;
    private double _lastThumbnailCommitMs;

    private void ThumbnailSliderLoaded(object sender, RoutedEventArgs e)
    {
        if (_sliderEventsAttached) return;
        ThumbnailSlider.ApplyTemplate();
        var thumbs = AssetDescendants(ThumbnailSlider).OfType<Thumb>().ToArray();
        _sliderEventsAttached = thumbs.Length > 0;
        foreach (var thumb in thumbs) {
            thumb.DragStarted += (_, _) => BeginThumbnailSizing();
            thumb.DragCompleted += (_, _) => CommitThumbnailSizing();
        }
        ThumbnailSlider.PointerCaptureLost += (_, _) => CommitThumbnailSizing();
        ThumbnailSlider.Unloaded += (_, _) => CommitThumbnailSizing();
    }
    private void BeginThumbnailSizing()
    {
        if (_isThumbnailSizing || Gallery.ItemsPanelRoot is not ItemsWrapGrid wrap || Viewer.IsOpen) return;
        _isThumbnailSizing = true; ++_sizingGeneration;
        _sizingFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _sizingStart = _sizingPending = _thumbnailSize;
        _sizingAnchorIndex = Math.Max(0, wrap.FirstVisibleIndex);
        _sizingAnchorY = (Gallery.ContainerFromIndex(_sizingAnchorIndex) as FrameworkElement)?
            .TransformToVisual(Gallery).TransformPoint(new Windows.Foundation.Point()).Y ?? 0;
        _pixelScroll?.Stop(); _thumbnailPrefetch?.Cancel(); _sizeChange?.Cancel();
        MediaThumbnail.SetResizePreview(true);
        _sizingVisual = ElementCompositionPreview.GetElementVisual(wrap);
        _sizingVisual.CenterPoint = new Vector3(0, (float)(_pixelScroll?.Scroll.VerticalOffset ?? 0), 0);
    }
    private void PreviewThumbnailSizing(double value)
    {
        if (!_isThumbnailSizing || _sizingVisual is null) return;
        _sizingPending = ViewingPolicy.ThumbnailWidth(value);
        var scale = (float)(_sizingPending / _sizingStart);
        // A compositor transform only: no collection mutation, remeasure, file I/O,
        // new decode, or preference write for individual slider movements.
        _sizingVisual.Scale = new Vector3(scale, scale, 1);
        _settingSize = true; SizePreset.SelectedIndex = 4; _settingSize = false;
    }
    private void CommitThumbnailSizing()
    {
        if (!_isThumbnailSizing) return;
        var timer = Stopwatch.StartNew();
        _isThumbnailSizing = false;
        if (_sizingVisual is not null) { _sizingVisual.Scale = Vector3.One; _sizingVisual.CenterPoint = Vector3.Zero; }
        _sizingVisual = null;
        _thumbnailLayoutCommit = true;
        try {
            if (_lifetime.IsCancellationRequested) return;
            ApplyThumbnailSize(_sizingPending, true);
            Gallery.UpdateLayout();
            if (Gallery.ItemsPanelRoot is ItemsWrapGrid wrap && _pixelScroll is { } scroller) {
                var columns = Math.Max(1, (int)((Gallery.ActualWidth - Gallery.Padding.Left - Gallery.Padding.Right - 8) / wrap.ItemWidth));
                var offset = Math.Floor(_sizingAnchorIndex / (double)columns) * wrap.ItemHeight - _sizingAnchorY;
                scroller.Scroll.ChangeView(null, Math.Clamp(offset, 0, scroller.Scroll.ScrollableHeight), null, true);
            }
        } finally {
            _thumbnailLayoutCommit = false;
            _sizingFinished?.TrySetResult(); _sizingFinished = null;
            MediaThumbnail.SetResizePreview(false);
            _lastThumbnailCommitMs = timer.Elapsed.TotalMilliseconds;
            _ = RestoreThumbnailLookaheadAsync(_sizingGeneration);
        }
    }
    private async Task RestoreThumbnailLookaheadAsync(int generation)
    {
        try {
            await Task.Delay(350, _lifetime.Token);
            if (!_isThumbnailSizing && generation == _sizingGeneration) {
                ResizeGallery(); ScheduleThumbnailWarm(); _ = PrimeBufferAsync();
            }
        } catch (OperationCanceledException) { }
    }
}
