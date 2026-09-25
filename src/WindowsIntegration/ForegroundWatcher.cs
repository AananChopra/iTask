using System.Windows.Threading;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.WindowsIntegration;

/// <summary>What is in front on a display, as far as the dock cares.</summary>
public enum ForegroundKind
{
    /// <summary>The desktop, or nothing (all windows minimized).</summary>
    Desktop,
    /// <summary>A normal, non-maximized application window.</summary>
    Windowed,
    /// <summary>A maximized or full-screen application window.</summary>
    Maximized,
}

/// <summary>
/// Raises <see cref="Changed"/> when the foreground window changes or is maximized/restored/minimized.
/// Event-driven via WinEvent hooks; the location hook is scoped to the foreground process only,
/// so we are not woken for every window move on the system.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private static readonly HashSet<string> TransientClasses = new(StringComparer.Ordinal)
    {
        "Windows.UI.Core.CoreWindow",          // Start, Search, Notification Center, Quick Settings
        "XamlExplorerHostIslandWindow",        // Alt+Tab, Task View, Win11 flyouts
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "TopLevelWindowForOverflowXamlIsland", // tray overflow
        "NotifyIconOverflowWindow",
        "#32768",                              // popup menus
        "Shell_TrayWnd",                       // our tray host / Explorer's hidden taskbar
    };

    private static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
    };

    private readonly Dispatcher _dispatcher;
    private readonly WinEventDelegate _foregroundProc;
    private readonly WinEventDelegate _locationProc;
    private IntPtr _foregroundHook;
    private IntPtr _minimizeHook;
    private IntPtr _locationHook;
    private uint _locationPid;
    private IntPtr _foreground;
    private bool _pending;

    public ForegroundWatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _foregroundProc = OnForegroundEvent;
        _locationProc = OnLocationEvent;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _minimizeHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        TrackForeground(GetForegroundWindow());
    }

    public event EventHandler? Changed;

    /// <summary>
    /// Classifies what is in front on one display: the front-most real app window there, by z-order.
    /// Per display rather than "the focused app", so each dock reacts to its own screen — a
    /// maximized window on display 2 hides dock 2 even while you're typing on display 1.
    /// </summary>
    public static ForegroundKind Classify(IntPtr monitor, RECT monitorBounds)
    {
        var front = FrontMostAppWindow(monitor);
        if (front == IntPtr.Zero)
            return ForegroundKind.Desktop;
        if (IsZoomed(front))
            return ForegroundKind.Maximized;
        if (GetWindowRect(front, out var r) && r.Left <= monitorBounds.Left && r.Top <= monitorBounds.Top &&
            r.Right >= monitorBounds.Right && r.Bottom >= monitorBounds.Bottom)
            return ForegroundKind.Maximized; // borderless full-screen
        return ForegroundKind.Windowed;
    }

    private static IntPtr FrontMostAppWindow(IntPtr monitor)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsAppWindow(hwnd) || MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != monitor)
                return true;
            found = hwnd;
            return false; // EnumWindows walks top to bottom, so the first match is the front-most
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>A visible, on-this-desktop application window (not a shell surface, overlay or tool window).</summary>
    private static bool IsAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOPMOST) != 0 || (ex & WS_EX_NOACTIVATE) != 0)
            return false; // overlays, widgets, our own bars
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0)
            return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false; // other virtual desktops, suspended UWP frames
        var cls = WindowUtils.GetClassName(hwnd);
        if (DesktopClasses.Contains(cls) || TransientClasses.Contains(cls) || WindowUtils.IsOwnWindow(hwnd))
            return false;
        return GetWindowRect(hwnd, out var r) && r.Width > 1 && r.Height > 1;
    }

    private void OnForegroundEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == EVENT_SYSTEM_FOREGROUND)
            TrackForeground(hwnd);
        RaiseChanged();
    }

    private void OnLocationEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Maximize / restore / snap of the foreground window arrive as location changes.
        if (idObject == OBJID_WINDOW && idChild == 0 && hwnd == _foreground)
            RaiseChanged();
    }

    private void TrackForeground(IntPtr hwnd)
    {
        _foreground = hwnd;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _locationPid && _locationHook != IntPtr.Zero)
            return;

        if (_locationHook != IntPtr.Zero)
            UnhookWinEvent(_locationHook);
        _locationHook = IntPtr.Zero;
        _locationPid = pid;
        if (pid != 0)
        {
            _locationHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
                _locationProc, pid, 0, WINEVENT_OUTOFCONTEXT);
        }
    }

    private void RaiseChanged()
    {
        // Coalesce bursts (a maximize animation produces many location events).
        if (_pending)
            return;
        _pending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _pending = false;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Dispose()
    {
        foreach (var hook in new[] { _foregroundHook, _minimizeHook, _locationHook })
        {
            if (hook != IntPtr.Zero)
                UnhookWinEvent(hook);
        }
        _foregroundHook = _minimizeHook = _locationHook = IntPtr.Zero;
    }
}
