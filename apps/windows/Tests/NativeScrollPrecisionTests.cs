using NektronMoments.Models;

internal static class NativeScrollPrecisionTests
{
    public static void Run(Action<bool, string> check)
    {
        check(NativeScrollPrecision.ToleranceFor(2500) == .1, "Ordinary native scrolling keeps its original subpixel tolerance");
        check(NativeScrollPrecision.IsAtTarget(2500.05, 2500), "Ordinary subpixel completion succeeds");
        check(!NativeScrollPrecision.IsAtTarget(2500.11, 2500), "Ordinary unfinished motion is not hidden");
        var extent = 18251472.754699707;
        var globalTarget = extent * .65;
        var nativeOffset = (double)(float)globalTarget;
        check(Math.Abs(nativeOffset - globalTarget) > .1 && Math.Abs(nativeOffset - globalTarget) < .5,
            "The production regression destination reproduces a greater-than-0.1-DIP float rounding error");
        check(NativeScrollPrecision.ToleranceFor(globalTarget) == .5, "Million-DIP precision uses half a float ULP");
        check(NativeScrollPrecision.IsAtTarget(nativeOffset, globalTarget), "Representable native endpoint completes without a false fallback");
        check(!NativeScrollPrecision.IsAtTarget(nativeOffset - 1, globalTarget), "An adjacent stale native destination still fails completion");
        check(!NativeScrollPrecision.IsAtTarget(nativeOffset, globalTarget, isIntermediate: true), "An intermediate event never proves native completion");
        check(NativeScrollPrecision.ToleranceFor(extent) == 1, "Large catalog precision remains bounded to its real float spacing");
        check(NativeScrollPrecision.IsAtTarget((float)extent, extent), "A representable large-catalog bottom is accepted");
        check(NativeScrollPrecision.IsAtTarget(extent, extent), "The exact native scroll extent is accepted");
        check(NativeScrollPrecision.ToleranceFor(40000000) == 2, "Single-column large catalogs permit at most two DIPs");
        check(NativeScrollPrecision.ToleranceFor(1e12) == 2, "Extreme extents cannot inflate completion tolerance indefinitely");
        check(!NativeScrollPrecision.IsAtTarget(1e12 + 3, 1e12), "Material error at extreme extents is never masked");
        check(!NativeScrollPrecision.IsAtTarget(double.NaN, globalTarget), "NaN actual offsets cannot complete");
        check(!NativeScrollPrecision.IsAtTarget(0, double.PositiveInfinity), "Infinite targets cannot complete");
        check(!NativeScrollPrecision.IsAtTarget(-1, -1), "Invalid negative offsets cannot complete");
        foreach (var target in new[] { 0d, 1000.123, 8388607.75, 8388608.25, globalTarget, 16777215.5, 16777217d, 33554434d }) {
            check(NativeScrollPrecision.IsAtTarget((double)(float)target, target), "Nearest native float is accepted across precision boundaries");
            check(!NativeScrollPrecision.IsAtTarget(target + NativeScrollPrecision.ToleranceFor(target) + .01, target),
                "Offsets outside the quantization bound remain unfinished");
        }
    }
}
