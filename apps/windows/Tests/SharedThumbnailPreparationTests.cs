using NektronMoments.Models;

internal static class SharedThumbnailPreparationTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var shared = new SharedThumbnailPreparation<object>();
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        Task? promoted = null;
        var bitmap = new object();
        async Task<object?> Prepare(Task promotion) {
            Interlocked.Increment(ref calls);
            promoted = promotion;
            entered.TrySetResult();
            await release.Task;
            return bitmap;
        }
        using var recycled = new CancellationTokenSource();
        var first = shared.GetAsync("epoch1|photo|512", false, Prepare, recycled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var visible = shared.GetAsync("epoch1|photo|512", true, Prepare, CancellationToken.None);
        check(calls == 1 && shared.Count == 1, "Two thumbnail callers share exactly one version/resolution preparation");
        check(promoted!.IsCompleted, "Visible tile promotes the existing background job");
        recycled.Cancel();
        await ExpectCanceled(first, check, "Recycled tile cancels its own wait promptly");
        check(!visible.IsCompleted && shared.Count == 1, "A canceled tile cannot cancel the image another caller needs");
        release.TrySetResult();
        check(ReferenceEquals(bitmap, await visible.WaitAsync(TimeSpan.FromSeconds(2))), "Shared callers receive the same ready bitmap instance");
        check(shared.Count == 0, "Successful preparation leaves no in-flight map entry");

        // The first (and only) tile may disappear before extraction finishes. Its
        // useful image still reaches the cache, instead of restarting on every scroll.
        entered = Signal(); release = Signal();
        var cached = false;
        using var vanished = new CancellationTokenSource();
        var abandoned = shared.GetAsync("photo2|512", false, async _ => {
            entered.TrySetResult(); await release.Task; cached = true; return bitmap;
        }, vanished.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vanished.Cancel();
        await ExpectCanceled(abandoned, check, "Last canceled waiter does not retain its tile");
        release.TrySetResult();
        await UntilAsync(() => shared.Count == 0);
        check(cached, "Work finishes/cache-publishes even after its original tile unloads");

        var retries = 0;
        try {
            await shared.GetAsync("unsupported", false, _ => {
                Interlocked.Increment(ref retries); throw new IOException("fixture failure");
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            check(false, "Fixture exception must reach the observer");
        }
        catch (IOException) { check(shared.Count == 0, "Failed preparations do not leak entries"); }
        check(ReferenceEquals(bitmap, await shared.GetAsync("unsupported", false, _ => {
            Interlocked.Increment(ref retries); return Task.FromResult<object?>(bitmap);
        }, CancellationToken.None)), "A failed thumbnail can be retried successfully");
        check(retries == 2, "No permanent failed-task cache prevents retry");
        check(await shared.GetAsync("missing", false, _ => Task.FromResult<object?>(null), CancellationToken.None) is null && shared.Count == 0,
            "Missing files release their shared map entry too");

        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        try {
            await shared.GetAsync("never", false, _ => throw new Exception("must not start"), alreadyCanceled.Token);
            check(false, "Pre-canceled caller must not enqueue work");
        }
        catch (OperationCanceledException) { check(shared.Count == 0, "Pre-canceled lookahead queues no work"); }

        entered = Signal(); release = Signal();
        var different = new[] { "epoch1|photo|512", "epoch1|photo|1024", "epoch2|photo|512" }
            .Select(key => shared.GetAsync(key, false, async _ => {
                await release.Task; return new object();
            }, CancellationToken.None)).ToArray();
        check(shared.Count == 3, "Different epochs and resolutions have independent preparations");
        release.TrySetResult();
        var results = await Task.WhenAll(different).WaitAsync(TimeSpan.FromSeconds(2));
        check(results.Distinct().Count() == 3 && shared.Count == 0, "All independent preparations complete without map retention");

        var limiter = new ThumbnailDecodeLimiter(1, 1);
        using var occupiedLookahead = await limiter.AcquireAsync(false, null, CancellationToken.None);
        entered = Signal();
        var queued = shared.GetAsync("approaching", false, async promotion => {
            entered.TrySetResult();
            using var slot = await limiter.AcquireAsync(false, promotion, CancellationToken.None);
            return bitmap;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(!queued.IsCompleted, "Background decoding stays bounded when its lane is full");
        shared.Promote("approaching");
        check(ReferenceEquals(bitmap, await queued.WaitAsync(TimeSpan.FromSeconds(2))), "Approaching thumbnail bypasses busy background lane using reserved foreground capacity");
        shared.Promote("already-completed");
        check(shared.Count == 0, "Promotion after completion never creates a phantom job");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ExpectCanceled(Task task, Action<bool, string> check, string message)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); check(false, message); }
        catch (OperationCanceledException) { check(true, message); }
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(1, deadline.Token);
    }
}
