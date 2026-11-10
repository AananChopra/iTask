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

    /// <summary>The display turned on (true) or off (false): laptop lid, screen timeout, Modern Standby.</summary>
    public event EventHandler<bool>? DisplayPowerChanged;

    // GUID_CONSOLE_DISPLAY_STATE. Laptops mostly sleep with "Modern Standby", which does not raise
    // SystemEvents.PowerModeChanged(Resume) at all, so this is how a wake is noticed.
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private IntPtr _powerNotification;

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

        var guid = ConsoleDisplayState;
        _powerNotification = RegisterPowerSettingNotification(_source.Handle, ref guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarCreatedMessage)
            TaskbarCreated?.Invoke(this, EventArgs.Empty);
        else if (msg == WM_DISPLAYCHANGE)
            DisplayChanged?.Invoke(this, EventArgs.Empty);
        else if (msg == WM_SETTINGCHANGE && wParam.ToInt32() == SPI_SETWORKAREA)
            WorkAreaChanged?.Invoke(this, EventArgs.Empty);
        else if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; BYTE Data[] }
            var setting = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(lParam);
            if (setting == ConsoleDisplayState)
            {
                int state = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 20); // 0 off, 1 on, 2 dimmed
                DisplayPowerChanged?.Invoke(this, state != 0);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_powerNotification != IntPtr.Zero)
            UnregisterPowerSettingNotification(_powerNotification);
        _powerNotification = IntPtr.Zero;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
