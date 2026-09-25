using System.Windows.Interop;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.ShellIntegration;

/// <summary>
/// Hidden top-level window that receives shell broadcasts (TaskbarCreated, display/work-area changes).
/// Message-only windows don't receive broadcasts, so this is a real, never-shown popup.
/// </summary>
public sealed class ShellMessageWindow : IDisposable
{
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _source;

    public event EventHandler? TaskbarCreated;
    public event EventHandler? DisplayChanged;
    public event EventHandler? WorkAreaChanged;

    public ShellMessageWindow()
    {
        var parameters = new HwndSourceParameters("iTask.ShellMessages")
        {
            WindowStyle = unchecked((int)WS_POPUP),
            ExtendedWindowStyle = (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarCreatedMessage)
            TaskbarCreated?.Invoke(this, EventArgs.Empty);
        else if (msg == WM_DISPLAYCHANGE)
            DisplayChanged?.Invoke(this, EventArgs.Empty);
        else if (msg == WM_SETTINGCHANGE && wParam.ToInt32() == SPI_SETWORKAREA)
            WorkAreaChanged?.Invoke(this, EventArgs.Empty);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
