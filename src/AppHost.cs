using System.Windows;
using System.Windows.Threading;
using iTask.Configuration;
using iTask.ShellIntegration;
using iTask.SystemInfo;
using iTask.TopBar.Flyouts;
using iTask.UI;
using iTask.Utilities;
using iTask.WindowsIntegration;

namespace iTask;

/// <summary>Application lifecycle: owns services, the taskbar controller and one shell per monitor.</summary>
public sealed class AppHost : IDisposable
{
    private readonly TaskbarController _taskbar = new();
    private readonly List<MonitorShell> _shells = new();
    private readonly DispatcherTimer _displayDebounce;
    private readonly List<IDisposable> _disposables = new();
    private AppSettings _settings = new();
    private ShellServices? _services;
    private ShellMessageWindow? _messages;
    private IntPtr _explorerTaskbar;
    private bool _disposed;

    public AppHost()
    {
        _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _displayDebounce.Tick += (_, _) =>
        {
            _displayDebounce.Stop();
            _services?.Brightness.RefreshAll();
            SyncMonitors();
        };
    }

    public void Start()
    {
        _settings = SettingsStore.Load();

        _messages = new ShellMessageWindow();
        _messages.TaskbarCreated += OnTaskbarCreated;
        _messages.DisplayChanged += (_, _) => _displayDebounce.Start();
        _messages.WorkAreaChanged += (_, _) => _shells.ForEach(s => s.RequestLayout());
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _explorerTaskbar = WindowUtils.FindExplorerTaskbar();
        if (_settings.Shell.HideNativeTaskbar)
            _taskbar.CaptureOriginalState();

        // After capturing the taskbar's state: the tray host registers its own Shell_TrayWnd.
        TrayHost? tray = null;
        if (_settings.TopBar.ShowTrayIcons)
        {
            try { tray = new TrayHost(); }
            catch (Exception ex) { Log.Error("Tray host failed to start", ex); }
        }

        Track(new ShowDesktopRepair());
        var theme = Track(new ThemeService(_settings.Appearance));
        var foreground = Track(new ForegroundWatcher());
        var settings = Track(new SettingsService(_settings, theme));
        settings.Changed += (_, _) => ApplySettings();
        _services = new ShellServices(
            theme,
            Track(new ClockService()),
            Track(new BatteryService()),
            Track(new AudioService()),
            Track(new BrightnessService()),
            Track(new NetworkService()),
            Track(new WifiService()),
            foreground,
            Track(new RunningAppsService()),
            Track(new FlyoutHost(theme, foreground)),
            settings,
            tray);

        SyncMonitors();

        // Hide the Windows taskbar only once our bars have drawn their first frame (they appear
        // over it, as topmost windows), so there's never a moment of bare desktop in between.
        if (_settings.Shell.HideNativeTaskbar)
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (!_disposed)
                    _taskbar.Hide();
            });
        Log.Info("iTask started.");
    }

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    /// <summary>Creates/updates/removes monitor shells to match connected displays.</summary>
    private void SyncMonitors()
    {
        var monitors = MonitorService.GetMonitors();
        Log.Info($"Monitors: {string.Join("; ", monitors.Select(m => $"{m.DeviceName} {m.Bounds} @{m.Scale:0.##}x{(m.IsPrimary ? " primary" : "")}"))}");

        // Every display gets its own top bar and dock.
        var targets = monitors.ToList();

        foreach (var shell in _shells.ToList())
        {
            var match = targets.FirstOrDefault(m => m.DeviceName == shell.Monitor.DeviceName);
            if (match is null)
            {
                shell.Dispose();
                _shells.Remove(shell);
            }
            else
            {
                shell.UpdateMonitor(match);
                targets.Remove(match);
            }
        }

        foreach (var monitor in targets)
        {
            var shell = new MonitorShell(monitor, _settings, _services!);
            shell.ReservationStuck += OnReservationStuck;
            _shells.Add(shell);
            shell.Start();
        }
    }

    /// <summary>
    /// Waking from sleep can rebuild displays and the work area without telling app bars. Check
    /// our reservations again once things have settled (twice: displays can take a while to return).
    /// </summary>
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume)
            return;
        // Raised on a system events thread; the timers must live on the UI thread.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var delay in new[] { 2, 8 })
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (_disposed)
                        return;
                    Log.Info("Resumed from sleep; re-checking layout.");
                    _displayDebounce.Stop();
                    _displayDebounce.Start();
                };
                timer.Start();
            }
        });
    }

    private DateTime _lastTaskbarReset = DateTime.MinValue;

    /// <summary>
    /// Explorer ignores our reserved strips even after re-registering (seen after sleep). Restarting
    /// iTask fixed it, and the part that matters is re-applying the taskbar's auto-hide state, which
    /// makes Explorer rebuild its app bar bookkeeping; do just that, then lay out again.
    /// </summary>
    private void OnReservationStuck(object? sender, EventArgs e)
    {
        if (_disposed || DateTime.UtcNow - _lastTaskbarReset < TimeSpan.FromSeconds(30))
            return;
        _lastTaskbarReset = DateTime.UtcNow;
        Log.Info("Resetting the taskbar's state so Explorer honours our reserved space again.");
        _taskbar.ResetState();
        foreach (var shell in _shells)
            shell.ForceReregister();
    }

    /// <summary>Settings changed: update every display's bars in place.</summary>
    private void ApplySettings()
    {
        if (_disposed)
            return;
        _services!.Flyouts.Close();
        foreach (var shell in _shells)
        {
            try { shell.ApplySettings(); }
            catch (Exception ex) { Log.Error("Applying settings failed", ex); }
        }
        Log.Info("Settings applied.");
    }

    private void OnTaskbarCreated(object? sender, EventArgs e)
    {
        // Our own tray host broadcasts TaskbarCreated too; only a new Explorer taskbar means a restart.
        var current = WindowUtils.FindExplorerTaskbar();
        if (current == _explorerTaskbar)
            return;
        _explorerTaskbar = current;
        Log.Info("Explorer restarted; re-applying.");
        _taskbar.Reapply();
        _shells.ForEach(s => s.OnExplorerRestarted());
    }

    /// <summary>Best-effort restore used from crash handlers; safe to call from any thread, repeatedly.</summary>
    public void EmergencyRestore()
    {
        try { _taskbar.Restore(); }
        catch (Exception ex) { Log.Error("Emergency taskbar restore failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _displayDebounce.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        foreach (var shell in _shells)
        {
            try { shell.Dispose(); }
            catch (Exception ex) { Log.Error("Shell dispose failed", ex); }
        }
        _shells.Clear();

        // Hand tray icons back to Explorer before its taskbar reappears.
        try { _services?.Tray?.Dispose(); }
        catch (Exception ex) { Log.Error("Tray host dispose failed", ex); }
        _taskbar.Restore();

        foreach (var d in Enumerable.Reverse(_disposables))
        {
            try { d.Dispose(); }
            catch (Exception ex) { Log.Error($"{d.GetType().Name} dispose failed", ex); }
        }
        _messages?.Dispose();
        Log.Info("iTask stopped.");
    }
}
