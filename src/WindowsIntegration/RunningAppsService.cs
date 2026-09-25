using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using iTask.Utilities;
using ManagedShell.Common.Enums;
using ManagedShell.WindowsTasks;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.WindowsIntegration;

/// <summary>One running application: all of its taskbar-worthy windows grouped together.</summary>
public sealed class RunningApp : INotifyPropertyChanged
{
    internal RunningApp(string key) => Key = key;

    public string Key { get; }
    public string Name { get; private set; } = "";
    /// <summary>The window's own icon (small; used in the top bar).</summary>
    public ImageSource? Icon { get; private set; }
    /// <summary>256 px shell icon for the dock, falling back to the window icon.</summary>
    public ImageSource? LargeIcon => _largeIcon ?? Icon;
    private ImageSource? _largeIcon;
    private string? _largeIconKey;
    public bool IsActive { get; private set; }
    public bool IsFlashing { get; private set; }
    public IReadOnlyList<ApplicationWindow> Windows { get; private set; } = Array.Empty<ApplicationWindow>();

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(List<ApplicationWindow> windows)
    {
        Windows = windows;
        var first = windows[0];
        Name = !string.IsNullOrWhiteSpace(first.WinFileDescription) && !first.IsUWP
            ? first.WinFileDescription
            : !string.IsNullOrWhiteSpace(first.Title) ? first.Title
            : Path.GetFileNameWithoutExtension(first.WinFileName ?? "");
        Icon = windows.Select(w => w.Icon).FirstOrDefault(i => i is not null);

        // AppUserModelID arrives lazily, so re-resolve the large icon whenever its inputs change.
        var iconKey = $"{first.AppUserModelID}|{first.WinFileName}";
        if (iconKey != _largeIconKey)
        {
            _largeIconKey = iconKey;
            _largeIcon = AppIconProvider.Get(first.AppUserModelID, first.WinFileName, first.ProcId);
        }
        IsActive = windows.Any(w => w.State == ApplicationWindow.WindowState.Active);
        IsFlashing = windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    /// <summary>
    /// Taskbar semantics: activate the app's front-most window; if the app is already active,
    /// minimize that window instead.
    /// </summary>
    public void Toggle()
    {
        // Our bars never take focus, so the real foreground window is still the user's app.
        var foreground = GetForegroundWindow();
        var active = Windows.FirstOrDefault(w => w.Handle == foreground);
        if (active is not null && !IsIconic(active.Handle) && active.CanMinimize)
        {
            active.Minimize();
            Log.Info($"Toggle {Name}: minimized '{active.Title}'");
            return;
        }
        var target = FrontMost(Windows.Where(w => !IsIconic(w.Handle))) ?? FrontMost(Windows);
        if (target is null)
            return;
        bool ok = Activate(target.Handle);
        Log.Info($"Toggle {Name}: activated '{target.Title}' ({(ok ? "ok" : "refused")})");
    }

    /// <summary>Restores if minimized and brings the window to the foreground.</summary>
    private static bool Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd)
            return true;
        // Foreground lock refused us: a synthetic Alt tap lets the next SetForegroundWindow through.
        InputSender.SendChord(InputSender.VK_MENU);
        SetForegroundWindow(hwnd);
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>The window highest in the z-order (EnumWindows enumerates top to bottom).</summary>
    private static ApplicationWindow? FrontMost(IEnumerable<ApplicationWindow> candidates)
    {
        var set = candidates.ToDictionary(w => w.Handle);
        if (set.Count <= 1)
            return set.Values.FirstOrDefault();
        ApplicationWindow? found = null;
        EnumWindows((hwnd, _) =>
        {
            if (set.TryGetValue(hwnd, out var w))
            {
                found = w;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found ?? set.Values.First();
    }
}

/// <summary>
/// Running apps as the taskbar sees them (via ManagedShell's shell-hook based task tracking, which
/// applies Explorer's rules: visible, un-owned, not tool windows, not cloaked, UWP-aware icons).
/// Windows are grouped per app (AppUserModelID, else executable path), in launch order.
/// </summary>
public sealed class RunningAppsService : IDisposable
{
    private readonly TasksService _tasksService;
    private readonly Tasks _tasks;
    private readonly ICollectionView _windows;
    private readonly HashSet<ApplicationWindow> _observed = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _rebuildPending;

    public RunningAppsService()
    {
        _tasksService = new TasksService(IconSize.ExtraLarge);
        _tasks = new Tasks(_tasksService);
        _tasks.Initialize(true);

        _windows = _tasks.CreateGroupedWindowsCollection();
        _windows.Filter = o => o is ApplicationWindow w && w.ShowInTaskbar;
        if (_windows is ICollectionViewLiveShaping live)
        {
            live.IsLiveFiltering = true;
            live.LiveFilteringProperties.Add(nameof(ApplicationWindow.ShowInTaskbar));
        }
        ((INotifyCollectionChanged)_windows).CollectionChanged += (_, _) => RequestRebuild();
        Rebuild();
    }

    public ObservableCollection<RunningApp> Apps { get; } = new();

    private void OnWindowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApplicationWindow.State) or nameof(ApplicationWindow.Icon)
            or nameof(ApplicationWindow.Title) or nameof(ApplicationWindow.ShowInTaskbar) or null or "")
            RequestRebuild();
    }

