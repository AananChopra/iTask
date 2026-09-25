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

        _area.Initialize();
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

    /// <summary>Tells apps where the tray lives, so their popups open next to it (below the top bar).</summary>
    public void SetHostBounds(RECT bounds)
    {
        _area.SetTrayHostSizeData(new TrayHostSizeData
        {
            edge = MsNative.ABEdge.ABE_TOP,
            rc = new MsNative.Rect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom },
        });
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
