using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NektronMoments.Models;
using Windows.System;

namespace NektronMoments.Controls;

/// <summary>Gentle mouse-wheel motion on the native, virtualized GridView scroll owner.</summary>
public sealed class PixelWheelScroller : IDisposable
{
    private readonly ScrollViewer _scroll;
    private readonly UIElement _content;
    private readonly PixelScrollMotion _motion = new();
    private readonly PointerEventHandler _pressed;
    private readonly KeyEventHandler _key;
    private long _lastFrame;
    private bool _rendering, _disposed;
    public ScrollViewer Scroll => _scroll;
    public bool IsAnimating => _rendering;
    public string InputSurface => _content.GetType().Name;

    public PixelWheelScroller(ScrollViewer scroll)
    {
        _scroll = scroll;
        _content = scroll.Content as UIElement ?? throw new InvalidOperationException("Gallery scroll content is not ready.");
        _scroll.VerticalSnapPointsType = SnapPointsType.None;
        // Handle the content's bubbling event BEFORE it reaches ScrollViewer's
        // row/page wheel handling. Never add a second movement after a handled event.
        _content.PointerWheelChanged += WheelChanged;
        _pressed = (_, _) => Stop();
        _key = (_, _) => Stop();
        _scroll.AddHandler(UIElement.PointerPressedEvent, _pressed, true);
        _scroll.AddHandler(UIElement.KeyDownEvent, _key, true);
        _scroll.DirectManipulationStarted += ManipulationStarted;
        _scroll.Unloaded += Unloaded;
    }
    private void WheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_content);
        if (e.Handled || !PixelScrollMotion.HandlesInput(
            e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse,
            point.Properties.IsHorizontalMouseWheel,
            e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control),
            e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift), point.Properties.MouseWheelDelta)) return;
        e.Handled = true;
        QueueWheel(point.Properties.MouseWheelDelta);
    }
    // Shared by the real routed input handler and the no-library release regression.
    public void QueueWheel(int delta)
    {
        if (_disposed) return;
        _motion.Queue(delta, _scroll.VerticalOffset, _scroll.ScrollableHeight);
        if (!_motion.IsActive) { Stop(); return; }
        if (_rendering) return;
        _lastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += Frame;
        _rendering = true;
    }
    private void Frame(object? sender, object args)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _lastFrame) / (double)Stopwatch.Frequency;
        _lastFrame = now;
        var next = _motion.Advance(seconds, _scroll.ScrollableHeight);
        // One bounded frame loop, with no additional native wheel animation or row snapping.
        _scroll.ChangeView(null, next, null, disableAnimation: true);
        if (!_motion.IsActive) Stop();
    }
    public void Stop()
    {
        if (_rendering) CompositionTarget.Rendering -= Frame;
        _rendering = false; _motion.Reset(_scroll.VerticalOffset);
    }
    private void ManipulationStarted(object? sender, object args) => Stop();
    private void Unloaded(object sender, RoutedEventArgs args) => Stop();
    public void Dispose()
    {
        if (_disposed) return;
        Stop(); _disposed = true;
        _content.PointerWheelChanged -= WheelChanged;
        _scroll.RemoveHandler(UIElement.PointerPressedEvent, _pressed);
        _scroll.RemoveHandler(UIElement.KeyDownEvent, _key);
        _scroll.DirectManipulationStarted -= ManipulationStarted;
        _scroll.Unloaded -= Unloaded;
    }
}
