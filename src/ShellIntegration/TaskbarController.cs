using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using iTask.Utilities;
using iTask.WindowsIntegration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.ShellIntegration;

/// <summary>
/// Hides the native taskbar without replacing Explorer:
///  1. Switches the taskbar to auto-hide (so the work area no longer reserves its space).
///  2. Hides the Shell_TrayWnd window so the auto-hide strip can't pop up.
///  3. Watches for Explorer re-showing it and hides it again.
/// The original auto-hide state is persisted to disk *before* anything changes, so a crash or
/// a forced kill can always be undone (next launch, or `iTask.exe --restore-taskbar`).
/// </summary>
public sealed class TaskbarController : IDisposable
{
    private const string PrimaryTrayClass = "Shell_TrayWnd";
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";

    private sealed record RecoveryState(int OriginalState);

    private readonly WinEventDelegate _hookProc; // keep the delegate alive while hooked
    private IntPtr _hook;
    private int _originalState;
    private bool _active;

    public TaskbarController()
    {
        _hookProc = OnWindowShown;
    }

    public bool IsActive => _active;

    public void Hide()
    {
        if (WindowUtils.FindExplorerTaskbar() == IntPtr.Zero)
        {
            Log.Warn("Shell_TrayWnd not found (Explorer not running?). Will retry on TaskbarCreated.");
        }

        // If a previous session died without restoring, the file holds the true original state.
        var previous = LoadRecoveryState();
        _originalState = previous?.OriginalState ?? GetState();
        SaveRecoveryState(new RecoveryState(_originalState));

        _active = true;
        Apply();
        Log.Info($"Native taskbar hidden (original state=0x{_originalState:X}).");
    }

    /// <summary>Re-applies hiding after Explorer restarts (TaskbarCreated).</summary>
    public void Reapply()
    {
        if (_active)
            Apply();
    }

    public void Restore()
    {
        if (!_active)
            return;
        _active = false;

        Unhook();
        foreach (var tray in FindTrayWindows())
        {
            ShowWindow(tray, SW_SHOWNA);
            // The tray host pushes Explorer's taskbar to the bottom of the z-order; put it back on top.
            SetWindowPos(tray, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        SetState(_originalState);
        DeleteRecoveryState();
        Log.Info("Native taskbar restored.");
    }

    public void Dispose() => Restore();

    /// <summary>Undo a previous session that could not clean up (crash / kill).</summary>
    public static void RecoverFromPreviousSession()
    {
        var state = LoadRecoveryState();
        foreach (var tray in FindTrayWindows())
        {
            ShowWindow(tray, SW_SHOWNA);
            SetWindowPos(tray, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        if (state is not null)
        {
            SetState(state.OriginalState);
            DeleteRecoveryState();
            Log.Info($"Recovered taskbar from previous session (state=0x{state.OriginalState:X}).");
        }
    }

    private void Apply()
    {
        SetState(_originalState | ABS_AUTOHIDE);
        foreach (var tray in FindTrayWindows())
        {
            // Only the primary taskbar is hidden outright: secondary monitors keep an auto-hide
            // taskbar until iTask draws its own UI there.
            if (WindowUtils.GetClassName(tray) == PrimaryTrayClass)
                ShowWindow(tray, SW_HIDE);
        }
        Hook();
    }

    private void Hook()
    {
        Unhook();
        var tray = WindowUtils.FindExplorerTaskbar();
        if (tray == IntPtr.Zero)
            return;
        GetWindowThreadProcessId(tray, out uint explorerPid);
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _hookProc,
            explorerPid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private void Unhook()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void OnWindowShown(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_active || idObject != OBJID_WINDOW || hwnd == IntPtr.Zero)
            return;
        if (WindowUtils.GetClassName(hwnd) == PrimaryTrayClass)
            ShowWindow(hwnd, SW_HIDE);
    }

    private static IEnumerable<IntPtr> FindTrayWindows()
    {
        var primary = WindowUtils.FindExplorerTaskbar();
        if (primary != IntPtr.Zero)
            yield return primary;

        var secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, SecondaryTrayClass, null)) != IntPtr.Zero)
            yield return secondary;
    }

    private static int GetState()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = WindowUtils.FindExplorerTaskbar() };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref data).ToUInt32();
    }

    private static void SetState(int state)
    {
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = WindowUtils.FindExplorerTaskbar(),
            lParam = (IntPtr)state,
        };
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    private static RecoveryState? LoadRecoveryState()
    {
        try
        {
            return File.Exists(AppPaths.TaskbarStateFile)
                ? JsonSerializer.Deserialize<RecoveryState>(File.ReadAllText(AppPaths.TaskbarStateFile))
                : null;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read taskbar recovery state", ex);
            return null;
        }
    }

    private static void SaveRecoveryState(RecoveryState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalDirectory);
            File.WriteAllText(AppPaths.TaskbarStateFile, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write taskbar recovery state", ex);
        }
    }

    private static void DeleteRecoveryState()
    {
        try { File.Delete(AppPaths.TaskbarStateFile); }
        catch (Exception ex) { Log.Error("Could not delete taskbar recovery state", ex); }
    }
}
