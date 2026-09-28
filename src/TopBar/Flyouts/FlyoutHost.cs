using System.Windows;
using iTask.UI;
using iTask.Utilities;
using iTask.WindowsIntegration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.TopBar.Flyouts;

/// <summary>Where a menu opens relative to the item that opened it.</summary>
public enum FlyoutPlacement
{
    /// <summary>Under a top bar item, left edges aligned (macOS menu bar).</summary>
    Below,
    /// <summary>Above a dock icon, centered on it (macOS dock menus).</summary>
    Above,
}

/// <summary>
/// Opens one menu at a time (top bar dropdowns, dock menus), and closes it on an outside click, a
/// click on the same item again, switching apps, or when the menu asks (after acting).
/// Like macOS: menus fade and grow in from their item and fade out; moving from one open menu
/// straight to another switches instantly.
/// </summary>
public sealed class FlyoutHost : IDisposable
{
    private const double Gap = 5; // DIPs between the bar (or dock) and the menu

    private readonly ThemeService _theme;
    private readonly ForegroundWatcher _foreground;
    private readonly MouseDownHook _mouseHook = new();
    private FlyoutWindow? _window;
    private object? _key;
    private RECT _windowRect;
    private RECT _anchorRect;
    private FlyoutPlacement _placement;
    private double _scale = 1;

    public FlyoutHost(ThemeService theme, ForegroundWatcher foreground)
    {
        _theme = theme;
        _foreground = foreground;
        _mouseHook.MouseDown += OnMouseDown;
    }

    public bool IsOpen => _window is not null;

    /// <summary>
    /// Opens <paramref name="content"/> at <paramref name="anchor"/>, or closes it if that menu is already
    /// open. <paramref name="edge"/> is the screen y the menu hangs from (bar bottom) or sits on (dock top).
    /// </summary>
    public void Toggle(object key, FrameworkElement anchor, int edge, Func<FrameworkElement> content, double width,
        FlyoutPlacement placement = FlyoutPlacement.Below)
    {
        _closeOnRelease = false;
        bool wasOpen = Equals(_key, key) && _window is not null;
        bool switching = _window is not null && !wasOpen;
        Close(animate: !switching);
        if (!wasOpen)
            Open(key, anchor, edge, content(), width, placement, animate: !switching);
    }

    private void Open(object key, FrameworkElement anchor, int edge, FrameworkElement content, double width,
        FlyoutPlacement placement, bool animate)
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
        int margin = monitor.ToPixels(6), gap = monitor.ToPixels(Gap);
        int anchorCenter = (_anchorRect.Left + _anchorRect.Right) / 2;
        int left = placement == FlyoutPlacement.Below ? _anchorRect.Left : anchorCenter - w / 2;
        left = Math.Max(Math.Min(left, monitor.Bounds.Right - margin - w), monitor.Bounds.Left + margin);
        int top = placement == FlyoutPlacement.Below ? edge + gap : edge - gap - h;
        _windowRect = new RECT(left, top, left + w, top + h);

        window.SetBounds(_windowRect);
        window.ShowPassive();
        _window = window;
        _key = key;
        _scale = monitor.Scale;
        _placement = placement;

        if (animate && SystemAnimations.Enabled)
        {
            // Grow from the item: its center horizontally, the bar (top) or dock (bottom) vertically.
            double originX = Math.Clamp((anchorCenter - left) / (double)w, 0, 1);
            window.AnimateIn(new Point(originX, placement == FlyoutPlacement.Below ? 0 : 1));
        }

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

    /// <summary>Grows or shrinks the open menu to its content's height, keeping the edge at its item.</summary>
    private void Refit(FlyoutWindow window)
    {
        if (window != _window)
            return;
        int height = (int)Math.Round(window.MeasureContent().Height * _scale);
        if (height == _windowRect.Height)
            return;
        _windowRect = _placement == FlyoutPlacement.Below
            ? new RECT(_windowRect.Left, _windowRect.Top, _windowRect.Right, _windowRect.Top + height)
            : new RECT(_windowRect.Left, _windowRect.Bottom - height, _windowRect.Right, _windowRect.Bottom);
        window.SetBounds(_windowRect);
    }

    public void Close() => Close(animate: true);

    private void Close(bool animate)
    {
        if (_window is null)
            return;
        _mouseHook.Stop();
        _foreground.Changed -= OnForegroundChanged;
        var window = _window;
        _window = null;
        _key = null;
        if (animate && SystemAnimations.Enabled)
            window.AnimateOut(() => CloseWindow(window));
        else
            CloseWindow(window);
    }

    private static void CloseWindow(FlyoutWindow window)
    {
        try { window.Close(); }
        catch (Exception ex) { Log.Error("Closing menu failed", ex); }
    }

    private bool _closeOnRelease;

    private void OnMouseDown(int x, int y)
    {
        // Clicks on the item that opened the menu are left to its own Click (which toggles).
        if (Contains(_windowRect, x, y) || Contains(_anchorRect, x, y))
            return;
        // Pressing somewhere else on our own bars: wait for the release. If it opens another menu,
        // that's an instant switch (like the macOS menu bar); otherwise the menu closes then.
        var hit = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), GA_ROOT);
        if (hit != IntPtr.Zero && WindowUtils.IsOwnWindow(hit))
        {
            _closeOnRelease = true;
            return;
        }
        Close();
    }

    /// <summary>
    /// A bar or the dock got a mouse release. Call it after that release's own click handling has run
    /// (e.g. via a dispatcher callback): if that didn't open another menu, close this one.
    /// </summary>
    public void OnBarMouseReleased()
    {
        if (!_closeOnRelease)
            return;
        _closeOnRelease = false;
        Close();
    }

    private void OnForegroundChanged(object? sender, EventArgs e) => Close();

    private static bool Contains(RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    private static IntPtr PresentationSourceHandle(FrameworkElement element) =>
        (PresentationSource.FromVisual(element) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero;

    public void Dispose()
    {
        Close(animate: false);
        _mouseHook.Dispose();
    }
}
