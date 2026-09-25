using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using iTask.Configuration;
using iTask.WindowsIntegration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.UI;

/// <summary>
/// Base for shell surfaces (top bar, dock): borderless, topmost, never activated, hidden from
/// Alt+Tab and the taskbar, positioned in physical pixels by its owner.
/// </summary>
public class OverlayWindow : Window
{
    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
    }

    public IntPtr Handle { get; private set; }

    public HwndSource? Source { get; private set; }

    protected virtual CornerStyle Corners => CornerStyle.Square;

    /// <summary>Which material this surface uses.</summary>
    protected virtual BackdropKind GetBackdrop(ThemeService theme) => BackdropKind.Solid;

    private BackdropKind _backdrop;
    private bool _cloaked;

    /// <summary>Creates the HWND without showing the window, so it can be positioned first.</summary>
    public void EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        Source = HwndSource.FromHwnd(Handle);
        Source.AddHook(WndProc);

        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Stay cloaked (shown to Windows, invisible to the eye) until the first frame is on screen,
        // so startup never flashes an empty or half-painted bar.
        SetCloaked(true);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // One more render pass so the backdrop and first frame are both composed before we appear.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () => SetCloaked(false));
    }

    private void SetCloaked(bool cloaked)
    {
        if (Handle == IntPtr.Zero || _cloaked == cloaked)
            return;
        _cloaked = cloaked;
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_CLOAK, ref value, sizeof(int));
    }

    public virtual void ApplyTheme(ThemeService theme)
    {
        if (Source is null)
            return;
        _backdrop = GetBackdrop(theme);
        Backdrop.Apply(Source, _backdrop, theme.IsDark, Corners, theme.Surface);
        if (_backdrop == BackdropKind.Acrylic)
        {
            // DWM draws system backdrops in their flat "inactive" fallback for windows that aren't
            // activated — and ours never are. Mark only the non-client state active.
            SendMessage(Handle, WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
        }
    }

    private RECT? _bounds;

    /// <summary>Moves/resizes the window (physical pixels) and keeps it in the topmost band.</summary>
    public virtual void SetBounds(RECT r)
    {
        _bounds = r;
        SetWindowPos(Handle, HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, SWP_NOACTIVATE);
    }

    /// <summary>Shows without activating (SW_SHOWNOACTIVATE) — never steals focus.</summary>
    public virtual void ShowPassive()
    {
        if (!IsVisible)
            Show(); // ShowActivated=false → SW_SHOWNA
    }

    protected virtual IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            // Clicking the bar must not pull focus away from the user's app.
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        if (msg == WM_DPICHANGED && _bounds is { } b)
        {
            // Moving onto a display with a different scale makes Windows (and WPF) resize us by the
            // DPI ratio. Our bounds are already exact physical pixels, so put them back afterwards.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, () =>
                SetWindowPos(Handle, HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, SWP_NOACTIVATE));
        }
        if (msg == WM_NCACTIVATE && wParam == IntPtr.Zero && _backdrop == BackdropKind.Acrylic)
        {
            // Stay visually active so the acrylic doesn't drop to its solid fallback.
            handled = true;
            return DefWindowProc(hwnd, WM_NCACTIVATE, new IntPtr(1), lParam);
        }
        return IntPtr.Zero;
    }
}
