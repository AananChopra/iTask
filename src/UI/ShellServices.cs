using iTask.ShellIntegration;
using iTask.SystemInfo;
using iTask.TopBar.Flyouts;
using iTask.WindowsIntegration;

namespace iTask.UI;

/// <summary>Process-wide services shared by every monitor's UI.</summary>
public sealed record ShellServices(
    ThemeService Theme,
    ClockService Clock,
    BatteryService Battery,
    AudioService Audio,
    BrightnessService Brightness,
    NetworkService Network,
    WifiService Wifi,
    ForegroundWatcher Foreground,
    RunningAppsService RunningApps,
    FlyoutHost Flyouts,
    SettingsService Settings,
    TrayHost? Tray);
