namespace NektronMoments.Models;

/// <summary>Frame-rate-independent absolute seeking with a critically damped follower.
/// Retargeting preserves the displayed trajectory instead of restarting an ease from a
/// possibly stale native offset. There is only ever one, latest thumb destination.</summary>
public sealed class ScrollbarGlide
{
    private double _velocity, _elapsed, _duration;
    public double Position { get; private set; }
    public double Target { get; private set; }
    public bool IsActive { get; private set; }
    public void Stop() { IsActive = false; _velocity = 0; }
    public void Retarget(double actual, double target, double maximum, double milliseconds)
    {
        if (!double.IsFinite(actual) || !double.IsFinite(target) ||
            !double.IsFinite(maximum) || !double.IsFinite(milliseconds)) return;
        maximum = Math.Max(0, maximum); target = Math.Clamp(target, 0, maximum);
        var duration = milliseconds <= 0 ? 0 : Math.Clamp(milliseconds, 1, 250) / 1000;
        if (IsActive && Math.Abs(target - Target) < .01 && _duration == duration && Position <= maximum) return;
        if (!IsActive) { Position = Math.Clamp(actual, 0, maximum); _velocity = 0; }
        else Position = Math.Clamp(Position, 0, maximum);
        Target = target;
        // A reversal must respond on the next frame, never coast in the old direction.
        if (Math.Sign(_velocity) != Math.Sign(Target - Position)) _velocity = 0;
        _elapsed = 0; _duration = duration;
        IsActive = Math.Abs(Target - Position) > .01;
        if (_duration == 0 || !IsActive) { Position = Target; Stop(); }
    }
    public double Advance(double seconds, double maximum)
    {
        if (!IsActive || !double.IsFinite(seconds) || !double.IsFinite(maximum) || seconds <= 0) return Position;
        maximum = Math.Max(0, maximum);
        Position = Math.Clamp(Position, 0, maximum); Target = Math.Clamp(Target, 0, maximum);
        // Exact integration is independent of refresh rate during normal rendering.
        // A suspended/busy UI gets one bounded catch-up step, not a large visual leap.
        var dt = Math.Min(seconds, .05);
        _elapsed += dt;
        var omega = 10 / _duration;
        var displacement = Position - Target;
        var coefficient = _velocity + omega * displacement;
        var decay = Math.Exp(-omega * dt);
        var next = Target + (displacement + coefficient * dt) * decay;
        _velocity = (_velocity - omega * coefficient * dt) * decay;
        // Reaching/passing a closer retarget must not overshoot it.
        Position = Math.Clamp(next, Math.Min(Position, Target), Math.Max(Position, Target));
        if (Position == Target || (Math.Abs(Target - Position) < .1 && Math.Abs(_velocity) < 2) ||
            _elapsed >= _duration * 2) { Position = Target; Stop(); }
        return Position;
    }
}
