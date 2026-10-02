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
    private readonly TopBarAutoHide _topBarAutoHide;
    private AppBar? _topAppBar;
    private AppBar? _dockAppBar;
    private bool _layoutPending;
    private bool _hiddenForFullscreen;
    private DispatcherTimer? _fullscreenWatchdog;

    public MonitorShell(MonitorInfo monitor, AppSettings settings, ShellServices services)
    {
        Monitor = monitor;
        _settings = settings;
        _services = services;
        _topBar = new TopBarWindow(settings.TopBar, services, monitor.DeviceName, glass: settings.Appearance.TopBarBackdrop == BackdropKind.Blur);
        _dock = new DockWindow(settings.Dock, services.RunningApps, services.Flyouts, settings.Appearance.DockGlass);
        _dock.ContentChanged += (_, _) => RequestLayout();
        _dockAutoHide = new DockAutoHide(_dock, services.Foreground, services.Flyouts, monitor);
        _topBarAutoHide = new TopBarAutoHide(_topBar);
    }

    public MonitorInfo Monitor { get; private set; }

    /// <summary>The dock hides at times (smart or auto-hide), so it must not reserve screen space.</summary>
    private bool DockHides => _settings.Dock.Visibility != DockVisibility.AlwaysVisible;

    public void Start()
    {
        _topBar.EnsureHandle();
        _dock.EnsureHandle();
        ApplyTheme();

        _topAppBar = CreateAppBar(_topBar, AppBarEdge.Top);
        // A smart-hiding dock must not reserve space: maximized apps get the full height beneath it.
        if (!DockHides && _settings.Dock.ReserveSpace)
            _dockAppBar = CreateAppBar(_dock, AppBarEdge.Bottom);

        Layout();
        _topBar.ShowPassive();
        _dockAutoHide.AlwaysHide = _settings.Dock.Visibility == DockVisibility.AutoHide;
        _dockAutoHide.IsEnabled = DockHides;

        _topBar.DpiChanged += (_, _) => RequestLayout();
        _dock.DpiChanged += (_, _) => RequestLayout();
        _services.Theme.Changed += OnThemeChanged;
        _services.Foreground.Activated += OnAppActivated;
        _services.Foreground.Changed += OnForegroundChanged;
        _services.RunningApps.AppActivated += OnAppActivated;

        // Safety net for what events miss (a game switching modes, a window resizing itself).
        _fullscreenWatchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenWatchdog.Tick += (_, _) => EvaluateFullscreen();
        _fullscreenWatchdog.Start();
    }

    private void OnAppActivated(object? sender, EventArgs e)
    {
        // Look first: lifting the bars above a full-screen game is exactly what must not happen.
        EvaluateFullscreen();
        if (_hiddenForFullscreen)
            return;
        _topBar.BringToTop();
        _dock.BringToTop();
    }

    private void OnForegroundChanged(object? sender, EventArgs e) => EvaluateFullscreen();

    private AppBar CreateAppBar(OverlayWindow window, AppBarEdge edge)
    {
        var bar = new AppBar(window.Source!, edge);
        bar.Register();
        bar.PositionChanged += (_, _) => RequestLayout();
        bar.FullScreenChanged += (_, _) => EvaluateFullscreen();
        bar.ReservationStuck += (_, _) => ReservationStuck?.Invoke(this, EventArgs.Empty);
        return bar;
    }

    /// <summary>
    /// Settings changed: update the bars in place (no teardown, so nothing blinks).
    /// </summary>
    public void ApplySettings()
    {
        _topBar.ApplySettings();
        _dock.ApplySettings();

        // Dock mode: only an always-visible dock reserves screen space.
        bool reserve = !DockHides && _settings.Dock.ReserveSpace;
        if (reserve && _dockAppBar is null)
        {
            _dockAppBar = CreateAppBar(_dock, AppBarEdge.Bottom);
        }
        else if (!reserve && _dockAppBar is not null)
        {
            _dockAppBar.Dispose();
            _dockAppBar = null;
        }
        _dockAutoHide.AlwaysHide = _settings.Dock.Visibility == DockVisibility.AutoHide;
        _dockAutoHide.IsEnabled = DockHides;

        // Turned "hide over full-screen apps" off while one is up: bring the bars back now.
        EvaluateFullscreen();
        Layout();
    }

    /// <summary>Explorer keeps ignoring this display's reserved strip; the host can reset its state.</summary>
    public event EventHandler? ReservationStuck;

    /// <summary>Explorer restarted: app bar registrations were lost with it.</summary>
    public void OnExplorerRestarted() => ForceReregister();

    /// <summary>Registers our app bars from scratch and reserves their space again.</summary>
    public void ForceReregister()
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
        _topBarAutoHide.SetHome(topRect);
        if (m.IsPrimary)
            _services.Tray?.ReportExplorerTaskbar(m);

        // Dock: centered at the bottom. Its window includes transparent room for magnified icons;
        // the body floats BottomMargin above the window's (and the screen's) bottom edge.
        int reserve = m.ToPixels(_dock.PanelHeight + _settings.Dock.BottomMargin);
        RECT strip = _dockAppBar?.Reserve(m.Bounds, reserve)
                     ?? new RECT(m.Bounds.Left, m.Bounds.Bottom - reserve, m.Bounds.Right, m.Bounds.Bottom);

        _dock.SetAvailableWidth(m.Bounds.Width / m.Scale);
        var size = _dock.GetWindowSize();
        int dockWidth = Math.Min(m.ToPixels(size.Width), m.Bounds.Width);
        int dockHeight = m.ToPixels(size.Height);
        int left = m.Bounds.Left + (m.Bounds.Width - dockWidth) / 2;
        var dockRect = new RECT(left, strip.Bottom - dockHeight, left + dockWidth, strip.Bottom);
        _dockAutoHide.SetHome(dockRect, m);

        Log.Info($"Layout on {m.DeviceName} @{m.Scale:0.##}x: top={topRect} dock={dockRect}");
    }

    /// <summary>
    /// Full-screen apps (games, videos, an app's own full screen mode) get the whole display: both
    /// bars slide away until it leaves full screen. Or they stay on top, if the setting says so.
    /// We judge by the foreground window itself; Explorer's notification is only a nudge to look.
    /// </summary>
    private void EvaluateFullscreen()
    {
        bool hide = _settings.TopBar.HideForFullScreen && WindowUtils.IsFullscreenOn(Monitor.Handle);
        if (hide == _hiddenForFullscreen)
            return;
        _hiddenForFullscreen = hide;
        Log.Info($"{(hide ? "Fullscreen app detected; hiding bars" : "Fullscreen app gone; showing bars")} on {Monitor.DeviceName}.");
        _topBarAutoHide.SetFullScreen(hide);
        _dockAutoHide.SetFullScreen(hide);
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
        _services.Foreground.Activated -= OnAppActivated;
        _services.Foreground.Changed -= OnForegroundChanged;
        _services.RunningApps.AppActivated -= OnAppActivated;
        _fullscreenWatchdog?.Stop();
        _dockAutoHide.Dispose();
        _topBarAutoHide.Dispose();
        _topAppBar?.Dispose();
        _dockAppBar?.Dispose();
        _topBar.Close();
        _dock.Close();
    }
}
