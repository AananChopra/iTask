using System.Windows;
using System.Windows.Threading;
using iTask.Configuration;
using iTask.ShellIntegration;
using iTask.SystemInfo;
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

        _services = new ShellServices(
            Track(new ThemeService(_settings.Appearance)),
            Track(new ClockService()),
            Track(new BatteryService()),
            Track(new AudioService()),
            Track(new NetworkService()),
            Track(new ForegroundWatcher()),
            Track(new RunningAppsService()),
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
            _shells.Add(shell);
            shell.Start();
        }
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
