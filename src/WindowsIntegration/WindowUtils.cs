using System.Text;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.WindowsIntegration;

public static class WindowUtils
{
    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// Explorer's own taskbar. Not simply FindWindow("Shell_TrayWnd"): our tray host registers a
    /// window of that class too (so apps send their tray icons to us), and it sits above Explorer's.
    /// </summary>
    public static IntPtr FindExplorerTaskbar()
    {
        int self = Environment.ProcessId;
        var hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, "Shell_TrayWnd", null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != self)
                return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// If one of Explorer's taskbars (primary or secondary) is docked along the top of this display,
    /// its rectangle. It keeps that strip reserved even while we hide it.
    /// </summary>
    public static RECT? FindExplorerTaskbarAtTop(MonitorInfo monitor)
    {
        var m = monitor.Bounds;
        int self = Environment.ProcessId;
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var hwnd = IntPtr.Zero;
            while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, cls, null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == self || !GetWindowRect(hwnd, out var r))
                    continue;
                bool spansWidth = r.Left <= m.Left + 2 && r.Right >= m.Right - 2;
                bool atTop = r.Top <= m.Top + 2 && r.Bottom > m.Top && r.Height < m.Height / 4;
                if (spansWidth && atTop)
                    return r;
            }
        }
        return null;
    }

    public static bool IsOwnWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>
    /// True when the foreground window is a full-screen app on this monitor: it covers the whole
    /// display (games, video players, F11 browsers). Explorer's own notification for this is not
    /// enough: it never comes for a game running in a framed window that is merely as big as the
    /// screen (or bigger), and it can be late or wrong, so we look at the window ourselves.
    /// Ordinary maximized windows don't count, even though their frame overhangs the display.
    /// </summary>
    public static bool IsFullscreenOn(IntPtr monitor)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow() || IsOwnWindow(fg))
            return false;
        if (!IsWindowVisible(fg) || IsIconic(fg))
            return false;
        if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != monitor)
            return false;

        var cls = GetClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ||
            cls == "#32770" || // dialog boxes (open/save, message boxes)
            ForegroundWatcher.IsTransientClass(cls)) // Start, Task View, Alt+Tab, menus
            return false;
        // Dialogs and pickers belong to an app window; a game's window belongs to nobody.
        if (GetWindow(fg, GW_OWNER) != IntPtr.Zero)
            return false;

        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(fg, out var r))
            return false;
        var bounds = info.rcMonitor;
        bool covers = r.Left <= bounds.Left && r.Top <= bounds.Top && r.Right >= bounds.Right && r.Bottom >= bounds.Bottom;
        if (!covers)
            return false;

        // A maximized window with a title bar is just a maximized window.
        long style = GetWindowLongPtr(fg, GWL_STYLE).ToInt64();
        if (IsZoomed(fg) && (style & WS_CAPTION) == WS_CAPTION)
            return false;

        // The Windows file picker (and similar helpers) start out as big as the screen for a moment.
        return !IsHelperProcess(fg);
    }

    /// <summary>The foreground window in a form for logs: process, title, class, rectangle.</summary>
    public static string DescribeForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return "(none)";
        GetWindowThreadProcessId(fg, out uint pid);
        string name = "?";
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
        var title = new StringBuilder(80);
        NativeMethods.GetWindowText(fg, title, title.Capacity);
        string rect = GetWindowRect(fg, out var r) ? r.ToString() : "?";
        return $"{name} '{title}' [{GetClassName(fg)}] {rect}";
    }

    private static readonly string[] HelperProcesses = { "pickerhost" };

    /// <summary>Windows' own helper hosts (the file picker) whose windows are not apps.</summary>
    public static bool IsHelperProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return HelperProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // exited, or not ours to look at
        }
    }
}
