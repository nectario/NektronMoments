namespace NektronMoments.Models;

/// <summary>Finite-duration absolute seeking. Never applies wheel sensitivity
/// and never queues earlier thumb positions behind the latest drag target.</summary>
public sealed class ScrollbarGlide
{
    private double _start, _elapsed, _duration;
    public double Position { get; private set; }
    public double Target { get; private set; }
    public bool IsActive { get; private set; }
    public void Stop() => IsActive = false;
    public void Retarget(double actual, double target, double maximum, double milliseconds)
    {
        if (!double.IsFinite(actual + target + maximum + milliseconds)) return;
        maximum = Math.Max(0, maximum); target = Math.Clamp(target, 0, maximum);
        if (IsActive && Math.Abs(target - Target) < .01) return;
        _start = Position = Math.Clamp(actual, 0, maximum); Target = target;
        _elapsed = 0; _duration = Math.Clamp(milliseconds, 0, 250) / 1000;
        IsActive = Math.Abs(Target - Position) > .01;
        if (_duration == 0) { Position = Target; IsActive = false; }
    }
    public double Advance(double seconds, double maximum)
    {
        if (!IsActive || !double.IsFinite(seconds + maximum) || seconds <= 0) return Position;
        _elapsed += seconds;
        var progress = Math.Clamp(_elapsed / _duration, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        Position = Math.Clamp(_start + (Target - _start) * eased, 0, Math.Max(0, maximum));
        if (progress >= 1) IsActive = false;
        return Position;
    }
}
