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

    /// Removes extended window styles.
    public static void RemoveExStyle(Window w, int flags)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        var cur = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(cur & ~(long)flags));
    }

    const uint LWA_ALPHA = 0x2;
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

    /// Whole-window opacity of a layered window (WS_EX_LAYERED), 0–1.
    public static void SetLayeredOpacity(Window w, double opacity)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) SetLayeredWindowAttributes(h, 0, (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), LWA_ALPHA);
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    /// Whether the mouse pointer is over the window (physical pixels, so it works with any DPI).
    public static bool IsCursorOver(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero || !GetCursorPos(out var p) || !GetWindowRect(h, out var r)) return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    /// Whether either Ctrl key is held right now (works while another app has focus).
    public static bool IsCtrlDown() => (GetAsyncKeyState(0x11) & 0x8000) != 0;

    const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

    /// Leaves the window out of screen capture (screen sharing, recordings, screenshots); it stays
    /// visible on the user's own screen. Needs Windows 10 2004 or later; older versions ignore it.
    public static void SetExcludedFromCapture(Window w, bool excluded)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
}
