using NektronMoments.Models;

internal static class GalleryWorkPolicyTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var small = GalleryWorkPolicy.CacheLength(4, 226, 650, 8);
        var maximized = GalleryWorkPolicy.CacheLength(28, 135, 1750, 8);
        check(small == 8 && maximized < 1, "Large windows bound native controls without shrinking photo caches");
        foreach (var columns in new[] { 1, 4, 10, 28, 50 })
        foreach (var height in new[] { 250d, 650, 1750, 3000 }) {
            var visibleItems = (Math.Ceiling(height / 135) + 1) * columns;
            var cache = GalleryWorkPolicy.CacheLength(columns, 135, height, 8);
            check(cache is >= .5 and <= 8, "Native cache stays inside supported bounds");
            check(cache * visibleItems <= Math.Max(240, visibleItems * .5) + .001, "Live offscreen XAML work has a viewport-aware budget");
        }
        check(GalleryWorkPolicy.CacheLength(4, 226, 650, 3) == 3, "Modest-machine cache maximum is preserved");
        foreach (var result in new[] {
            GalleryWorkPolicy.CacheLength(0, 200, 600, 8), GalleryWorkPolicy.CacheLength(4, 0, 600, 8),
            GalleryWorkPolicy.CacheLength(4, double.NaN, 600, 8), GalleryWorkPolicy.CacheLength(4, 200, double.PositiveInfinity, 8),
            GalleryWorkPolicy.CacheLength(4, 200, 600, double.NaN), GalleryWorkPolicy.CacheLength(4, 200, 600, -1) })
            check(result == .5, "Unknown layout/cache values retain a safe minimal buffer");
        check(GalleryWorkPolicy.IsNearViewport(100, 100, 0, 0), "Visible photos get reserved decode work");
        check(GalleryWorkPolicy.IsNearViewport(100, 100, 0, 220), "One approaching row can decode ahead");
        check(!GalleryWorkPolicy.IsNearViewport(100, 100, 0, 300), "Far-offscreen work stays in lookahead lane");
        check(!GalleryWorkPolicy.IsNearViewport(0, 0, 0, 0) &&
            !GalleryWorkPolicy.IsNearViewport(100, 100, double.NaN, 0), "Unmeasured controls do not consume foreground capacity");
        check(!GalleryWorkPolicy.IsNearViewport(100, 100, -300, -300), "Invalid negative bring-into-view distances stay out of foreground");

        var limiter = new ThumbnailDecodeLimiter(2, 1);
        using var ahead = await limiter.AcquireAsync(false, null, CancellationToken.None);
        using var first = await limiter.AcquireAsync(true, null, CancellationToken.None);
        var promotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = limiter.AcquireAsync(false, promotion.Task, CancellationToken.None);
        check(!pending.IsCompleted, "Lookahead decode count is bounded independently of disk workers");
        promotion.SetResult();
        using var visible = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        check(pending.IsCompletedSuccessfully, "Newly visible tile bypasses the busy lookahead lane");
        using var canceled = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(true, null, canceled.Token);
        check(!waiting.IsCompleted, "Foreground decode concurrency is also bounded");
        canceled.Cancel();
        try { await waiting; check(false, "Canceled decode must not run"); }
        catch (OperationCanceledException) { check(true, "Recycled tile cancels while waiting for a decode permit"); }
        visible.Dispose(); visible.Dispose();
        using var replacement = await limiter.AcquireAsync(true, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        check(true, "Decode permits return once even if disposal repeats");

        // Exercise completion/promotion races: neither lane may leak a permit.
        var racing = new ThumbnailDecodeLimiter(1, 1);
        for (var iteration = 0; iteration < 100; iteration++) {
            var p = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = await racing.AcquireAsync(false, null, CancellationToken.None);
            var moving = racing.AcquireAsync(false, p.Task, CancellationToken.None);
            if (iteration % 2 == 0) { p.SetResult(); held.Dispose(); }
            else { held.Dispose(); p.SetResult(); }
            using var granted = await moving.WaitAsync(TimeSpan.FromSeconds(2));
        }
        using var foregroundAfterRace = await racing.AcquireAsync(true, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        using var aheadAfterRace = await racing.AcquireAsync(false, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        check(true, "Promotion/grant races preserve both lane capacities");

        var stationary = new ThumbnailDecodeLimiter(1, 1);
        var occupying = await stationary.AcquireAsync(false, null, CancellationToken.None);
        var noViewportChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedWithoutInput = stationary.AcquireAsync(false, noViewportChange.Task, CancellationToken.None);
        occupying.Dispose();
        using (await queuedWithoutInput.WaitAsync(TimeSpan.FromSeconds(2)))
            check(true, "Stationary offscreen thumbnails complete without another viewport/input event");

        var canceledPromotion = new ThumbnailDecodeLimiter(1, 1);
        var busyAhead = await canceledPromotion.AcquireAsync(false, null, CancellationToken.None);
        var busyForeground = await canceledPromotion.AcquireAsync(true, null, CancellationToken.None);
        using var recycle = new CancellationTokenSource();
        var recyclingPromotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recycled = canceledPromotion.AcquireAsync(false, recyclingPromotion.Task, recycle.Token);
        recyclingPromotion.SetResult(); recycle.Cancel();
        try { await recycled; check(false, "Recycled promotion must not retain a permit"); }
        catch (OperationCanceledException) { check(true, "Promotion during recycling cancels cleanly"); }
        busyAhead.Dispose(); busyForeground.Dispose();
        using var reuseForeground = await canceledPromotion.AcquireAsync(true, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        using var reuseAhead = await canceledPromotion.AcquireAsync(false, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        check(true, "Canceled promotion leaves both lanes reusable");
    }
}
