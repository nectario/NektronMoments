using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NektronMoments.Services;

/// <summary>Small UI-only publication batches; background warming never enters this queue.</summary>
public sealed class ThumbnailPresentationQueue
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Queue<Request> _pending = new();
    private int _active;
    private bool _closed;
    public long Started { get; private set; }
    public long Skipped { get; private set; }
    public double MaximumBatchMs { get; private set; }
    public int Pending => _pending.Count + _active;

    public ThumbnailPresentationQueue(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Interval = TimeSpan.FromMilliseconds(8);
        _timer.Tick += (_, _) => Drain();
    }

    public Task<SoftwareBitmapSource?> Enqueue(PreparedThumbnail bitmap, Func<bool> stillVisible, CancellationToken token)
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Presentation requests must originate on the UI thread.");
        token.ThrowIfCancellationRequested();
        if (_closed) return Task.FromCanceled<SoftwareBitmapSource?>(new CancellationToken(true));
        if (bitmap.Source is { } source) return Task.FromResult<SoftwareBitmapSource?>(source);
        var request = new Request(bitmap, stillVisible, token);
        _pending.Enqueue(request);
        Schedule();
        return request.Completion.Task.WaitAsync(token);
    }

    private void Schedule()
    {
        if (!_closed && _pending.Count > 0 && _active < 4 && !_timer.IsRunning) _timer.Start();
    }
    private void Drain()
    {
        var trace = DiagnosticTrace.Start(4);
        var watch = Stopwatch.StartNew();
        var examined = 0; var started = 0;
        while (!_closed && _active < 4 && _pending.Count > 0 && examined++ < 32 && started < 4 && watch.Elapsed.TotalMilliseconds < 2) {
            var request = _pending.Dequeue();
            if (request.Token.IsCancellationRequested || !request.StillVisible()) {
                ++Skipped; request.Completion.TrySetCanceled(new CancellationToken(true)); continue;
            }
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
        _closed = true; _timer.Stop();
        while (_pending.TryDequeue(out var request)) request.Completion.TrySetCanceled(new CancellationToken(true));
    }
    private sealed record Request(PreparedThumbnail Bitmap, Func<bool> StillVisible, CancellationToken Token)
    {
        public TaskCompletionSource<SoftwareBitmapSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
