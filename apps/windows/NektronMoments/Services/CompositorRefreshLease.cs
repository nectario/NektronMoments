using System.Runtime.InteropServices;
using System.Runtime;

namespace NektronMoments.Services;

/// <summary>Balanced, gesture-scoped DRR boost. Never changes display settings or forces a frame rate.</summary>
public sealed class CompositorRefreshLease : IDisposable
{
    private readonly Func<bool, int> _request;
    public bool IsHeld { get; private set; }
    public int? LastStatus { get; private set; }
    private GCLatencyMode? _previousGcMode;
    public CompositorRefreshLease(Func<bool, int>? request = null) => _request = request ?? NativeRequest;
    public void Begin()
    {
        // Bitmap memory pressure can trigger blocking generation-2 collections
        // during a gesture. Keep collection enabled, but prefer background GC
        // until input settles; restore the app's exact previous policy afterwards.
        if (_previousGcMode is null && GCSettings.LatencyMode is GCLatencyMode.Interactive or GCLatencyMode.Batch) {
            var previous = GCSettings.LatencyMode;
            try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; _previousGcMode = previous; }
            catch (InvalidOperationException) { } // An external no-GC region owns policy.
        }
        if (IsHeld) return;
        LastStatus = _request(true); IsHeld = LastStatus == 0;
    }
    public void End()
    {
        if (_previousGcMode is { } previous) {
            _previousGcMode = null;
            if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency) GCSettings.LatencyMode = previous;
        }
        if (!IsHeld) return;
        IsHeld = false; LastStatus = _request(false);
    }
    public void Dispose() => End();
    private static int NativeRequest(bool enable)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return unchecked((int)0x80004001);
        try { return DCompositionBoostCompositorClock(enable); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return unchecked((int)0x80004001); }
    }
    [DllImport("dcomp.dll")]
    private static extern int DCompositionBoostCompositorClock([MarshalAs(UnmanagedType.Bool)] bool enable);
}
