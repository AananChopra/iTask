using System.Runtime.InteropServices;
using System.Windows.Threading;
using iTask.Utilities;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.WindowsIntegration;

/// <summary>
/// On some Windows 11 builds, "show desktop" (Win+D) hands the windows it restores back as
/// always-on-top. Those then bury every normal window, so apps activated from the dock (or a new
/// window like iTask Settings) open behind them. This undoes exactly that side effect: windows that
/// were not always-on-top when the desktop came to the front, but are once you leave it. Windows
/// someone pinned on purpose (PowerToys Always On Top tags them) are left alone.
/// </summary>
public sealed class ShowDesktopRepair : IDisposable
{
    private const string PowerToysPinnedProp = "AlwaysOnTop_Pinned";
    private static readonly TimeSpan[] CheckDelays = { TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1500) };

    private readonly WinEventDelegate _proc; // keep the delegate alive while hooked
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IntPtr _hook;
    private HashSet<IntPtr>? _normalWhenDesktopShown;

    public ShowDesktopRepair()
    {
        _proc = OnForeground;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnForeground(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || WindowUtils.IsOwnWindow(hwnd))
            return;
        if (WindowUtils.GetClassName(hwnd) is "Progman" or "WorkerW")
        {
            // Desktop in front: remember which windows are ordinary (not always-on-top) right now.
            _normalWhenDesktopShown ??= SnapshotNormalWindows();
            return;
        }
        if (_normalWhenDesktopShown is not { } before)
            return;
        _normalWhenDesktopShown = null;
        // Left the desktop. Restored windows get the flag during their restore, so look twice.
        foreach (var delay in CheckDelays)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Repair(before);
            };
            timer.Start();
        }
    }

    private static HashSet<IntPtr> SnapshotNormalWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsTopmost(hwnd) && !WindowUtils.IsOwnWindow(hwnd))
                set.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    private static void Repair(HashSet<IntPtr> wereNormal)
    {
        foreach (var hwnd in wereNormal)
        {
            if (!IsWindowVisible(hwnd) || !IsTopmost(hwnd) || GetProp(hwnd, PowerToysPinnedProp) != IntPtr.Zero)
                continue;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_NOACTIVATE) != 0)
                continue; // overlays and the like are topmost by design
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Log.Info($"Show desktop left '{WindowUtils.GetClassName(hwnd)}' ({hwnd}) always-on-top; undone.");
        }
    }

    private static bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}
