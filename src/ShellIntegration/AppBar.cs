using System.Runtime.InteropServices;
using System.Windows.Interop;
using iTask.Utilities;
using iTask.WindowsIntegration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.ShellIntegration;

public enum AppBarEdge : uint
{
    Top = ABE_TOP,
    Bottom = ABE_BOTTOM,
}

/// <summary>
/// Registers a window as a shell application desktop toolbar so Explorer shrinks the work area
/// (maximized windows then stop at our edge instead of sliding underneath).
/// </summary>
public sealed class AppBar : IDisposable
{
    private static readonly uint CallbackMessage = RegisterWindowMessage("iTask.AppBarCallback");

    private readonly HwndSource _source;
    private readonly AppBarEdge _edge;
    private RECT? _lastRect;

    public AppBar(HwndSource source, AppBarEdge edge)
    {
        _source = source;
        _edge = edge;
        _source.AddHook(WndProc);
    }

    public bool IsRegistered { get; private set; }

    /// <summary>Explorer says the work area or other app bars changed; re-reserve.</summary>
    public event EventHandler? PositionChanged;

    /// <summary>A fullscreen app opened (true) or closed (false) on this app bar's monitor.</summary>
    public event EventHandler<bool>? FullScreenChanged;

    public bool Register()
    {
        if (IsRegistered)
            return true;
        var data = NewData();
        data.uCallbackMessage = CallbackMessage;
        IsRegistered = SHAppBarMessage(ABM_NEW, ref data) != UIntPtr.Zero;
        _lastRect = null;
        if (!IsRegistered)
            Log.Warn($"ABM_NEW failed for {_edge} app bar.");
        return IsRegistered;
    }

    /// <summary>Call after Explorer restarts: its app bar list was lost with it.</summary>
    public void ForceReregister()
    {
        Unregister();
        Register();
    }

    public void Unregister()
    {
        if (!IsRegistered)
            return;
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        IsRegistered = false;
        _lastRect = null;
    }

    /// <summary>
    /// Reserves a strip of <paramref name="thickness"/> physical pixels along our edge of the monitor
    /// and returns the rectangle Explorer granted.
    /// </summary>
    public RECT Reserve(RECT monitorBounds, int thickness)
    {
        var desired = Strip(monitorBounds, thickness);
        if (!IsRegistered)
            return desired;

        var data = NewData();
        data.uEdge = (uint)_edge;
        data.rc = desired;
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc = Trim(data.rc, thickness);

        // Skipping identical SETPOS calls avoids ping-pong notifications between our two bars.
        if (_lastRect is { } last && last.Equals(data.rc))
            return data.rc;

        SHAppBarMessage(ABM_SETPOS, ref data);
        data.rc = Trim(data.rc, thickness);
        _lastRect = data.rc;
        return data.rc;
    }

    private RECT Strip(RECT m, int thickness) => _edge == AppBarEdge.Top
        ? new RECT(m.Left, m.Top, m.Right, m.Top + thickness)
        : new RECT(m.Left, m.Bottom - thickness, m.Right, m.Bottom);

    private RECT Trim(RECT r, int thickness) => _edge == AppBarEdge.Top
        ? new RECT(r.Left, r.Top, r.Right, r.Top + thickness)
        : new RECT(r.Left, r.Bottom - thickness, r.Right, r.Bottom);

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _source.Handle };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage && IsRegistered)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    PositionChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case ABN_FULLSCREENAPP:
                    FullScreenChanged?.Invoke(this, lParam != IntPtr.Zero);
                    break;
            }
            handled = true;
        }
        else if (msg == WM_WINDOWPOSCHANGED && IsRegistered)
        {
            var data = NewData();
            SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref data);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }
}
