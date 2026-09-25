using System.Windows.Interop;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.WindowsIntegration;

/// <summary>A hidden, never-shown top-level window for receiving system notifications.</summary>
public sealed class MessageWindow : IDisposable
{
    public delegate void MessageHandler(int msg, IntPtr wParam, IntPtr lParam);

    private readonly HwndSource _source;

    public MessageWindow(string name)
    {
        _source = new HwndSource(new HwndSourceParameters(name)
        {
            WindowStyle = unchecked((int)WS_POPUP),
            ExtendedWindowStyle = (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    public event MessageHandler? MessageReceived;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        MessageReceived?.Invoke(msg, wParam, lParam);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
