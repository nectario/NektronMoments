using System.Diagnostics;

namespace NektronMoments.Models;

/// <summary>Defer optional background work while input is active; visible work bypasses it.</summary>
public sealed class ThumbnailWorkGate
{
    private int _held;
    private long _quietUntil;
    public bool IsPaused => Volatile.Read(ref _held) != 0 || Stopwatch.GetTimestamp() < Volatile.Read(ref _quietUntil);
    public void Pulse(int quietMilliseconds = 180) =>
        Interlocked.Exchange(ref _quietUntil, Stopwatch.GetTimestamp() + (long)(Math.Max(0, quietMilliseconds) * Stopwatch.Frequency / 1000d));
    public void Hold(bool held)
    {
        Volatile.Write(ref _held, held ? 1 : 0);
        if (!held) Pulse();
    }
    public async Task WaitAsync(Task promoted, CancellationToken token = default)
    {
        while (!promoted.IsCompleted && IsPaused) {
            token.ThrowIfCancellationRequested();
            // This wait is on workers, never the UI queue. Promotion wakes a
            // needed photo immediately even when the user keeps dragging.
            var wait = Task.Delay(40, token);
            await Task.WhenAny(promoted, wait).ConfigureAwait(false);
            if (!promoted.IsCompleted) await wait.ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
    }
}
