namespace NektronMoments.Models;

/// <summary>Pixel distances, not photo rows. Independent of thumbnail size and RAM.</summary>
public sealed class PixelScrollMotion
{
    public const double PixelsPerNotch = 48;
    public const double MaximumPendingPixels = 144;
    public const double MaximumPixelsPerSecond = 720;
    private int _direction;
    public double Position { get; private set; }
    public double Target { get; private set; }
    public bool IsActive { get; private set; }

    public static bool HandlesInput(bool mouse, bool horizontal, bool control, bool shift, int delta) =>
        mouse && !horizontal && !control && !shift && delta != 0;

    public void Reset(double position = 0)
    {
        Position = Target = double.IsFinite(position) ? Math.Max(0, position) : 0;
        IsActive = false; _direction = 0;
    }
    public void Queue(int wheelDelta, double actualPosition, double maximum)
    {
        if (wheelDelta == 0 || !double.IsFinite(actualPosition) || !double.IsFinite(maximum)) return;
        maximum = Math.Max(0, maximum);
        Position = Math.Clamp(actualPosition, 0, maximum);
        var distance = -(double)wheelDelta / 120d * PixelsPerNotch; // Preserve high-resolution wheel fractions.
        var direction = Math.Sign(distance);
        var basis = IsActive && direction == _direction ? Target : Position;
        // Reversing discards the old destination, so the photo never drifts the wrong way.
        Target = Math.Clamp(Math.Clamp(basis + distance,
            Position - MaximumPendingPixels, Position + MaximumPendingPixels), 0, maximum);
        _direction = direction;
        IsActive = Math.Abs(Target - Position) > .01;
    }
    public double Advance(double seconds, double maximum)
    {
        if (!IsActive || !double.IsFinite(seconds) || seconds <= 0 || !double.IsFinite(maximum)) return Position;
        maximum = Math.Max(0, maximum);
        Position = Math.Clamp(Position, 0, maximum); Target = Math.Clamp(Target, 0, maximum);
        var dt = Math.Min(seconds, .05); // A delayed UI frame must not jump far to catch up.
        var remaining = Target - Position;
        var step = Math.Min(Math.Abs(remaining) * (1 - Math.Exp(-30 * dt)), MaximumPixelsPerSecond * dt);
        Position += Math.Sign(remaining) * step;
        if (Math.Abs(Target - Position) < .1) { Position = Target; IsActive = false; }
        return Position;
    }
}
