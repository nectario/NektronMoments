using NektronMoments.Models;

internal static class ScrollPolicyTests
{
    public static void Run(Action<bool, string> check)
    {
        static void Settle(PixelScrollMotion motion, double hz = 60, double maximum = 10000) {
            for (var frame = 0; motion.IsActive && frame < hz * 2; frame++) motion.Advance(1 / hz, maximum);
        }
        foreach (var hz in new[] { 30d, 60, 120, 144, 240 }) {
            var motion = new PixelScrollMotion();
            motion.Queue(-120, 500, 10000);
            var first = motion.Advance(1 / hz, 10000);
            check(first > 500 && first < 548, "Wheel motion has pixel intermediates, not an item jump");
            Settle(motion, hz);
            check(Math.Abs(motion.Position - 548) < .001, "Every refresh rate travels exactly 48 DIP per notch");
            check(!motion.IsActive, "No frame loop after wheel settles");
        }
        var fractional = new PixelScrollMotion(); fractional.Queue(-15, 500, 10000); Settle(fractional);
        check(fractional.Position == 506, "High resolution delta is not rounded to a full notch");
        var burst = new PixelScrollMotion();
        for (var i = 0; i < 100; i++) burst.Queue(-120, 500, 10000);
        check(burst.Target == 644, "Fast wheel bursts cannot queue many rows of travel");
        var previous = burst.Position;
        for (var i = 0; burst.IsActive && i < 120; i++) {
            var next = burst.Advance(1 / 60d, 10000);
            check(next >= previous && next - previous <= 12.1, "Bounded monotonic pixel steps at 60 Hz");
            previous = next;
        }
        burst.Queue(-120, 500, 10000); burst.Advance(.016, 10000);
        var reverseAt = burst.Position; burst.Queue(120, reverseAt, 10000);
        check(burst.Target == reverseAt - 48, "Reversal discards accumulated old direction");
        check(burst.Advance(.016, 10000) < reverseAt, "Reversal reacts in the next frame");
        burst.Reset(900); check(!burst.IsActive && burst.Target == 900, "Pointer/key cancellation clears the pending target");
        burst.Queue(120, 0, 10000); check(!burst.IsActive && burst.Target == 0, "Top bound cannot overscroll");
        burst.Queue(-120, 10000, 10000); check(!burst.IsActive, "Bottom bound cannot overscroll");
        burst.Queue(-120, 100, 10000); burst.Advance(1, 10000);
        check(burst.Position <= 136, "A stalled frame does not leap to catch up");
        burst.Advance(.016, 50); check(burst.Position <= 50 && burst.Target <= 50, "Shrinking extents clamp safely");
        burst.Reset(); burst.Queue(int.MinValue, 0, 10000);
        check(burst.Target == 144, "Large signed deltas do not overflow arithmetic");
        burst.Reset(5); burst.Queue(-120, double.NaN, 100); burst.Advance(double.NaN, 100);
        check(burst.Position == 5, "Invalid values do not poison the motion state");
        check(PixelScrollMotion.HandlesInput(true, false, false, false, -120), "Plain mouse wheel belongs to pixel controller");
        check(!PixelScrollMotion.HandlesInput(false, false, false, false, -120), "Touch/pen retain native behavior");
        check(!PixelScrollMotion.HandlesInput(true, true, false, false, -120), "Horizontal wheel remains native");
        check(!PixelScrollMotion.HandlesInput(true, false, true, false, -120), "Control modifier remains native");
        check(!PixelScrollMotion.HandlesInput(true, false, false, true, -120), "Shift modifier remains native");
    }
}
