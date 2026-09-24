using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NektronMoments.Models;

namespace NektronMoments.Controls;

/// <summary>Mouse-only animation adapter on the native thumb's painted child.
/// The real ScrollViewer still owns range, virtualization, keyboard, track clicks,
/// accessibility and touch. Pointer positions become latest animated destinations,
/// rather than a second immediate scroll followed by an animation.</summary>
public sealed class AnimatedThumbDrag : IDisposable
{
    private readonly FrameworkElement _surface;
    private readonly ScrollBar _bar;
    private readonly Thumb _thumb;
    private readonly PixelWheelScroller _scroller;
    private readonly Action<bool> _tracking;
    private Pointer? _pointer;
    private double _origin, _offset, _maximum, _travel;
    public bool IsDragging => _pointer is not null;

    public AnimatedThumbDrag(FrameworkElement surface, ScrollBar bar, Thumb thumb,
        PixelWheelScroller scroller, Action<bool> tracking)
    {
        _surface = surface; _bar = bar; _thumb = thumb; _scroller = scroller; _tracking = tracking;
        surface.PointerPressed += Pressed;
        surface.PointerMoved += Moved;
        surface.PointerReleased += Released;
        surface.PointerCanceled += Canceled;
        surface.PointerCaptureLost += Canceled;
        surface.Unloaded += Unloaded;
    }
    private void Pressed(object sender, PointerRoutedEventArgs args)
    {
        if (IsDragging || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
            !args.GetCurrentPoint(_bar).Properties.IsLeftButtonPressed) return;
        // The two paging regions are exactly the available thumb travel.
        // The root also contains arrow buttons; do not count those in the track.
        if (VisualTreeHelper.GetParent(_thumb) is not Panel track) return;
        _travel = track.Children.OfType<FrameworkElement>()
            .Where(part => part.Name is "VerticalLargeDecrease" or "VerticalLargeIncrease")
            .Sum(part => part.ActualHeight);
        _maximum = _scroller.Scroll.ScrollableHeight;
        if (_travel <= 0 || _maximum <= 0 || !_surface.CapturePointer(args.Pointer)) return;
        _pointer = args.Pointer;
        _origin = args.GetCurrentPoint(_bar).Position.Y;
        _offset = _scroller.Scroll.VerticalOffset;
        _scroller.Stop();
        _scroller.ScrollbarInputActive = true;
        _tracking(true);
        args.Handled = true; // Prevent Thumb's immediate native drag from also running.
    }
    private void Moved(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer?.PointerId != args.Pointer.PointerId) return;
        Seek(args.GetCurrentPoint(_bar).Position.Y);
        args.Handled = true;
    }
    private void Seek(double y) => _scroller.SeekFromScrollbar(ThumbDragTarget.Resolve(_offset, _origin, y, _maximum, _travel));
    private void Released(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer?.PointerId != args.Pointer.PointerId) return;
        Seek(args.GetCurrentPoint(_bar).Position.Y);
        End(false); args.Handled = true;
    }
    private void Canceled(object sender, PointerRoutedEventArgs args) { if (_pointer?.PointerId == args.Pointer.PointerId) End(true); }
    private void Unloaded(object sender, RoutedEventArgs args) => Cancel();
    public void Cancel() => End(true);
    private void End(bool cancel)
    {
        if (_pointer is not { } pointer) return;
        _pointer = null;
        _scroller.ScrollbarInputActive = false;
        if (cancel) _scroller.Stop();
        _surface.ReleasePointerCapture(pointer);
        _tracking(false);
    }
    public void Dispose()
    {
        Cancel();
        _surface.PointerPressed -= Pressed; _surface.PointerMoved -= Moved;
        _surface.PointerReleased -= Released; _surface.PointerCanceled -= Canceled;
        _surface.PointerCaptureLost -= Canceled; _surface.Unloaded -= Unloaded;
    }
}
