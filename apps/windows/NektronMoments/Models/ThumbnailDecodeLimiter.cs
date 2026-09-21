namespace NektronMoments.Models;

/// <summary>Reserves decode capacity for visible photos while keeping lookahead bounded.</summary>
public sealed class ThumbnailDecodeLimiter(int foregroundCount, int lookaheadCount)
{
    private readonly SemaphoreSlim _foreground = new(Math.Max(1, foregroundCount));
    private readonly SemaphoreSlim _lookahead = new(Math.Max(1, lookaheadCount));

    public async Task<IDisposable> AcquireAsync(bool foreground, Task? promoted, CancellationToken cancellation)
    {
        if (foreground || promoted?.IsCompleted == true) return await AcquireForegroundAsync(cancellation).ConfigureAwait(false);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var ahead = _lookahead.WaitAsync(waiting.Token);
        if (promoted is not null) {
            await Task.WhenAny(ahead, promoted).ConfigureAwait(false);
            if (promoted.IsCompleted) {
                waiting.Cancel();
                // Promotion can race a lookahead grant. Return that permit before
                // entering the reserved foreground lane, including cancellation.
                try { await ahead.ConfigureAwait(false); _lookahead.Release(); }
                catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                cancellation.ThrowIfCancellationRequested();
                return await AcquireForegroundAsync(cancellation).ConfigureAwait(false);
            }
        }
        await ahead.ConfigureAwait(false);
        return new Lease(_lookahead);
    }

    private async Task<IDisposable> AcquireForegroundAsync(CancellationToken cancellation)
    {
        await _foreground.WaitAsync(cancellation).ConfigureAwait(false);
        return new Lease(_foreground);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
