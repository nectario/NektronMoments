using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using NektronMoments.Controls;
using NektronMoments.Models;

namespace NektronMoments;

public sealed partial class MainPage
{
    private bool _isThumbnailSizing, _thumbnailLayoutCommit, _sliderEventsAttached, _sizingDirty;
    private double _sizingPending, _sizingAnchorY;
    private int _sizingAnchorIndex, _sizingGeneration;
    private long _lastSizingFrame, _thumbnailLayoutChanges, _liveReflowUpdates;
    private double _lastThumbnailCommitMs;
    private readonly HashSet<int> _liveColumns = [];
    private TaskCompletionSource? _sizingFinished;
    private ContainerVisual? _sizingRoot;
    private readonly List<SizingTile> _sizingTiles = [];
    private readonly List<IDisposable> _sizingOwned = [];
    private Services.CompositionRetirementQueue? _sizingRetirement;
    private sealed record SizingTile(int Index, ContainerVisual Root, SpriteVisual Back, SpriteVisual Image,
        SpriteVisual Caption, CompositionRoundedRectangleGeometry Shape, CompositionVisualSurface ImageSource,
        CompositionVisualSurface CaptionSource);

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
        if (_isThumbnailSizing || _reorderActive || _orderCommitActive || Gallery.ItemsPanelRoot is not ItemsWrapGrid wrap || Viewer.IsOpen) return;
        _reorderMotion?.Stop();
        _isThumbnailSizing = true; ++_sizingGeneration;
        _sizingFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _sizingPending = _thumbnailSize; _liveColumns.Clear(); _liveReflowUpdates = 0;
        _sizingAnchorIndex = Math.Max(0, wrap.FirstVisibleIndex);
        _sizingAnchorY = (Gallery.ContainerFromIndex(_sizingAnchorIndex) as FrameworkElement)?
            .TransformToVisual(Gallery).TransformPoint(new Windows.Foundation.Point()).Y ?? 0;
        _pixelScroll?.Stop(); _thumbnailPrefetch?.Cancel(); _sizeChange?.Cancel();
        MediaThumbnail.SetResizePreview(true);
        _sizingRetirement ??= new(DispatcherQueue);
        try {
        T Own<T>(T value) where T : IDisposable { _sizingOwned.Add(value); return value; }
        var compositor = ElementCompositionPreview.GetElementVisual(wrap).Compositor;
        _sizingRoot = Own(compositor.CreateContainerVisual());
        _sizingRoot.Size = new Vector2((float)Gallery.ActualWidth, (float)Gallery.ActualHeight);
        _sizingRoot.Clip = Own(compositor.CreateInsetClip());
        var maxColumns = GetGalleryColumns(112);
        var capacity = maxColumns * ((int)Math.Ceiling(Gallery.ActualHeight / (112 * .72 + 54)) + 2);
        var first = Math.Max(0, _sizingAnchorIndex - maxColumns);
        var last = Math.Min(Gallery.Items.Count, _sizingAnchorIndex + capacity);
        var background = ((SolidColorBrush)DetailsSplit.PaneBackground).Color;
        var movement = Own(compositor.CreateVector3KeyFrameAnimation()); movement.Target = "Offset";
        // All visible cards retarget together on the compositor; never delay by row.
        movement.InsertExpressionKeyFrame(1, "this.FinalValue"); movement.Duration = TimeSpan.FromMilliseconds(180);
        var sizeAnimation = Own(compositor.CreateVector2KeyFrameAnimation()); sizeAnimation.Target = "Size";
        sizeAnimation.InsertExpressionKeyFrame(1, "this.FinalValue"); sizeAnimation.Duration = TimeSpan.FromMilliseconds(80);
        var motion = Own(compositor.CreateImplicitAnimationCollection()); motion["Offset"] = movement; motion["Size"] = sizeAnimation;
        for (var index = first; index < last; index++) {
            if (Gallery.ContainerFromIndex(index) is not GridViewItem item || item.ActualWidth <= 0) continue;
            var tile = Own(compositor.CreateContainerVisual());
            var shape = Own(compositor.CreateRoundedRectangleGeometry()); shape.CornerRadius = new Vector2(10);
            tile.Clip = Own(compositor.CreateGeometricClip(shape));
            var back = Own(compositor.CreateSpriteVisual()); back.Brush = Own(compositor.CreateColorBrush(background));
            var imageSource = Own(compositor.CreateVisualSurface());
            imageSource.SourceVisual = ElementCompositionPreview.GetElementVisual(item);
            imageSource.SourceSize = new Vector2((float)item.ActualWidth, (float)Math.Max(1, item.ActualHeight - 54));
            var imageBrush = Own(compositor.CreateSurfaceBrush(imageSource)); imageBrush.Stretch = CompositionStretch.UniformToFill;
            var image = Own(compositor.CreateSpriteVisual()); image.Brush = imageBrush;
            var captionSource = Own(compositor.CreateVisualSurface());
            captionSource.SourceVisual = imageSource.SourceVisual;
            captionSource.SourceOffset = new Vector2(0, (float)Math.Max(0, item.ActualHeight - 54));
            captionSource.SourceSize = new Vector2((float)item.ActualWidth, 54);
            var captionBrush = Own(compositor.CreateSurfaceBrush(captionSource));
            captionBrush.Stretch = CompositionStretch.None; captionBrush.HorizontalAlignmentRatio = 0; captionBrush.VerticalAlignmentRatio = 0;
            var caption = Own(compositor.CreateSpriteVisual()); caption.Brush = captionBrush;
            tile.Children.InsertAtTop(back); tile.Children.InsertAtTop(image); tile.Children.InsertAtTop(caption);
            _sizingRoot.Children.InsertAtTop(tile);
            _sizingTiles.Add(new(index, tile, back, image, caption, shape, imageSource, captionSource));
        }
        ElementCompositionPreview.SetElementChildVisual(SizingCanvas, _sizingRoot);
        SizingCanvas.Visibility = Visibility.Visible;
        _sizingDirty = true; ApplySizingFrame(true);
        foreach (var tile in _sizingTiles) { tile.Root.ImplicitAnimations = motion; tile.Image.ImplicitAnimations = motion; tile.Caption.ImplicitAnimations = motion; }
        CompositionTarget.Rendering += SizingFrame;
        } catch (Exception error) {
            CompositionTarget.Rendering -= SizingFrame;
            _isThumbnailSizing = false;
            ReleaseSizingVisuals();
            _sizingFinished?.TrySetResult(); _sizingFinished = null;
            MediaThumbnail.SetResizePreview(false);
            Services.DiagnosticLog.Write(error);
        }
    }
    private void PreviewThumbnailSizing(double value)
    {
        if (!_isThumbnailSizing) return;
        _sizingPending = ViewingPolicy.ThumbnailWidth(value); _sizingDirty = true;
        _settingSize = true; SizePreset.SelectedIndex = 4; _settingSize = false;
    }
    private void SizingFrame(object? sender, object args) => ApplySizingFrame(false);
    private void ApplySizingFrame(bool force)
    {
        if (!_isThumbnailSizing || !_sizingDirty) return;
        var now = Stopwatch.GetTimestamp();
        if (!force && (now - _lastSizingFrame) * 1000d / Stopwatch.Frequency < 8) return;
        _lastSizingFrame = now; _sizingDirty = false;
        var width = Math.Clamp(_sizingPending, 100, Math.Max(100, Gallery.ActualWidth - 24));
        var height = width * .72 + 54;
        var columns = GetGalleryColumns(width);
        _liveColumns.Add(columns); ++_liveReflowUpdates;
        var anchorRow = _sizingAnchorIndex / columns;
        foreach (var tile in _sizingTiles) {
            var w = (float)Math.Max(1, width - 12); var h = (float)Math.Max(55, height - 12);
            tile.Root.Offset = new Vector3((float)((tile.Index % columns) * width), (float)((tile.Index / columns - anchorRow) * height + _sizingAnchorY), 0);
            tile.Root.Size = tile.Back.Size = tile.Shape.Size = new Vector2(w, h);
            tile.Image.Size = new Vector2(w, h - 54);
            tile.Caption.Offset = new Vector3(0, h - 54, 0); tile.Caption.Size = new Vector2(w, 54);
        }
    }
    private void CommitThumbnailSizing()
    {
        if (!_isThumbnailSizing) return;
        var timer = Stopwatch.StartNew();
        CompositionTarget.Rendering -= SizingFrame;
        _isThumbnailSizing = false; _thumbnailLayoutCommit = true;
        try {
            if (_lifetime.IsCancellationRequested) return;
            ApplyThumbnailSize(_sizingPending, true); Gallery.UpdateLayout();
            if (Gallery.ItemsPanelRoot is ItemsWrapGrid wrap && _pixelScroll is { } scroller) {
                var columns = GetGalleryColumns(wrap.ItemWidth);
                var offset = Math.Floor(_sizingAnchorIndex / (double)columns) * wrap.ItemHeight - _sizingAnchorY;
                scroller.JumpTo(Math.Clamp(offset, 0, scroller.Scroll.ScrollableHeight));
            }
        } finally {
            ReleaseSizingVisuals();
            _thumbnailLayoutCommit = false;
            _sizingFinished?.TrySetResult(); _sizingFinished = null;
            MediaThumbnail.SetResizePreview(false);
            _lastThumbnailCommitMs = timer.Elapsed.TotalMilliseconds;
            _ = RestoreThumbnailLookaheadAsync(_sizingGeneration);
        }
    }
    private void ReleaseSizingVisuals()
    {
            SizingCanvas.Visibility = Visibility.Collapsed;
            ElementCompositionPreview.SetElementChildVisual(SizingCanvas, null);
            _sizingRoot?.Children.RemoveAll();
            foreach (var tile in _sizingTiles) {
                tile.Root.ImplicitAnimations = tile.Image.ImplicitAnimations = tile.Caption.ImplicitAnimations = null;
                tile.Root.StopAnimation("Offset"); tile.Root.StopAnimation("Size");
                tile.Image.StopAnimation("Size"); tile.Caption.StopAnimation("Size"); tile.Caption.StopAnimation("Offset");
                tile.Root.Children.RemoveAll(); tile.Root.Clip = null;
                tile.Back.Brush = tile.Image.Brush = tile.Caption.Brush = null;
                tile.ImageSource.SourceVisual = tile.CaptionSource.SourceVisual = null;
            }
            _sizingRoot = null;
            _sizingRetirement!.Enqueue(_sizingOwned.AsEnumerable().Reverse()); _sizingOwned.Clear();
            _sizingTiles.Clear();
    }
    private async Task RestoreThumbnailLookaheadAsync(int generation)
    {
        try {
            await Task.Delay(350, _lifetime.Token);
            if (!_isThumbnailSizing && generation == _sizingGeneration) { ResizeGallery(); ScheduleThumbnailWarm(); }
        } catch (OperationCanceledException) { }
    }
}
