using System.IO;
using System.Diagnostics;

namespace iTask.Utilities;

/// <summary>Tiny append-only file logger. Never throws.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LocalDirectory);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the shell down.
        }
    }
}
