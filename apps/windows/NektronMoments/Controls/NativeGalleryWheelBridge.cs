using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace NektronMoments.Controls;

/// <summary>Route the app's native mouse-wheel messages before WinUI's built-in
/// ScrollViewer handling, including wheel input over its scrollbar and gutter.</summary>
public sealed class NativeGalleryWheelBridge : IDisposable
{
    private const uint MouseWheel = 0x020A;
    private readonly nint _window;
    private readonly UIElement _gallery, _root;
    private readonly Func<bool> _enabled;
    private readonly Action<int> _wheel;
    private readonly SubclassProc _callback;
    private readonly List<nint> _handles = [];
    private bool _disposed;
    public int RegisteredWindows => _handles.Count;
    public int NativeMessagesHandled { get; private set; }

    public NativeGalleryWheelBridge(Window window, UIElement gallery, Func<bool> enabled, Action<int> wheel)
    {
        _window = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _root = (UIElement)window.Content; _gallery = gallery; _enabled = enabled; _wheel = wheel;
        _callback = Dispatch;
        Register(_window);
        EnumChildWindows(_window, (child, _) => { Register(child); return true; }, 0);
    }
    private void Register(nint window)
    {
        if (GetWindowThreadProcessId(window, out var process) != GetCurrentThreadId() || process != Environment.ProcessId) return;
        if (SetWindowSubclass(window, _callback, 0x4E4D5748, 0)) _handles.Add(window);
    }
    public Rect GalleryScreenBounds()
    {
        if (_gallery is not FrameworkElement element || element.XamlRoot is null) return default;
        var point = element.TransformToVisual(_root).TransformPoint(new Point());
        var origin = new NativePoint(); ClientToScreen(_window, ref origin);
        var scale = element.XamlRoot.RasterizationScale;
        return new Rect(origin.X + point.X * scale, origin.Y + point.Y * scale,
            element.ActualWidth * scale, element.ActualHeight * scale);
    }
    // The release regression checks this same native-message routing decision
    // with points over photos AND over the scrollbar; it does not inject OS input.
    public bool RouteWheel(int delta, Point screenPoint, bool control = false, bool shift = false)
    {
        if (_disposed || !_enabled() || control || shift || delta == 0 || !GalleryScreenBounds().Contains(screenPoint)) return false;
        _wheel(delta); return true;
    }
    private nint Dispatch(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try {
            if (message == MouseWheel) {
                var delta = unchecked((short)((ulong)wParam >> 16));
                var packed = (long)lParam;
                var point = new Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
                if (RouteWheel(delta, point, (wParam & 8) != 0, (wParam & 4) != 0)) {
                    NativeMessagesHandled++;
                    return 0; // Native processing must not add a second scroll/page jump.
                }
            }
        } catch { /* Never unwind a managed exception through a window procedure. */ }
        return DefSubclassProc(window, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _handles) RemoveWindowSubclass(window, _callback, 0x4E4D5748);
        _handles.Clear();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    private delegate bool EnumProc(nint window, nint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
