using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace iTask.WindowsIntegration;

/// <summary>
/// Reports mouse-button presses anywhere on screen (low-level hook) while started. Used to close
/// dropdowns on an outside click: our windows never activate, so they never get "deactivated".
/// Only installed while something needs it; the callback just posts and returns immediately.
/// </summary>
public sealed class MouseDownHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private readonly HookProc _proc; // keep alive while hooked
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IntPtr _hook;

    public MouseDownHook() => _proc = OnHook;

    /// <summary>Screen point (physical px) of a button press.</summary>
    public event Action<int, int>? MouseDown;

    public void Start()
    {
        if (_hook == IntPtr.Zero)
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        int message = wParam.ToInt32();
        if (code >= 0 && message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4); // MSLLHOOKSTRUCT.pt
            _dispatcher.BeginInvoke(() => MouseDown?.Invoke(x, y));
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
