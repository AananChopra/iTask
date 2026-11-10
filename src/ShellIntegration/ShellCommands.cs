using System.Diagnostics;
using System.Runtime.InteropServices;
using iTask.Utilities;
using iTask.WindowsIntegration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.ShellIntegration;

/// <summary>Entry points into existing Windows shell UI (we never re-implement these).</summary>
public static class ShellCommands
{
    private const int SC_TASKLIST = 0xF130; // what the taskbar's own Start button sends

    /// <summary>
    /// Opens (or, if it is open, closes) the Start menu. A synthesized Windows key tap normally does
    /// it, but after the laptop wakes up the first one can be ignored until a real key press. So we
    /// check that Start really appeared and, if it didn't, try the other routes, logging which one
    /// worked.
    /// </summary>
    public static void OpenStartMenu() => _ = OpenStartMenuAsync();

    private static bool _starting;

    private static async Task OpenStartMenuAsync()
    {
        if (_starting)
            return;
        _starting = true;
        try
        {
            var before = GetForegroundWindow();
            // Already open: this click is meant to close it. One tap, no checking.
            if (IsStartSurface(before))
            {
                InputSender.SendChord(InputSender.VK_LWIN);
                return;
            }

            bool sent = InputSender.SendChord(InputSender.VK_LWIN);
            if (await StartAppeared(before, 700))
                return;

            Log.Warn($"Start did not open after the Windows key tap (SendInput {(sent ? "ok" : "failed")}). In front: {WindowUtils.DescribeForeground()}");

            // Second route: the message the real taskbar's Start button sends to Explorer.
            var taskbar = WindowUtils.FindExplorerTaskbar();
            if (taskbar != IntPtr.Zero)
            {
                PostMessage(taskbar, WM_SYSCOMMAND, new IntPtr(SC_TASKLIST), IntPtr.Zero);
                if (await StartAppeared(before, 700))
                {
                    Log.Info("Start opened via the taskbar message instead.");
                    return;
                }
            }

            // Third: the key tap again (the first press may have only woken Start up).
            InputSender.SendChord(InputSender.VK_LWIN);
            Log.Info(await StartAppeared(before, 900)
                ? "Start opened on the second Windows key tap."
                : "Start still did not open after three attempts.");
        }
        catch (Exception ex)
        {
            Log.Error("Opening Start failed", ex);
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>Waits up to <paramref name="ms"/> for Start (or its search page) to take the foreground.</summary>
    private static async Task<bool> StartAppeared(IntPtr before, int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            await Task.Delay(40);
            var fg = GetForegroundWindow();
            if (fg != before && IsStartSurface(fg))
                return true;
            if (IsLauncherVisible())
                return true;
        }
        return false;
    }

    private static readonly string[] StartHosts = { "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost" };

    /// <summary>True for the Start menu / search window (the foreground window while Start is open).</summary>
    private static bool IsStartSurface(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (StartHosts.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                return true;
            // Newer builds host Start's XAML in Explorer itself.
            return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                   WindowUtils.GetClassName(hwnd).StartsWith("XamlExplorerHostIslandWindow", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // ── IAppVisibility: the shell's own answer to "is the Start launcher showing?" ───────────

    [ComImport, Guid("2246EA2D-CAEA-4444-A3C4-6DE827E44313"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAppVisibility
    {
        [PreserveSig] int GetAppVisibilityOnMonitor(IntPtr monitor, out int mode);
        [PreserveSig] int IsLauncherVisible([MarshalAs(UnmanagedType.Bool)] out bool visible);
    }

    private static IAppVisibility? _appVisibility;

    private static bool IsLauncherVisible()
    {
        try
        {
            _appVisibility ??= (IAppVisibility?)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("7E5FE3D9-985F-4908-91F9-EE19F9FD1514"))!);
            return _appVisibility is not null && _appVisibility.IsLauncherVisible(out bool visible) == 0 && visible;
        }
        catch
        {
            _appVisibility = null; // the shell restarted; ask for a fresh one next time
            return false;
        }
    }

    public static void OpenTaskManager() => Launch("taskmgr.exe");

    public static void OpenSettings() => Launch("ms-settings:");

    public static void Launch(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { Arguments = arguments ?? "", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to launch '{target}'", ex);
        }
    }
}
