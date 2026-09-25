using System.Diagnostics;
using iTask.Utilities;
using iTask.WindowsIntegration;

namespace iTask.ShellIntegration;

/// <summary>Entry points into existing Windows shell UI (we never re-implement these).</summary>
public static class ShellCommands
{
    public static void OpenStartMenu() => InputSender.SendChord(InputSender.VK_LWIN);

    /// <summary>Win+A: Quick Settings (Wi-Fi, volume slider, battery saver…).</summary>
    public static void OpenQuickSettings() => InputSender.SendChord(InputSender.VK_LWIN, InputSender.VK_A);

    public static void OpenTaskManager() => Launch("taskmgr.exe");

    public static void OpenSettings() => Launch("ms-settings:");

    public static void Launch(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { Arguments = arguments ?? "", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to launch '{target}'", ex);
        }
    }
}
