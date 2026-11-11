using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MeetingAssistant.App.Infrastructure;

/// Win32 helpers for the overlay windows (non-activating, click-through, topmost) and monitor placement.
public static class Native
{
    const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_TOPMOST = 0x8;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    /// Adds extended window styles (call after SourceInitialized).
    public static void AddExStyle(Window w, int flags)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        var cur = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(cur | (long)flags));
    }

    /// Shows the window topmost without activating it (SW_SHOWNOACTIVATE semantics).
    public static void ShowNoActivate(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// Work area (physical pixels) and DPI scale of the monitor under the mouse pointer.
    public static (Rect workArea, double scale) MonitorUnderCursor()
    {
        GetCursorPos(out var pt);
        var mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref info);
        double scale = 1;
        try { if (GetDpiForMonitor(mon, 0, out var dx, out _) == 0) scale = dx / 96.0; } catch (DllNotFoundException) { }
        var r = info.rcWork;
        return (new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), scale);
    }

    /// Moves and sizes a window in physical pixels, topmost, without activating it.
    public static void PlacePhysical(Window w, int x, int y, int width, int height)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(h, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// Flashes the taskbar button (the Mac's attention request).
    public static void Flash(Window w)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(w).Handle,
            dwFlags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG
            uCount = 3,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
}
