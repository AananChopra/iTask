using System.Windows;
using iTask.UI;
using iTask.Utilities;
using iTask.WindowsIntegration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.TopBar.Flyouts;

/// <summary>
/// Opens one top bar dropdown at a time, under the item that opened it, and closes it on an outside
/// click, a click on the same item again, switching apps, or when the menu asks (after acting).
/// </summary>
public sealed class FlyoutHost : IDisposable
{
    private const double Gap = 5; // DIPs between the bar and the menu

    private readonly ThemeService _theme;
    private readonly ForegroundWatcher _foreground;
    private readonly MouseDownHook _mouseHook = new();
    private FlyoutWindow? _window;
    private object? _key;
    private RECT _windowRect;
    private RECT _anchorRect;

    public FlyoutHost(ThemeService theme, ForegroundWatcher foreground)
    {
        _theme = theme;
        _foreground = foreground;
        _mouseHook.MouseDown += OnMouseDown;
    }

    public bool IsOpen => _window is not null;

    /// <summary>Opens <paramref name="content"/> under <paramref name="anchor"/>, or closes it if that menu is already open.</summary>
    public void Toggle(object key, FrameworkElement anchor, int barBottom, Func<FrameworkElement> content, double width)
    {
        bool wasOpen = Equals(_key, key) && _window is not null;
        Close();
        if (!wasOpen)
            Open(key, anchor, barBottom, content(), width);
    }

    private void Open(object key, FrameworkElement anchor, int barBottom, FrameworkElement content, double width)
    {
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        _anchorRect = new RECT((int)topLeft.X, (int)topLeft.Y, (int)bottomRight.X, (int)bottomRight.Y);

        var hmon = MonitorFromWindow(PresentationSourceHandle(anchor), MONITOR_DEFAULTTONEAREST);
        var monitor = MonitorService.GetMonitors().FirstOrDefault(m => m.Handle == hmon) ?? MonitorService.GetPrimary();
        if (monitor is null)
            return;

        var window = new FlyoutWindow(content, width);
        window.EnsureHandle();
        window.ApplyTheme(_theme);
        var size = window.MeasureContent();

        int w = monitor.ToPixels(size.Width), h = monitor.ToPixels(size.Height);
        int margin = monitor.ToPixels(6);
        // Like macOS: the menu's left edge lines up with the item, shifted left if it would run off screen.
        int left = Math.Min(_anchorRect.Left, monitor.Bounds.Right - margin - w);
        left = Math.Max(left, monitor.Bounds.Left + margin);
        int top = barBottom + monitor.ToPixels(Gap);
        _windowRect = new RECT(left, top, left + w, top + h);

        window.SetBounds(_windowRect);
        window.ShowPassive();
        _window = window;
        _key = key;
        _scale = monitor.Scale;

        // Content often fills in after opening (lists load on Loaded, scans finish later): fit to it.
        window.ContentRendered += (_, _) => Refit(window);
        if (content is IFlyoutContent flyout)
        {
            flyout.CloseRequested += (_, _) => Close();
            flyout.ContentChanged += (_, _) =>
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => Refit(window));
        }
        _mouseHook.Start();
        _foreground.Changed += OnForegroundChanged;
    }

    private double _scale = 1;

    /// <summary>Grows or shrinks the open menu to its content's height (top edge stays put).</summary>
    private void Refit(FlyoutWindow window)
    {
        if (window != _window)
            return;
        int height = (int)Math.Round(window.MeasureContent().Height * _scale);
        if (height == _windowRect.Height)
            return;
        _windowRect = new RECT(_windowRect.Left, _windowRect.Top, _windowRect.Right, _windowRect.Top + height);
        window.SetBounds(_windowRect);
    }

    public void Close()
    {
        if (_window is null)
            return;
        _mouseHook.Stop();
        _foreground.Changed -= OnForegroundChanged;
        var window = _window;
        _window = null;
        _key = null;
        try { window.Close(); }
        catch (Exception ex) { Log.Error("Closing menu failed", ex); }
    }

    private void OnMouseDown(int x, int y)
    {
        // Clicks on the item that opened the menu are left to its own Click (which toggles).
        if (Contains(_windowRect, x, y) || Contains(_anchorRect, x, y))
            return;
        Close();
    }

    private void OnForegroundChanged(object? sender, EventArgs e) => Close();

    private static bool Contains(RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    private static IntPtr PresentationSourceHandle(FrameworkElement element) =>
        (PresentationSource.FromVisual(element) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero;

    public void Dispose()
    {
        Close();
        _mouseHook.Dispose();
    }
}
