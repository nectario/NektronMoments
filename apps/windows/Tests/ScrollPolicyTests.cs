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
            check(first > 500 && first < 564, "Wheel motion has pixel intermediates, not an item jump");
            Settle(motion, hz);
            check(Math.Abs(motion.Position - 564) < .001, "Every refresh rate travels exactly 64 DIP per default notch");
            check(!motion.IsActive, "No frame loop after wheel settles");
        }
        var fractional = new PixelScrollMotion(); fractional.Queue(-15, 500, 10000); Settle(fractional);
        check(fractional.Position == 508, "High resolution delta is not rounded to a full notch");
        var burst = new PixelScrollMotion();
        for (var i = 0; i < 100; i++) burst.Queue(-120, 500, 10000);
        check(burst.Target == 692, "Fast wheel bursts cannot queue more than three default notches");
        var previous = burst.Position;
        for (var i = 0; burst.IsActive && i < 120; i++) {
            var next = burst.Advance(1 / 60d, 10000);
            check(next >= previous && next - previous <= 12.1, "Bounded monotonic pixel steps at 60 Hz");
            previous = next;
        }
        burst.Queue(-120, 500, 10000); burst.Advance(.016, 10000);
        var reverseAt = burst.Position; burst.Queue(120, reverseAt, 10000);
        check(burst.Target == reverseAt - 64, "Reversal discards accumulated old direction");
        check(burst.Advance(.016, 10000) < reverseAt, "Reversal reacts in the next frame");
        burst.Reset(900); check(!burst.IsActive && burst.Target == 900, "Pointer/key cancellation clears the pending target");
        burst.Queue(120, 0, 10000); check(!burst.IsActive && burst.Target == 0, "Top bound cannot overscroll");
        burst.Queue(-120, 10000, 10000); check(!burst.IsActive, "Bottom bound cannot overscroll");
        burst.Queue(-120, 100, 10000); burst.Advance(1, 10000);
        check(burst.Position <= 136, "A stalled frame does not leap to catch up");
        burst.Advance(.016, 50); check(burst.Position <= 50 && burst.Target <= 50, "Shrinking extents clamp safely");
        burst.Reset(); burst.Queue(int.MinValue, 0, 10000);
        check(burst.Target == 192, "Large signed deltas do not overflow arithmetic");
        burst.Reset(5); burst.Queue(-120, double.NaN, 100); burst.Advance(double.NaN, 100);
        check(burst.Position == 5, "Invalid values do not poison the motion state");
        check(PixelScrollMotion.HandlesInput(true, false, false, false, -120), "Plain mouse wheel belongs to pixel controller");
        check(!PixelScrollMotion.HandlesInput(false, false, false, false, -120), "Touch/pen retain native behavior");
        check(!PixelScrollMotion.HandlesInput(true, true, false, false, -120), "Horizontal wheel remains native");
        check(!PixelScrollMotion.HandlesInput(true, false, true, false, -120), "Control modifier remains native");
        check(!PixelScrollMotion.HandlesInput(true, false, false, true, -120), "Shift modifier remains native");
        var custom = new PixelScrollMotion { WheelDistance = 12 }; custom.Queue(-120, 500, 10000); Settle(custom);
        check(custom.Position == 512, "Independent slow wheel speed");
        custom.WheelDistance = 144; custom.Queue(-120, 500, 10000); Settle(custom);
        check(custom.Position == 644, "Independent fast wheel speed");
        var glide = new ScrollbarGlide();
        glide.Retarget(500, 4000, 10000, 120);
        check(glide.Advance(.02, 10000) is > 500 and < 4000, "Absolute scrollbar target is smoothed");
        glide.Retarget(glide.Position, 1500, 10000, 120);
        glide.Advance(.03, 10000); glide.Retarget(glide.Position, 2500, 10000, 120);
        for (var frame = 0; glide.IsActive && frame < 60; frame++) glide.Advance(1 / 60d, 10000);
        check(glide.Position == 2500 && !glide.IsActive, "Latest scrollbar target wins; bounded settling");
        glide.Retarget(500, 20000, 10000, 0);
        check(glide.Position == 10000 && !glide.IsActive, "Immediate mode and extent bounds");
        glide.Retarget(500, -200, 10000, 120);
        for (var frame = 0; glide.IsActive && frame < 60; frame++) glide.Advance(1 / 60d, 10000);
        check(glide.Position == 0, "Scrollbar clamps its top boundary");
        glide.Retarget(500, 4000, 10000, 120); glide.Stop();
        check(!glide.IsActive, "Scrollbar glide cancellation");

        // Same physical time, different display refresh rates: analytic integration
        // must not change the trajectory (the old repeated cubic restart did).
        double? reference = null;
        foreach (var hz in new[] { 30d, 60, 120, 144, 240 }) {
            var follower = new ScrollbarGlide(); follower.Retarget(500, 5000, 10000, 250);
            var elapsed = 0d;
            while (elapsed < .1 - 1e-9) {
                var dt = Math.Min(1 / hz, .1 - elapsed); follower.Advance(dt, 10000); elapsed += dt;
            }
            reference ??= follower.Position;
            check(Math.Abs(follower.Position - reference.Value) < 1e-7, "Scrollbar trajectory is refresh-rate independent");
            for (var frame = 0; follower.IsActive && frame < hz; frame++) follower.Advance(1 / hz, 10000);
            check(follower.Position == 5000 && !follower.IsActive, "Scrollbar settles exactly and stops its frame loop at every refresh rate");
        }

        var dense = new ScrollbarGlide(); dense.Retarget(500, 4000, 10000, 120);
        dense.Advance(.016, 10000); var displayed = dense.Position;
        // Native offset can still reflect the preceding layout when many pointer
        // events arrive in one frame. None may rewind the last requested position.
        for (var i = 0; i < 100; i++) dense.Retarget(500, 4000 + i, 10000, 120);
        check(dense.Position == displayed && dense.Target == 4099, "Dense input only updates the latest target, never rewinds to stale native offset");
        check(dense.Advance(.016, 10000) > displayed, "Continuous drag progresses despite stale native offset samples");
        displayed = dense.Position; dense.Retarget(500, 100, 10000, 120);
        check(dense.Position == displayed && dense.Advance(.016, 10000) < displayed, "Scrollbar reversal keeps position continuous and changes direction next frame");

        var smooth = new ScrollbarGlide(); smooth.Retarget(500, 2000, 10000, 250);
        smooth.Advance(.016, 10000); var before = smooth.Position;
        smooth.Advance(.00001, 10000); var speedBefore = (smooth.Position - before) / .00001;
        before = smooth.Position; smooth.Retarget(500, 2001, 10000, 250);
        smooth.Advance(.00001, 10000); var speedAfter = (smooth.Position - before) / .00001;
        check(Math.Abs(speedAfter - speedBefore) / speedBefore < .01, "A nearby new thumb target preserves velocity instead of restarting acceleration");
        smooth.Retarget(500, smooth.Position + 1, 10000, 250);
        for (var frame = 0; smooth.IsActive && frame < 120; frame++) smooth.Advance(1 / 60d, 10000);
        check(smooth.Position == smooth.Target && !smooth.IsActive, "Closer retarget cannot overshoot or oscillate");

        var delayed = new ScrollbarGlide(); delayed.Retarget(500, 5000, 10000, 250);
        var bounded = new ScrollbarGlide(); bounded.Retarget(500, 5000, 10000, 250);
        check(delayed.Advance(5, 10000) == bounded.Advance(.05, 10000), "A stalled scrollbar frame takes one bounded catch-up step");
        delayed.Advance(.016, 100);
        check(delayed.Position <= 100 && delayed.Target <= 100 && !delayed.IsActive, "Scrollbar clamps and settles if the collection shrinks");
        delayed.Retarget(20, 90, 100, 120); displayed = delayed.Position;
        delayed.Retarget(double.NaN, 0, 100, 120); delayed.Advance(double.PositiveInfinity, 100);
        check(delayed.Position == displayed && delayed.Target == 90, "Invalid scrollbar input leaves state unchanged");
        delayed.Retarget(20, 90, 100, 0);
        check(delayed.Position == 90 && !delayed.IsActive, "Switching to immediate mode applies even an unchanged target");

        foreach (var hz in new[] { 30d, 60, 120, 144, 240 }) {
            var edge = new ScrollbarGlide();
            edge.Retarget(500, 600, 10000, 120);
            var maximumStepError = 0d;
            for (var frame = 1; frame <= hz * 2; frame++) {
                var last = edge.Position;
                // Model auto-continuation at a steady 600 DIP/s while native offsets
                // lag the render requests. Smooth motion must not restart per target.
                edge.Retarget(Math.Max(0, last - 20), 600 + frame * 600 / hz, 10000, 120);
                edge.Advance(1 / hz, 10000);
                if (frame > hz) maximumStepError = Math.Max(maximumStepError, Math.Abs(edge.Position - last - 600 / hz));
            }
            check(maximumStepError < .001, "Continuous edge following reaches a constant pixel velocity without per-target pulses");
            for (var frame = 0; edge.IsActive && frame < hz; frame++) edge.Advance(1 / hz, 10000);
            check(!edge.IsActive && edge.Position == edge.Target, "Releasing continuous edge tracking settles at the latest position");
        }
    }
}
