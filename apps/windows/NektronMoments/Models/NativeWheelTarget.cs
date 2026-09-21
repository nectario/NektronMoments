namespace NektronMoments.Models;

/// <summary>
/// Accumulates wheel destinations; ScrollViewer, not the UI thread, interpolates
/// the frames. Native offset samples may lag many wheel messages.
/// </summary>
public sealed class NativeWheelTarget
{
    public double WheelDistance { get; set; } = PixelScrollMotion.PixelsPerNotch;
    public double Target { get; private set; }
    public bool IsActive { get; private set; }
    private bool _hasTarget;
    private int _direction;

    public bool Reverses(int delta) => delta != 0 && _direction != 0 &&
        Math.Sign(-(double)delta) != _direction;

    public static double PendingLimit(double viewport, double wheelDistance)
    {
        var rate = Math.Clamp(double.IsFinite(wheelDistance) ? wheelDistance : PixelScrollMotion.PixelsPerNotch, 12, 144);
        var height = double.IsFinite(viewport) && viewport > 0 ? viewport : 500;
        // Room for a fast wheel train, but no seconds-long runaway after input
        // stops or the UI is blocked. This is a distance bound, not a speed cap.
        return Math.Max(height * 2, rate * 16);
    }

    public void Reset(double actual = 0)
    {
        Target = double.IsFinite(actual) ? Math.Max(0, actual) : 0;
        IsActive = _hasTarget = false;
        _direction = 0;
    }

    public void Queue(int delta, double actual, double maximum, double viewport)
    {
        if (delta == 0 || !double.IsFinite(actual) || !double.IsFinite(maximum)) return;
        maximum = Math.Max(0, maximum);
        actual = Math.Clamp(actual, 0, maximum);
        var rate = Math.Clamp(double.IsFinite(WheelDistance) ? WheelDistance : PixelScrollMotion.PixelsPerNotch, 12, 144);
        var distance = -(double)delta / 120 * rate;
        var direction = Math.Sign(distance);
        // Preserve fractional residuals even after a native float-rounded endpoint.
        // An external move outside that tolerance starts a new destination instead.
        var accumulated = _hasTarget && direction == _direction &&
            (IsActive || NativeScrollPrecision.IsAtTarget(actual, Target));
        var basis = accumulated ? Target : actual;
        var pending = PendingLimit(viewport, rate);
        Target = Math.Clamp(Math.Clamp(basis + distance, actual - pending, actual + pending), 0, maximum);
        _direction = direction;
        _hasTarget = true;
        IsActive = Math.Abs(Target - actual) > .000001;
    }

    public void Complete(double actual)
    {
        if (!NativeScrollPrecision.IsAtTarget(actual, Target)) { Reset(actual); return; }
        IsActive = false;
    }
}
