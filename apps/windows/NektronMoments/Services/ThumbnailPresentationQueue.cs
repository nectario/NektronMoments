using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;

namespace NektronMoments.Services;

/// <summary>Foreground-first UI publication with bounded, input-yielding idle display warming.</summary>
public sealed class ThumbnailPresentationQueue
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Queue<Request> _pending = new();
    private readonly Queue<Request> _warmPending = new();
    private readonly Func<bool> _backgroundPaused;
    public int WarmPending => _warmPending.Count;
    private int _active;
    private bool _closed;
    private bool _interactionActive, _frameQueued;
    public bool InteractionActive {
        get => _interactionActive;
        set {
            if (_interactionActive == value) return;
            _interactionActive = value; _timer.Stop(); CancelFrame(); Schedule();
        }
    }
    private void CancelFrame() { if (_frameQueued) { CompositionTarget.Rendering -= OnFrame; _frameQueued = false; } }
    private void OnFrame(object? sender, object args) { CancelFrame(); Drain(); }
    public long Started { get; private set; }
    public long Skipped { get; private set; }
    public double MaximumBatchMs { get; private set; }
    public int Pending => _pending.Count + _active;

    public ThumbnailPresentationQueue(DispatcherQueue dispatcher, Func<bool>? backgroundPaused = null)
    {
        _backgroundPaused = backgroundPaused ?? (() => false);
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Interval = TimeSpan.FromMilliseconds(8);
        _timer.Tick += (_, _) => Drain();
    }

    public Task<SoftwareBitmapSource?> Enqueue(PreparedThumbnail bitmap, Func<bool> stillVisible, CancellationToken token, bool background = false)
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Presentation requests must originate on the UI thread.");
        token.ThrowIfCancellationRequested();
        if (_closed) return Task.FromCanceled<SoftwareBitmapSource?>(new CancellationToken(true));
        if (bitmap.Source is { } source) return Task.FromResult<SoftwareBitmapSource?>(source);
        if (background && _warmPending.Count >= 1024) return Task.FromResult<SoftwareBitmapSource?>(null);
        var request = new Request(bitmap, stillVisible, token);
        if (background) {
            _warmPending.Enqueue(request);
        } else _pending.Enqueue(request);
        Schedule();
        return request.Completion.Task.WaitAsync(token);
    }

    private void Schedule()
    {
        if (_closed || (_pending.Count == 0 && _warmPending.Count == 0) || _active >= 4) return;
        if (InteractionActive && _pending.Count > 0) {
            if (!_frameQueued) { _frameQueued = true; CompositionTarget.Rendering += OnFrame; }
        } else if (!_timer.IsRunning) {
            _timer.Interval = TimeSpan.FromMilliseconds(_pending.Count == 0 && (InteractionActive || _backgroundPaused()) ? 100 : 8);
            _timer.Start();
        }
    }
    private void Drain()
    {
        var trace = DiagnosticTrace.Start(4);
        var watch = Stopwatch.StartNew();
        var examined = 0; var started = 0;
        var warming = _pending.Count == 0;
        var maximum = InteractionActive || warming ? 1 : 4;
        var queue = warming ? _warmPending : _pending;
        // Limit starts per frame, not the lifetime of asynchronous GPU uploads.
        // Serializing the whole upload kept ready images waiting across frames.
        while (!_closed && (!warming || (!InteractionActive && !_backgroundPaused())) && _active < 4 && queue.Count > 0 && examined++ < 32 && started < maximum && watch.Elapsed.TotalMilliseconds < (InteractionActive || warming ? .75 : 2)) {
            var request = queue.Dequeue();
            if (request.Token.IsCancellationRequested || !request.StillVisible()) {
                ++Skipped; request.Completion.TrySetCanceled(new CancellationToken(true)); continue;
            }
            if (request.Bitmap.Source is { } ready) { request.Completion.TrySetResult(ready); continue; }
            ++started; ++Started; ++_active;
            _ = RunAsync(request);
        }
        MaximumBatchMs = Math.Max(MaximumBatchMs, watch.Elapsed.TotalMilliseconds);
        Schedule();
        DiagnosticTrace.Stop(trace, 4);
    }
    private async Task RunAsync(Request request)
    {
        var trace = DiagnosticTrace.Start(3);
        try { request.Completion.TrySetResult(await request.Bitmap.PresentAsync()); }
        catch (Exception error) {
            request.Completion.TrySetException(error);
            _ = request.Completion.Task.Exception; // An already-recycled tile may have canceled its wait.
        } finally { DiagnosticTrace.Stop(trace, 3); --_active; Schedule(); }
    }
    public void Close()
    {
        _closed = true; _timer.Stop(); CancelFrame();
        while (_pending.TryDequeue(out var request)) request.Completion.TrySetCanceled(new CancellationToken(true));
        while (_warmPending.TryDequeue(out var warm)) warm.Completion.TrySetCanceled(new CancellationToken(true));
    }
    private sealed record Request(PreparedThumbnail Bitmap, Func<bool> StillVisible, CancellationToken Token)
    {
        public TaskCompletionSource<SoftwareBitmapSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
