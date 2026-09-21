namespace NektronMoments.Models;

/// <summary>Completion tolerance for native offsets represented by compositor floats.</summary>
public static class NativeScrollPrecision
{
    public const double MinimumTolerance = .1;
    public const double MaximumTolerance = 2;

    public static double ToleranceFor(double target)
    {
        if (!double.IsFinite(target) || target < 0) return 0;
        var value = (float)target;
        if (!float.IsFinite(value)) return MaximumTolerance;
        // A double destination may fall halfway between adjacent float offsets.
        // Use the larger neighboring spacing at powers of two; never widen beyond
        // two DIPs, even for an extreme extent whose native precision is worse.
        var spacing = Math.Max((double)MathF.BitIncrement(value) - value,
            (double)value - MathF.BitDecrement(value));
        return Math.Clamp(spacing * .5, MinimumTolerance, MaximumTolerance);
    }

    public static bool IsAtTarget(double actual, double target, bool isIntermediate = false) =>
        !isIntermediate && double.IsFinite(actual) && actual >= 0 &&
        double.IsFinite(target) && target >= 0 && Math.Abs(actual - target) <= ToleranceFor(target);
}
