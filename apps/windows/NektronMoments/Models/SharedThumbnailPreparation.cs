namespace NektronMoments.Models;

/// <summary>
/// One preparation per version/resolution key. A tile owns its wait, not the work:
/// recycling it must not throw away an image another tile or lookahead needs.
/// Completed and failed jobs leave the map; the separate byte-budgeted cache owns results.
/// </summary>
public sealed class SharedThumbnailPreparation<T> where T : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Work> _running = [];
    public int Count { get { lock (_gate) return _running.Count; } }

    public Task<T?> GetAsync(string key, bool foreground,
        Func<Task, Task<T?>> prepare, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        Work work;
        bool start;
        lock (_gate) {
            start = !_running.TryGetValue(key, out work!);
            if (start) _running.Add(key, work = new Work());
        }
        if (foreground) work.Promoted.TrySetResult();
        if (start) _ = Task.Run(() => RunAsync(key, work, prepare));
        // WaitAsync cancels only this observer. In-flight extraction/decode is useful
        // even when its first tile scrolls away, so it still finishes and warms RAM.
        return callerToken.CanBeCanceled ? work.Result.Task.WaitAsync(callerToken) : work.Result.Task;
    }

    public void Promote(string key)
    {
        lock (_gate) {
            if (_running.TryGetValue(key, out var work)) work.Promoted.TrySetResult();
        }
    }

    private async Task RunAsync(string key, Work work, Func<Task, Task<T?>> prepare)
    {
        T? result = null;
        Exception? error = null;
        try { result = await prepare(work.Promoted.Task).ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        finally {
            lock (_gate) {
                if (_running.TryGetValue(key, out var current) && ReferenceEquals(current, work))
                    _running.Remove(key);
            }
        }
        if (error is null) work.Result.TrySetResult(result);
        else {
            work.Result.TrySetException(error);
            // All interested callers may have canceled their waits; observe the
            // shared failure as well so an abandoned job cannot surface later.
            _ = work.Result.Task.Exception;
        }
    }

    private sealed class Work
    {
        public TaskCompletionSource Promoted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<T?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
