using NektronMoments.Models;

internal static class NativeWheelTargetTests
{
    public static void Run(Action<bool, string> check)
    {
        var wheel = new NativeWheelTarget();
        wheel.Queue(-120, 500, 100000, 500);
        check(wheel.Target == 564 && wheel.IsActive, "Native wheel default remains exactly 64 DIP per notch");
        wheel.Complete(564);
        check(!wheel.IsActive, "Native wheel completion leaves no software frame loop to run");
        wheel.Queue(-120, 564, 100000, 500);
        check(wheel.Target == 628, "A separated slow notch resumes smoothly from the completed endpoint");

        foreach (var rate in new[] { 2, 8, 30, 60 }) {
            foreach (var viewport in new[] { 500d, 1000d, 2000d }) {
                var train = new NativeWheelTarget();
                var exact = true;
                var monotonic = true;
                var previous = 1000d;
                // Actual native offsets lag the target by 200 ms, intentionally
                // longer than a frame. Fast input must still accumulate each notch.
                for (var i = 0; i < rate * 2; i++) {
                    var actual = 1000 + Math.Max(0, i - (int)Math.Ceiling(rate * .2)) * 64;
                    train.Queue(-120, actual, 100000, viewport);
                    exact &= train.Target == 1000 + (i + 1) * 64;
                    monotonic &= train.Target > previous;
                    previous = train.Target;
                }
                check(exact, $"{rate} notches/sec retains the full distance with a {viewport} DIP viewport");
                check(monotonic, $"{rate} notches/sec never rewinds to a stale native offset");
                train.Complete(train.Target);
                check(!train.IsActive, $"{rate} notches/sec settles after the input train ends");
            }
        }

        var burst = new NativeWheelTarget();
        for (var i = 0; i < 8; i++) burst.Queue(-120, 500, 100000, 500);
        check(burst.Target == 1012, "Eight coalesced notches travel 512 DIP instead of truncating at the old 192 DIP ceiling");
        for (var i = 0; i < 1000; i++) burst.Queue(-120, 500, 100000, 500);
        check(burst.Target == 500 + NativeWheelTarget.PendingLimit(500, 64), "A blocked native view bounds pending distance without an unlimited runaway");
        check(NativeWheelTarget.PendingLimit(2000, 64) == 4000, "A large viewport can sustain faster motion without a fixed pixel/sec cap");
        check(burst.Reverses(120), "Reversal is exposed so the old native animation can be cancelled immediately");
        burst.Queue(120, 800, 100000, 500);
        check(burst.Target == 736, "Reversal starts from the currently displayed photo instead of the old target");
        burst.Queue(120, 800, 100000, 500);
        check(burst.Target == 672, "Repeated reverse input accumulates even before native offset advances");
        burst.Reset(1400);
        check(!burst.IsActive && !burst.Reverses(-120), "Pointer, key and scrollbar ownership changes clear pending wheel state");
        burst.Queue(-120, 1400, 100000, 500);
        check(burst.Target == 1464, "Wheel input after a scrollbar move starts at the new actual position");

        var fractional = new NativeWheelTarget();
        for (var i = 0; i < 8; i++) fractional.Queue(-15, 500, 10000, 500);
        check(fractional.Target == 564, "Eight high-resolution fractions equal exactly one wheel notch");
        var quantized = new NativeWheelTarget();
        const double origin = 24000000;
        var quantizedActual = origin;
        for (var i = 0; i < 120; i++) {
            quantized.Queue(-1, quantizedActual, 30000000, 500);
            quantizedActual = (float)quantized.Target;
            quantized.Complete(quantizedActual);
        }
        check(Math.Abs(quantized.Target - origin - 64) < .00001, "Float-rounded native endpoints do not discard high-resolution wheel residuals");
        quantized.Queue(-120, origin + 1000, 30000000, 500);
        check(quantized.Target == origin + 1064, "An external move after completion discards stale fractional state");

        var custom = new NativeWheelTarget { WheelDistance = 12 };
        custom.Queue(-120, 500, 10000, 500);
        check(custom.Target == 512, "Native wheel respects a custom slow setting");
        custom.Reset(500); custom.WheelDistance = 144;
        custom.Queue(-120, 500, 10000, 500);
        check(custom.Target == 644, "Native wheel respects a custom fast setting");
        custom.Reset(); custom.Queue(120, 0, 10000, 500);
        check(custom.Target == 0 && !custom.IsActive, "Native wheel cannot overscroll above the first item");
        custom.Queue(-120, 10000, 10000, 500);
        check(custom.Target == 10000 && !custom.IsActive, "Native wheel cannot overscroll below the final item");
        custom.Queue(-120, 10000, 2500, 500);
        check(custom.Target == 2500 && !custom.IsActive, "Shrinking extents clamp both actual position and destination");
        custom.Reset(500); custom.Queue(int.MinValue, 500, 100000, 500);
        check(custom.Target == 500 + NativeWheelTarget.PendingLimit(500, 144), "Extreme signed deltas do not overflow target arithmetic");
        custom.Reset(500); custom.Queue(-120, double.NaN, 10000, 500);
        check(custom.Target == 500 && !custom.IsActive, "Invalid positions cannot poison wheel state");
        custom.Queue(-120, 500, double.PositiveInfinity, 500);
        check(custom.Target == 500 && !custom.IsActive, "Invalid native extents are ignored");
        custom.WheelDistance = double.NaN; custom.Queue(-120, 500, 10000, double.NaN);
        check(custom.Target == 564, "Invalid optional settings safely retain the default distance");
    }
}
