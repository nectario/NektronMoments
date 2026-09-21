using NektronMoments.Models;

internal static class BrowsingPolicyTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var total in new[] { 0, 199, 200, 201, 144135 }) {
            check(BrowsingPolicy.InitialCount(total) == Math.Min(total, 200), "Initial native browsing range is at most 200 items");
            var current = BrowsingPolicy.InitialCount(total);
            check(BrowsingPolicy.NextCount(total, current) == Math.Min(total, current + 200), "Browsing extends by exactly one available batch");
            check(BrowsingPolicy.NextCount(total, total) == total, "Final browsing range never extends past the catalog");
        }
        check(BrowsingPolicy.InitialCount(-1) == 0 && BrowsingPolicy.NextCount(-1, 200) == 0,
            "Invalid negative catalog sizes are empty");
        check(BrowsingPolicy.NextCount(int.MaxValue, int.MaxValue - 100) == int.MaxValue,
            "Range growth does not overflow large catalog counts");
        check(!BrowsingPolicy.ShouldExtend(1000, 200, 99, 24, false), "Browsing does not expand before approaching its end");
        check(!BrowsingPolicy.ShouldExtend(1000, 200, 174, 24, false) &&
            BrowsingPolicy.ShouldExtend(1000, 200, 175, 24, false), "Next range becomes eligible with 24 photos remaining");
        check(BrowsingPolicy.ShouldExtend(1000, 200, 151, 48, false), "A larger viewport gets earlier range preparation");
        check(!BrowsingPolicy.ShouldExtend(1000, 200, 199, 24, true), "A held scrollbar thumb never changes range");
        check(!BrowsingPolicy.ShouldExtend(200, 200, 199, 24, false), "The final photo never requests an extra range");
        check(!BrowsingPolicy.ShouldExtend(201, 0, 0, 24, false) &&
            !BrowsingPolicy.ShouldExtend(201, 200, -1, 24, false) &&
            !BrowsingPolicy.ShouldExtend(201, 200, 199, 0, false), "Unmeasured viewports do not trigger range expansion");
        check(!BrowsingPolicy.ShouldExtend(1000, 200, 98, int.MaxValue, false) &&
            BrowsingPolicy.ShouldExtend(1000, 200, 99, int.MaxValue, false), "Dense viewports cap the extension threshold at half a batch");

        var forward = BrowsingPolicy.PrefetchIndexes(5000, 180, 199, 200, 1, 1000);
        check(forward.Take(20).SequenceEqual(Enumerable.Range(180, 20)), "Visible photos always have first thumbnail priority");
        check(forward.Skip(20).Take(200).SequenceEqual(Enumerable.Range(200, 200)),
            "The next 200 photos warm before reverse-side or distant work, beyond the exposed range");
        check(forward[220] == 179 && forward.Contains(100), "Warming preserves a reverse-side buffer before more distant work");
        check(forward.Length == 1000 && forward.Max() > 399, "Thumbnail warming continues far beyond the native scrollbar range");
        var backward = BrowsingPolicy.PrefetchIndexes(5000, 1500, 1519, 1600, -1, 1000);
        check(backward.Take(20).SequenceEqual(Enumerable.Range(1500, 20)) &&
            backward.Skip(20).Take(200).SequenceEqual(Enumerable.Range(1300, 200).Reverse()),
            "Direction reversal gives previously passed photos immediate preparation priority");
        check(backward[220] == 1520 && backward.Contains(700), "Backward travel keeps a forward reserve while looking farther behind");
        check(BrowsingPolicy.PrefetchIndexes(500, 180, 199, 200, 1, 5).SequenceEqual(Enumerable.Range(180, 5)),
            "A small warm budget is spent on visible photos first");
        check(BrowsingPolicy.PrefetchIndexes(500, 180, 199, 200, 0, 100)
            .SequenceEqual(BrowsingPolicy.PrefetchIndexes(500, 180, 199, 200, 1, 100)), "Initial stationary warming looks forward");

        // Exercise both catalog boundaries, tiny libraries, mismatched visible
        // indexes and small plans. Every file appears at most once in a plan.
        foreach (var total in new[] { 0, 1, 199, 200, 201, 1000 })
        foreach (var first in new[] { -1, 0, 180, total - 2, int.MaxValue })
        foreach (var direction in new[] { -1, 0, 1 })
        foreach (var budget in new[] { 0, 1, 20, 200, 2000 }) {
            var plan = BrowsingPolicy.PrefetchIndexes(total, first,
                first == int.MaxValue ? int.MaxValue : first + 19, Math.Min(total, 200), direction, budget);
            check(plan.Length == Math.Min(total, budget) && plan.Distinct().Count() == plan.Length &&
                plan.All(index => index >= 0 && index < total), "Prefetch plans fill their bound without duplicates or out-of-catalog files");
        }
        check(BrowsingPolicy.PrefetchIndexes(200, 0, 20, 0, 1, 100).Length == 0,
            "A catalog not yet exposed does not claim a measured viewport");
        var atEnd = BrowsingPolicy.PrefetchIndexes(1000, 980, 999, 1000, 1, 600);
        check(atEnd.Length == 600 && atEnd.Distinct().Count() == 600 && atEnd.Last() == 400,
            "Warming at the catalog end uses spare capacity to retain photos behind");
        var atStart = BrowsingPolicy.PrefetchIndexes(1000, 0, 19, 200, -1, 600);
        check(atStart.Length == 600 && atStart.Distinct().Count() == 600 && atStart.Last() == 599,
            "Warming at the catalog start retains upcoming photos when traveling backward");

        const long GiB = 1024L * 1024 * 1024;
        check(BrowsingPolicy.WarmCount(20000, 256, 8 * GiB) == 16384, "256-pixel warming uses at most half of an 8 GB decoded cache");
        check(BrowsingPolicy.WarmCount(20000, 512, 8 * GiB) == 4096, "Higher-resolution warming respects its larger decoded footprint");
        check(BrowsingPolicy.WarmCount(20000, 1024, 8 * GiB) == 1024, "High-DPI thumbnails preserve hot-cache space");
        check(BrowsingPolicy.WarmCount(500, 256, 8 * GiB) == 500, "The configured lookahead maximum still bounds a large-memory machine");
        check(BrowsingPolicy.WarmCount(500, 1024, 1) == 0 &&
            BrowsingPolicy.WarmCount(500, 0, 8 * GiB) == 0 &&
            BrowsingPolicy.WarmCount(-1, 256, 8 * GiB) == 0 &&
            BrowsingPolicy.WarmCount(500, 256, -1) == 0 &&
            BrowsingPolicy.WarmCount(int.MaxValue, uint.MaxValue, long.MaxValue) == 0,
            "Invalid or extreme image budgets cannot overflow into unbounded warming");
    }
}
