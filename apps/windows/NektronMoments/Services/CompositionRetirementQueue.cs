using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace NektronMoments.Services;

/// <summary>Release detached, exclusively-owned resize resources in short UI-thread turns.</summary>
public sealed class CompositionRetirementQueue
{
    private readonly Queue<IDisposable> _pending = new();
    private readonly DispatcherQueueTimer _timer;
    public int Pending => _pending.Count;
    public long Retired { get; private set; }
    public int Failures { get; private set; }
    public double MaximumBatchMs { get; private set; }
    public CompositionRetirementQueue(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(8);
        _timer.Tick += (_, _) => Drain(false);
    }
    public void Enqueue(IEnumerable<IDisposable> resources)
    {
        foreach (var resource in resources) _pending.Enqueue(resource);
        if (_pending.Count > 0 && !_timer.IsRunning) _timer.Start();
    }
    public void Close() { _timer.Stop(); Drain(true); }
    private void Drain(bool all)
    {
        var clock = Stopwatch.StartNew(); var count = 0;
        while (_pending.Count > 0 && (all || count < 48 && clock.Elapsed.TotalMilliseconds < 2)) {
            var resource = _pending.Dequeue();
            try { resource.Dispose(); } catch (Exception error) { ++Failures; DiagnosticLog.Write(error); }
            ++Retired; ++count;
        }
        if (!all) MaximumBatchMs = Math.Max(MaximumBatchMs, clock.Elapsed.TotalMilliseconds);
        if (_pending.Count == 0) _timer.Stop();
    }
}
