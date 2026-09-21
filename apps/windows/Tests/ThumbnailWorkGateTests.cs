using NektronMoments.Models;
internal static class ThumbnailWorkGateTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var gate = new ThumbnailWorkGate();
        var visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        check(!gate.IsPaused, "Idle background thumbnail work starts normally");
        gate.Hold(true);
        var waiting = gate.WaitAsync(visible.Task);
        check(gate.IsPaused && !waiting.IsCompleted, "Thumb input pauses optional warming");
        visible.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        check(gate.IsPaused, "Visible work bypasses the pause without releasing the user's drag");
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopped = new CancellationTokenSource();
        var canceled = gate.WaitAsync(never.Task, stopped.Token);
        stopped.Cancel();
        try { await canceled; check(false, "Canceled waiting work must stop"); }
        catch (OperationCanceledException) { check(true, "Paused worker waiting is cancellation-safe"); }
        gate.Hold(false);
        check(gate.IsPaused, "A brief quiet period follows interaction");
        await gate.WaitAsync(never.Task).WaitAsync(TimeSpan.FromSeconds(2));
        check(!gate.IsPaused, "Warming resumes once interaction settles");
        gate.Pulse(30);
        check(gate.IsPaused, "Wheel/native view updates also pause optional work");
        await gate.WaitAsync(never.Task).WaitAsync(TimeSpan.FromSeconds(1));
    }
}
