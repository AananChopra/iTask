using iTask.ShellIntegration;
using iTask.SystemInfo;
using iTask.WindowsIntegration;

namespace iTask.UI;

/// <summary>Process-wide services shared by every monitor's UI.</summary>
public sealed record ShellServices(
    ThemeService Theme,
    ClockService Clock,
    BatteryService Battery,
    AudioService Audio,
    NetworkService Network,
    ForegroundWatcher Foreground,
    RunningAppsService RunningApps,
    TrayHost? Tray);