    private void RequestRebuild()
    {
        if (_rebuildPending)
            return;
        _rebuildPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _rebuildPending = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        var windows = _windows.Cast<ApplicationWindow>().ToList();

        // Track per-window property changes (active state, icon, title).
        foreach (var w in windows.Where(w => _observed.Add(w)))
            w.PropertyChanged += OnWindowChanged;
        foreach (var gone in _observed.Where(w => !windows.Contains(w)).ToList())
        {
            gone.PropertyChanged -= OnWindowChanged;
            _observed.Remove(gone);
        }

        var groups = windows.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());
        // First load: launch order. Afterwards new apps are simply appended, so nothing shifts.
        IEnumerable<KeyValuePair<string, List<ApplicationWindow>>> ordered = Apps.Count == 0
            ? groups.OrderBy(g => LaunchTime(g.Value))
            : groups;

        for (int i = Apps.Count - 1; i >= 0; i--)
        {
            if (!groups.ContainsKey(Apps[i].Key))
            {
                Log.Info($"Dock: removed {Apps[i].Key}");
                Apps.RemoveAt(i);
            }
        }
        foreach (var (key, list) in ordered)
        {
            var app = Apps.FirstOrDefault(a => a.Key == key);
            if (app is null)
            {
                app = new RunningApp(key);
                Apps.Add(app); // new apps go to the end, like a dock
                Log.Info($"Dock: added {key} ({list.Count} window(s))");
            }
            app.Update(list);
        }
    }

    /// <summary>
    /// Grouping key. Must not change over a window's lifetime (a changing key would remove and
    /// re-append the app, reshuffling the dock), and AppUserModelID is filled in lazily — so use
    /// the executable, and the app ID only for UWP windows (whose process is a shared host).
    /// </summary>
    private static string KeyOf(ApplicationWindow w)
    {
        if (w.IsUWP && !string.IsNullOrEmpty(w.AppUserModelID))
            return "aumid:" + w.AppUserModelID.ToLowerInvariant();
        if (!string.IsNullOrEmpty(w.WinFileName))
            return "exe:" + w.WinFileName.ToLowerInvariant();
        return "hwnd:" + w.Handle;
    }

    /// <summary>When the app's process started; used to order the dock on first load.</summary>
    private static DateTime LaunchTime(List<ApplicationWindow> windows)
    {
        var earliest = DateTime.MaxValue;
        foreach (var w in windows)
        {
            if (w.ProcId is not { } pid)
                continue;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                if (p.StartTime < earliest)
                    earliest = p.StartTime;
            }
            catch
            {
                // Elevated or already exited: sort it last.
            }
        }
        return earliest;
    }

    public void Dispose()
    {
        foreach (var w in _observed)
            w.PropertyChanged -= OnWindowChanged;
        _observed.Clear();
        try { _tasks.Dispose(); }
        catch (Exception ex) { Log.Error("Tasks dispose failed", ex); }
        _tasksService.Dispose();
    }
}
