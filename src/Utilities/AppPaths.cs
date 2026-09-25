using System.IO;
namespace iTask.Utilities;

public static class AppPaths
{
    /// <summary>%APPDATA%\iTask — user-editable configuration (roams).</summary>
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iTask");

    /// <summary>%LOCALAPPDATA%\iTask — machine-local state and logs.</summary>
    public static string LocalDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iTask");

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public static string TaskbarStateFile => Path.Combine(LocalDirectory, "taskbar-state.json");
    public static string LogFile => Path.Combine(LocalDirectory, "iTask.log");
}
