using System.Collections.Concurrent;

namespace iTask.Utilities;

/// <summary>
/// Counts a few window messages per window (move/resize/DPI) so a runaway loop shows up in the
/// log with numbers: which window, which message, how many per second.
/// </summary>
public static class MessageStats
{
    private static readonly ConcurrentDictionary<string, int> Counts = new();

    public static void Count(string window, string message) =>
        Counts.AddOrUpdate($"{window}:{message}", 1, (_, n) => n + 1);

    /// <summary>Returns the counts since the last call (largest first) and starts over.</summary>
    public static string Drain()
    {
        var snapshot = Counts.ToArray();
        Counts.Clear();
        return string.Join(" ", snapshot.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
