using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Relay.App.Controls;

/// <summary>Small, optional Win32/DWM touches. Every call fails silently on systems that don't support it.</summary>
internal static partial class NativeMethods
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    public static void StyleWindow(Window window, bool dark, bool smallCorners = false)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var d = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
            var corner = smallCorners ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { /* Windows 10 or missing DWM: cosmetic only */ }
    }

    /// <summary>The full bounds (in device-independent pixels) of the monitor showing the window.</summary>
    public static Rect? MonitorBounds(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref info)) return null;
            var source = PresentationSource.FromVisual(window);
            var m = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var tl = m.Transform(new Point(info.rcMonitor.Left, info.rcMonitor.Top));
            var br = m.Transform(new Point(info.rcMonitor.Right, info.rcMonitor.Bottom));
            return new Rect(tl, br);
        }
        catch { return null; }
    }
}
