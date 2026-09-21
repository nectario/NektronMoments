using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace NektronMoments.Services;

[EventSource(Name = "Nektron-Moments-Diagnostics")]
public sealed class MomentsDiagnosticEvents : EventSource
{
    public static readonly MomentsDiagnosticEvents Log = new();
    private MomentsDiagnosticEvents() : base(EventSourceSettings.EtwSelfDescribingEventFormat) { }
    [Event(1, Level = EventLevel.Informational)]
    public void Marker(string phase, int value) { if (IsEnabled()) WriteEvent(1, phase, value); }
    [Event(2, Level = EventLevel.Informational)]
    public void Work(long id, long kind, long phase) { if (IsEnabled()) WriteEvent(2, id, kind, phase); }
    [Event(3, Level = EventLevel.Informational)]
    public void Thumb(string phase, double offset) { if (IsEnabled()) WriteEvent(3, phase, offset); }
    [Event(4, Level = EventLevel.Informational)]
    public void Frame(int phase, double value) { if (IsEnabled()) WriteEvent(4, phase, value); }
}

public static class DiagnosticTrace
{
    public static readonly string? Output = Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_DIAGNOSTIC_DIR");
    public static bool Enabled => !string.IsNullOrWhiteSpace(Output);
    private static long _workId;
    public static long Start(int kind) {
        if (!Enabled) return 0;
        var id = Interlocked.Increment(ref _workId); MomentsDiagnosticEvents.Log.Work(id, kind, 0); return id;
    }
    public static void Stop(long id, int kind) { if (id != 0) MomentsDiagnosticEvents.Log.Work(id, kind, 1); }
    public static void Marker(string phase, int value = 0) { if (Enabled) MomentsDiagnosticEvents.Log.Marker(phase, value); }
    public static void Thumb(string phase, double offset) { if (Enabled) MomentsDiagnosticEvents.Log.Thumb(phase, offset); }

    public static async Task WaitForCollectorAsync()
    {
        if (!Enabled) return;
        if (!Path.IsPathFullyQualified(Output!)) throw new ArgumentException("Diagnostic directory must be absolute.");
        Directory.CreateDirectory(Output!);
        _ = MomentsDiagnosticEvents.Log;
        await File.WriteAllTextAsync(Path.Combine(Output!, "app-ready.json"),
            System.Text.Json.JsonSerializer.Serialize(new { processId = Environment.ProcessId, uiThreadId = GetCurrentThreadId() }));
        if (Environment.GetEnvironmentVariable("NEKTRON_MOMENTS_TRACE_GATE") == "1") {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(Output!, "collector-ready.flag")) && watch.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(25);
        }
        Marker("WindowStarting", (int)GetCurrentThreadId());
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public static void AttachFrames(Microsoft.UI.Xaml.Window window)
    {
        if (!Enabled) return;
        void Rendered(object? sender, Microsoft.UI.Xaml.Media.RenderedEventArgs args) =>
            MomentsDiagnosticEvents.Log.Frame(1, args.FrameDuration.TotalMilliseconds);
        // Observe completed frames only. A Rendering subscription would request
        // extra idle frames and distort the startup/idle control measurement.
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += Rendered;
        window.Closed += (_, _) => {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= Rendered;
        };
    }
}
