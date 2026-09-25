using System.ComponentModel;
using System.Windows.Data;
using iTask.Utilities;
using iTask.WindowsIntegration;
using ManagedShell.AppBar;
using ManagedShell.WindowsTray;
using MsNative = ManagedShell.Interop.NativeMethods;

namespace iTask.ShellIntegration;

/// <summary>
/// Hosts other apps' notification-area icons (via ManagedShell, the tray engine behind RetroBar).
///
/// How it coexists with Explorer: it registers its own "Shell_TrayWnd" window above Explorer's, so
/// Shell_NotifyIcon calls reach us first; everything else (AppBar messages etc.) is forwarded to
/// Explorer untouched. It then broadcasts TaskbarCreated so running apps re-add their icons. On exit
/// the window is destroyed and TaskbarCreated is broadcast again, handing the icons back to Explorer.
/// </summary>
public sealed class TrayHost : IDisposable
{
    // Explorer's built-in system icons; iTask draws its own network/volume/battery indicators.
    private static readonly HashSet<Guid> SystemIconGuids = new()
    {
        new Guid(NotificationArea.NETWORK_GUID),
        new Guid(NotificationArea.POWER_GUID),
        new Guid(NotificationArea.VOLUME_GUID),
        new Guid(NotificationArea.HEALTH_GUID),
    };

    private readonly TrayService _trayService;
    private readonly NotificationArea _area;
    private readonly AppBarManager _appBarForwarder;

    public TrayHost()
    {
        _trayService = new TrayService();
        _area = new NotificationArea(Array.Empty<string>(), _trayService, new ExplorerTrayService());

        // Every SHAppBarMessage call on the system now lands on our Shell_TrayWnd first. Position
        // messages (QUERYPOS/SETPOS) carry shared memory that shell32 allocated for *our* process, so
        // a plain forward leaves Explorer unable to read it and the reservation silently fails —
        // maximized windows would slide under the top bar. AppBarManager re-allocates that memory for
        // Explorer and forwards properly (for our bars and every other app's).
        _appBarForwarder = new AppBarManager(new ExplorerHelper(_area));

        // Before the tray window exists, so nothing can ever read ManagedShell's default ("top").
        var primary = MonitorService.GetPrimary();
        if (primary is not null)
            ReportExplorerTaskbar(primary);

        _area.Initialize();
        if (primary is not null)
            ReportExplorerTaskbar(primary);
        if (_area.IsFailed)
            Log.Warn("Tray host failed to initialize; tray icons will not be shown.");

        var view = new ListCollectionView(_area.TrayIcons) { Filter = IsShown };
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(NotifyIcon.IsHidden));
        Icons = view;
        Log.Info("Tray host started.");
    }

    /// <summary>Visible app tray icons, live-updating.</summary>
    public ICollectionView Icons { get; }

    /// <summary>
    /// Sets the answer to "where is the taskbar?" (ABM_GETTASKBARPOS), which now reaches our
    /// Shell_TrayWnd first and which apps use to place their flyouts. Report where Explorer's taskbar
    /// really is (its window keeps its rect while hidden). The saved StuckRects3 record isn't
    /// reliable for this: on newer Windows 11 builds it can say "bottom" while the taskbar is at the top.
    /// </summary>
    public void ReportExplorerTaskbar(MonitorInfo primary)
    {
        var b = primary.Bounds;
        var edge = MsNative.ABEdge.ABE_BOTTOM;
        int t = primary.ToPixels(48);

        var taskbar = WindowUtils.FindExplorerTaskbar();
        if (taskbar != IntPtr.Zero && NativeMethods.GetWindowRect(taskbar, out var r) && r.Width > 0 && r.Height > 0)
        {
            // The edge it hugs is the one its center is closest to.
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            var distances = new (MsNative.ABEdge edge, int d)[]
            {
                (MsNative.ABEdge.ABE_TOP, Math.Abs(cy - b.Top)),
                (MsNative.ABEdge.ABE_BOTTOM, Math.Abs(b.Bottom - cy)),
                (MsNative.ABEdge.ABE_LEFT, Math.Abs(cx - b.Left)),
                (MsNative.ABEdge.ABE_RIGHT, Math.Abs(b.Right - cx)),
            };
            edge = distances.MinBy(x => x.d).edge;
            int thickness = edge is MsNative.ABEdge.ABE_LEFT or MsNative.ABEdge.ABE_RIGHT ? r.Width : r.Height;
            if (thickness > 0 && thickness < b.Height / 3)
                t = thickness;
        }

        var rc = edge switch
        {
            MsNative.ABEdge.ABE_TOP => new MsNative.Rect { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Top + t },
            MsNative.ABEdge.ABE_LEFT => new MsNative.Rect { Left = b.Left, Top = b.Top, Right = b.Left + t, Bottom = b.Bottom },
            MsNative.ABEdge.ABE_RIGHT => new MsNative.Rect { Left = b.Right - t, Top = b.Top, Right = b.Right, Bottom = b.Bottom },
            _ => new MsNative.Rect { Left = b.Left, Top = b.Bottom - t, Right = b.Right, Bottom = b.Bottom },
        };
        _area.SetTrayHostSizeData(new TrayHostSizeData { edge = edge, rc = rc });
    }

    private static bool IsShown(object item) =>
        item is NotifyIcon icon && !icon.IsHidden && !SystemIconGuids.Contains(icon.GUID);

    public void Dispose()
    {
        _area.Dispose();
        _appBarForwarder.Dispose();
        Log.Info("Tray host stopped.");
    }
}
