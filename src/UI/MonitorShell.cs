using System.Windows.Threading;
using iTask.Configuration;
using iTask.Dock;
using iTask.ShellIntegration;
using iTask.TopBar;
using iTask.Utilities;
using iTask.WindowsIntegration;

namespace iTask.UI;

/// <summary>
/// The top bar + dock for one monitor, including their app bar reservations and layout.
/// Today only the primary monitor gets one; per-monitor support means creating one per display.
/// </summary>
public sealed class MonitorShell : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ShellServices _services;
    private readonly TopBarWindow _topBar;
    private readonly DockWindow _dock;
    private readonly DockAutoHide _dockAutoHide;
    private AppBar? _topAppBar;
    private AppBar? _dockAppBar;
    private bool _layoutPending;
    private bool _hiddenForFullscreen;

    public MonitorShell(MonitorInfo monitor, AppSettings settings, ShellServices services)
    {
        Monitor = monitor;
        _settings = settings;
        _services = services;
        _topBar = new TopBarWindow(settings.TopBar, services, glass: settings.Appearance.TopBarBackdrop == BackdropKind.Blur);
        _dock = new DockWindow(settings.Dock, services.RunningApps, settings.Appearance.DockGlass);
        _dock.ContentChanged += (_, _) => RequestLayout();
        _dockAutoHide = new DockAutoHide(_dock, services.Foreground, monitor);
    }

    public MonitorInfo Monitor { get; private set; }

    private bool SmartDock => _settings.Dock.Visibility == DockVisibility.Smart;

    public void Start()
    {
        _topBar.EnsureHandle();
        _dock.EnsureHandle();
        ApplyTheme();

        _topAppBar = CreateAppBar(_topBar, AppBarEdge.Top);
        // A smart-hiding dock must not reserve space: maximized apps get the full height beneath it.
        if (!SmartDock && _settings.Dock.ReserveSpace)
            _dockAppBar = CreateAppBar(_dock, AppBarEdge.Bottom);

        Layout();
        _topBar.ShowPassive();
        _dockAutoHide.IsEnabled = SmartDock;

        _topBar.DpiChanged += (_, _) => RequestLayout();
        _dock.DpiChanged += (_, _) => RequestLayout();
        _services.Theme.Changed += OnThemeChanged;
    }

    private AppBar CreateAppBar(OverlayWindow window, AppBarEdge edge)
    {
        var bar = new AppBar(window.Source!, edge);
        bar.Register();
        bar.PositionChanged += (_, _) => RequestLayout();
        bar.FullScreenChanged += (_, fullscreen) => OnFullScreenChanged(fullscreen);
        return bar;
    }

    /// <summary>Explorer restarted: app bar registrations were lost with it.</summary>
    public void OnExplorerRestarted()
    {
        _topAppBar?.ForceReregister();
        _dockAppBar?.ForceReregister();
        Layout();
    }

    public void UpdateMonitor(MonitorInfo monitor)
    {
        Monitor = monitor;
        RequestLayout();
    }

    public void RequestLayout()
    {
        if (_layoutPending)
            return;
        _layoutPending = true;
        _topBar.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _layoutPending = false;
            Layout();
        });
    }

    private void Layout()
    {
        var m = Monitor;

        // Top bar: full-width strip reserved from the work area.
        int topHeight = m.ToPixels(_settings.TopBar.Height);
        RECT topRect;
        var nativeTaskbar = _settings.Shell.HideNativeTaskbar ? WindowUtils.FindExplorerTaskbarAtTop(m) : null;
        if (nativeTaskbar is { } tb)
        {
            // The (hidden) Windows taskbar is docked at the top, and Explorer enforces its full strip
            // there (auto-hide doesn't release it, and it resets any work-area change within a
            // second). So occupy that strip rather than stacking below it: maximized windows start
            // right under the bar, and Windows' flyouts, which anchor to the taskbar, open flush
            // under it. Our app bar stays registered with zero thickness so full-screen
            // notifications keep arriving.
            _topAppBar?.Reserve(m.Bounds, 0);
            topRect = new RECT(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, Math.Max(m.Bounds.Top + topHeight, tb.Bottom));
        }
        else
        {
            topRect = _topAppBar?.Reserve(m.Bounds, topHeight)
                      ?? new RECT(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Top + topHeight);
        }
        _topBar.SetBounds(topRect);
        if (m.IsPrimary)
            _services.Tray?.ReportExplorerTaskbar(m);

        // Dock: centered at the bottom. Its window includes transparent room for magnified icons;
        // the body floats BottomMargin above the window's (and the screen's) bottom edge.
        int reserve = m.ToPixels(_dock.PanelHeight + _settings.Dock.BottomMargin);
        RECT strip = _dockAppBar?.Reserve(m.Bounds, reserve)
                     ?? new RECT(m.Bounds.Left, m.Bounds.Bottom - reserve, m.Bounds.Right, m.Bounds.Bottom);

        var size = _dock.GetWindowSize();
        int dockWidth = Math.Min(m.ToPixels(size.Width), m.Bounds.Width);
        int dockHeight = m.ToPixels(size.Height);
        int left = m.Bounds.Left + (m.Bounds.Width - dockWidth) / 2;
        var dockRect = new RECT(left, strip.Bottom - dockHeight, left + dockWidth, strip.Bottom);
        _dockAutoHide.SetHome(dockRect, m);

        Log.Info($"Layout on {m.DeviceName} @{m.Scale:0.##}x: top={topRect} dock={dockRect}");
    }

    private void OnFullScreenChanged(bool fullscreen)
    {
        // Explorer can send this for the desktop itself; confirm before hiding.
        bool hide = fullscreen && WindowUtils.IsForegroundFullscreen(Monitor.Bounds);
        if (hide == _hiddenForFullscreen)
            return;
        _hiddenForFullscreen = hide;
        Log.Info(hide ? "Fullscreen app detected; hiding bars." : "Fullscreen app gone; showing bars.");
        if (hide)
            _topBar.Hide();
        else
            _topBar.ShowPassive();
        _dockAutoHide.SetSuspended(hide);
        if (!hide)
            Layout();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        _topBar.ApplyTheme(_services.Theme);
        _dock.ApplyTheme(_services.Theme);
    }

    public void Dispose()
    {
        _services.Theme.Changed -= OnThemeChanged;
        _dockAutoHide.Dispose();
        _topAppBar?.Dispose();
        _dockAppBar?.Dispose();
        _topBar.Close();
        _dock.Close();
    }
}
