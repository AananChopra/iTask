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
    /// True when the foreground window covers the whole monitor (games, video players, F11 browsers).
    /// Used to double-check ABN_FULLSCREENAPP, which Explorer occasionally sends for the desktop itself.
    /// </summary>
    public static bool IsForegroundFullscreen(RECT monitorBounds)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow())
            return false;

        var cls = GetClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        if (!GetWindowRect(fg, out var r))
            return false;

        return r.Left <= monitorBounds.Left && r.Top <= monitorBounds.Top &&
               r.Right >= monitorBounds.Right && r.Bottom >= monitorBounds.Bottom;
    }
}
