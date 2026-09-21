using System.Diagnostics;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private readonly NativeWheelTarget _wheel = new();
    private readonly ScrollbarGlide _glide = new();
    private enum InputMode { None, Wheel, Scrollbar, Navigation, Stop }
    private InputMode _inputMode;
    private bool _usingGlide;
    private readonly PointerEventHandler _pressed;
    private readonly KeyEventHandler _key;
    private long _lastFrame;
    private bool _rendering, _disposed;
    // Native animation keeps scroll interpolation off the busy XAML layout loop.
    // The former UI-thread follower remains available for controlled diagnostics.
    private readonly bool _nativeScrollbarAnimationEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_SCROLL_DRIVER"), "follower", StringComparison.OrdinalIgnoreCase);
    private readonly DispatcherQueueTimer? _nativeDeadline;
    private bool _nativeActive, _nativeQueued, _nativeSubmitting, _softwareFallbackActive;
    private bool _nativeViewIsIntermediate;
    private bool _nativeImmediate;
    private bool _waitingForNativeCancellation;
    private int _nativeEpoch;
    private double _nativeTarget;
    public ScrollViewer Scroll => _scroll;
    public bool IsAnimating => _rendering || _nativeActive;
    public bool IsWheelMotion => IsAnimating && _inputMode == InputMode.Wheel;
    public double Target => _nativeActive ? _nativeTarget : _usingGlide ? _glide.Target : _motion.Target;
    public bool NativeScrollbarAnimationEnabled => _nativeScrollbarAnimationEnabled;
    public bool NativeWheelAnimationEnabled => _nativeScrollbarAnimationEnabled;
    public string ActiveInputMode => IsAnimating ? _inputMode.ToString() : "None";
    public int NativeScrollSubmissions { get; private set; }
    public int NativeScrollRejected { get; private set; }
    public int NativeScrollFallbacks { get; private set; }
    public int NativeWheelSubmissions { get; private set; }
    public int NativeWheelRejected { get; private set; }
    public int NativeWheelFallbacks { get; private set; }
    public int NativeImmediateSubmissions { get; private set; }
    public int NativeCancellationAttempts { get; private set; }
    public int NativeCancellationsHandled { get; private set; }
    public int NativeCancellationCompletions { get; private set; }
    public bool WaitingForNativeCancellation => _waitingForNativeCancellation;
    public int SoftwareWheelFrames { get; private set; }
    public double? LastNativeFallbackTarget { get; private set; }
    public double? LastNativeFallbackOffset { get; private set; }
    public bool? LastNativeFallbackIntermediate { get; private set; }
    public event Action? Settled;
    public string InputSurface => _content.GetType().Name;
    public double WheelDistance {
        get => _wheel.WheelDistance;
        set => _motion.WheelDistance = _wheel.WheelDistance = Math.Clamp(double.IsFinite(value) ? value : PixelScrollMotion.PixelsPerNotch, 12, 144);
    }
    public double ScrollbarGlideMs { get; set; } = 120;

    public PixelWheelScroller(ScrollViewer scroll)
    {
        _scroll = scroll;
        _content = scroll.Content as UIElement ?? throw new InvalidOperationException("Gallery scroll content is not ready.");
        _scroll.VerticalSnapPointsType = SnapPointsType.None;
        // Handle the content's bubbling event BEFORE it reaches ScrollViewer's
        // row/page wheel handling. Never add a second movement after a handled event.
        _content.PointerWheelChanged += WheelChanged;
        _pressed = (_, args) => {
            // The built-in bar owns its drag, paging and capture. A queued Stop
            // would otherwise overwrite the native target later in this turn.
            for (var node = args.OriginalSource as DependencyObject; node is not null && node != _scroll;
                node = VisualTreeHelper.GetParent(node)) {
                if (node is ScrollBar) { YieldToNativeInput(); return; }
            }
            Stop();
        };
        _key = (_, args) => {
            // These keys hand scrolling to the native control; a queued freeze
            // must not run after its own keyboard navigation request.
            if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
                StopCore(raiseSettled: true);
            else Stop();
        };
        _scroll.AddHandler(UIElement.PointerPressedEvent, _pressed, true);
        _scroll.AddHandler(UIElement.KeyDownEvent, _key, true);
        _scroll.DirectManipulationStarted += ManipulationStarted;
        _scroll.Unloaded += Unloaded;
        if (_nativeScrollbarAnimationEnabled) {
            _scroll.ViewChanged += NativeViewChanged;
            _nativeDeadline = _scroll.DispatcherQueue.CreateTimer();
            _nativeDeadline.IsRepeating = false;
            _nativeDeadline.Tick += NativeDeadline;
        }
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
        if (_disposed || delta == 0) return;
        if (_nativeScrollbarAnimationEnabled) {
            // Switching input ownership or reversing cancels the previous native
            // destination. Same-direction input must never restart at a stale
            // VerticalOffset or discard the accumulated fast-wheel distance.
            if (_inputMode != InputMode.Wheel || _wheel.Reverses(delta)) StopCore(raiseSettled: false);
            _wheel.Queue(delta, _scroll.VerticalOffset, _scroll.ScrollableHeight, _scroll.ViewportHeight);
            if (!_wheel.IsActive) { if (IsAnimating) Stop(); return; }
            if (_softwareFallbackActive) SeekSoftware(_wheel.Target, 120, InputMode.Wheel);
            else SeekNative(_wheel.Target, InputMode.Wheel);
            return;
        }
        // Diagnostic comparison only: the shipping path above never attaches a
        // CompositionTarget wheel frame loop or imposes the old 720 DIP/s limit.
        if (_usingGlide || _nativeActive) Stop();
        _inputMode = InputMode.Wheel;
        _motion.Queue(delta, _scroll.VerticalOffset, _scroll.ScrollableHeight);
        if (!_motion.IsActive) { Stop(); return; }
        if (_rendering) return;
        _lastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += Frame;
        _rendering = true;
    }
    public void SeekFromScrollbar(double target)
    {
        if (_disposed) return;
        if (_inputMode == InputMode.Wheel) StopCore(raiseSettled: false);
        if (_nativeScrollbarAnimationEnabled && ScrollbarGlideMs > 0 && !_softwareFallbackActive) { SeekNative(target, InputMode.Scrollbar); return; }
        SeekSoftware(target, _softwareFallbackActive && ScrollbarGlideMs > 0 ? 120 : ScrollbarGlideMs, InputMode.Scrollbar);
    }
    private void SeekSoftware(double target, double milliseconds, InputMode mode)
    {
        if (_nativeActive || !_usingGlide || _inputMode != mode)
            StopCore(raiseSettled: false, preserveWheelTarget: mode == InputMode.Wheel);
        _inputMode = mode;
        _usingGlide = true;
        _glide.Retarget(_scroll.VerticalOffset, target, _scroll.ScrollableHeight, milliseconds);
        if (!_glide.IsActive) { _scroll.ChangeView(null, _glide.Position, null, true); Stop(); return; }
        if (_rendering) return;
        _lastFrame = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += Frame; _rendering = true;
    }
    /// <summary>Replace pending wheel/drag/freeze requests with one immediate
    /// navigation destination. Do not pair Stop with a raw ScrollViewer.ChangeView:
    /// competing immediate requests in the same XAML turn can retain the old view.</summary>
    public void JumpTo(double target)
    {
        if (_disposed || !double.IsFinite(target)) return;
        StopCore(raiseSettled: false);
        CancelNativeManipulation();
        if (_nativeScrollbarAnimationEnabled) SeekNative(target, InputMode.Navigation, immediate: true);
        else {
            target = Math.Clamp(target, 0, _scroll.ScrollableHeight);
            _scroll.ChangeView(null, target, null, disableAnimation: true);
            _motion.Reset(target); _wheel.Reset(target);
            Settled?.Invoke();
        }
    }
    private void SeekNative(double target, InputMode mode, bool immediate = false)
    {
        if (!double.IsFinite(target)) return;
        target = Math.Clamp(target, 0, _scroll.ScrollableHeight);
        if (_nativeActive && _inputMode == mode && _nativeImmediate == immediate && Math.Abs(target - _nativeTarget) < .01) return;
        if (!_nativeActive || _inputMode != mode) {
            StopCore(raiseSettled: false, preserveWheelTarget: mode == InputMode.Wheel);
            _nativeActive = true;
            _inputMode = mode;
        }
        _nativeTarget = target;
        _nativeImmediate = immediate;
        // This is an inactivity watchdog, not a rendering timer. Native animation
        // owns all intermediate frames; new pointer input only changes its destination.
        _nativeDeadline!.Stop();
        _nativeDeadline.Interval = TimeSpan.FromMilliseconds(1500);
        _nativeDeadline.Start();
        if (_waitingForNativeCancellation) return;
        QueueNativeSubmission();
    }
    private void QueueNativeSubmission()
    {
        if (_disposed || !_nativeActive || _waitingForNativeCancellation) return;
        if (_nativeQueued) return;
        _nativeQueued = true;
        var epoch = _nativeEpoch;
        if (!_scroll.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => SubmitNative(epoch)))
            StopCore(raiseSettled: true);
    }
    private void SubmitNative(int epoch)
    {
        if (_disposed || !_nativeActive || epoch != _nativeEpoch) return;
        _nativeQueued = false;
        _nativeTarget = Math.Clamp(_nativeTarget, 0, _scroll.ScrollableHeight);
        _nativeSubmitting = true;
        try {
            NativeScrollSubmissions++;
            if (_inputMode == InputMode.Wheel) NativeWheelSubmissions++;
            if (_nativeImmediate) NativeImmediateSubmissions++;
            // false means the control did not accept a new view. It is NOT a
            // completion signal: an older native animation can still be running.
            if (!_scroll.ChangeView(null, _nativeTarget, null, disableAnimation: _nativeImmediate)) {
                NativeScrollRejected++;
                if (_inputMode == InputMode.Wheel) NativeWheelRejected++;
            }
        } finally { _nativeSubmitting = false; }
        FinishNativeIfAtTarget();
    }
    private void NativeViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        // CancelDirectManipulations disables the DM viewport synchronously, but
        // its final virtualized offset/layout flush is asynchronous. Submitting a
        // new immediate transform before that flush adds the remaining old delta
        // to the new destination. The final ViewChanged is the native completion
        // signal; only then submit our one latest replacement request.
        if (_waitingForNativeCancellation) {
            if (!args.IsIntermediate) {
                _waitingForNativeCancellation = false;
                NativeCancellationCompletions++;
                QueueNativeSubmission();
            }
            return;
        }
        if (!_nativeActive) return;
        _nativeViewIsIntermediate = args.IsIntermediate;
        // ScrollViewer does not provide request IDs. An old completion is safe to
        // accept only if it actually reached the most recent destination.
        if (!args.IsIntermediate && !_nativeSubmitting) FinishNativeIfAtTarget();
    }
    private bool FinishNativeIfAtTarget()
    {
        if (!_nativeActive || _nativeQueued || _waitingForNativeCancellation || !NativeScrollPrecision.IsAtTarget(
            _scroll.VerticalOffset, _nativeTarget, _nativeViewIsIntermediate)) return false;
        _nativeActive = false;
        _nativeDeadline?.Stop();
        _motion.Reset(_scroll.VerticalOffset);
        if (_inputMode == InputMode.Wheel) _wheel.Complete(_scroll.VerticalOffset);
        Settled?.Invoke();
        return true;
    }
    private void NativeDeadline(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || !_nativeActive || FinishNativeIfAtTarget()) return;
        // Recover from a rejected/stalled native request without jumping to its
        // destination. Freeze the current view, then finish with the bounded software
        // follower. Further targets use that follower until this gesture settles.
        // This is counted so release/performance verification can reject degradation.
        var target = Math.Clamp(_nativeTarget, 0, _scroll.ScrollableHeight);
        var mode = _inputMode;
        LastNativeFallbackTarget = _nativeTarget;
        LastNativeFallbackOffset = _scroll.VerticalOffset;
        LastNativeFallbackIntermediate = _nativeViewIsIntermediate;
        NativeScrollFallbacks++;
        if (mode == InputMode.Wheel) NativeWheelFallbacks++;
        StopCore(raiseSettled: false, preserveWheelTarget: mode == InputMode.Wheel);
        CancelNativeManipulation();
        SeekSoftware(target, 120, mode);
        _softwareFallbackActive = IsAnimating;
    }
    private void Frame(object? sender, object args)
    {
        if (_inputMode == InputMode.Wheel) SoftwareWheelFrames++;
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _lastFrame) / (double)Stopwatch.Frequency;
        _lastFrame = now;
        var next = _usingGlide ? _glide.Advance(seconds, _scroll.ScrollableHeight) : _motion.Advance(seconds, _scroll.ScrollableHeight);
        // One bounded frame loop, with no additional native wheel animation or row snapping.
        _scroll.ChangeView(null, next, null, disableAnimation: true);
        if (_usingGlide ? !_glide.IsActive : !_motion.IsActive) Stop();
    }
    public void Stop()
    {
        if (_disposed) return;
        var wasNative = _nativeActive;
        var wasAnimating = IsAnimating;
        var actual = _scroll.VerticalOffset;
        StopCore(raiseSettled: false);
        CancelNativeManipulation();
        // Freeze goes through the same epoch/coalescing channel as a subsequent
        // JumpTo, wheel reversal or drag. Latest input wins before native submission;
        // there is never an old synchronous freeze competing with the new destination.
        if (wasNative && !_disposed && _scroll.IsLoaded)
            SeekNative(actual, InputMode.Stop, immediate: true);
        else if (wasAnimating) Settled?.Invoke();
    }
    public void YieldToNativeInput()
    {
        if (_disposed) return;
        // Do not ChangeView, cancel capture, or enqueue a freeze. Windows takes
        // over the viewport directly, just as in the original 0.1.1 GridView.
        _waitingForNativeCancellation = false;
        StopCore(raiseSettled: true);
    }
    private void CancelNativeManipulation()
    {
        if (_disposed || !_scroll.IsLoaded || _waitingForNativeCancellation) return;
        // ChangeView updates the transform but is not a physical inertia cancel.
        // CancelDirectManipulations walks ancestors, so call it on the content,
        // not on the ScrollViewer. WinUI then disables the active DM viewport.
        NativeCancellationAttempts++;
        // Set the flag before entering WinUI because completion may also be
        // synchronous. A second Stop/JumpTo must retain this pending native flush.
        _waitingForNativeCancellation = _nativeScrollbarAnimationEnabled;
        if (_content.CancelDirectManipulations()) NativeCancellationsHandled++;
        else _waitingForNativeCancellation = false;
    }
    private void StopCore(bool raiseSettled, bool preserveWheelTarget = false)
    {
        var wasAnimating = IsAnimating;
        var actual = _scroll.VerticalOffset;
        ++_nativeEpoch;
        _nativeActive = _nativeQueued = _nativeSubmitting = _softwareFallbackActive = false;
        _nativeViewIsIntermediate = _nativeImmediate = false;
        _nativeDeadline?.Stop();
        if (_rendering) CompositionTarget.Rendering -= Frame;
        _rendering = false; _motion.Reset(actual); _glide.Stop(); _usingGlide = false;
        if (!preserveWheelTarget) _wheel.Reset(actual);
        _inputMode = InputMode.None;
        if (wasAnimating && raiseSettled) Settled?.Invoke();
    }
    // Touch/pen owns the native viewport now; invalidate our queued work without
    // placing a later freeze request over the user's direct manipulation.
    private void ManipulationStarted(object? sender, object args) => StopCore(raiseSettled: true);
    private void Unloaded(object sender, RoutedEventArgs args)
    {
        _waitingForNativeCancellation = false;
        StopCore(raiseSettled: false);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _waitingForNativeCancellation = false;
        StopCore(raiseSettled: false); _disposed = true;
        _content.PointerWheelChanged -= WheelChanged;
        _scroll.RemoveHandler(UIElement.PointerPressedEvent, _pressed);
        _scroll.RemoveHandler(UIElement.KeyDownEvent, _key);
        _scroll.DirectManipulationStarted -= ManipulationStarted;
        _scroll.Unloaded -= Unloaded;
        if (_nativeScrollbarAnimationEnabled) _scroll.ViewChanged -= NativeViewChanged;
        if (_nativeDeadline is not null) _nativeDeadline.Tick -= NativeDeadline;
    }
}
