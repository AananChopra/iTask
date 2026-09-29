using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace iTask.Utilities;

/// <summary>
/// Watches iTask's own GDI / USER object counts. Windows caps both at 10,000 per process, and WPF
/// stops rendering for good once GDI runs out (every window throws OutOfMemoryException), so we
/// restart well before that. Also logs the counts now and then, to show when a leak grows.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;
    private const int RestartThreshold = 8000;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(30);

    private readonly DispatcherTimer _timer;
    private DateTime _lastReport = DateTime.MinValue;
    private int _reportedGdiStep;

    /// <summary>Raised on the UI thread, every check while iTask should restart; the argument says why.</summary>
    public event EventHandler<string>? Unhealthy;

    public HealthMonitor()
    {
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    private void Check()
    {
        using var self = Process.GetCurrentProcess();
        int gdi = (int)GetGuiResources(self.Handle, GR_GDIOBJECTS);
        int user = (int)GetGuiResources(self.Handle, GR_USEROBJECTS);

        int gdiStep = gdi / 1000;
        if (DateTime.UtcNow - _lastReport >= ReportInterval || gdiStep > _reportedGdiStep)
        {
            _lastReport = DateTime.UtcNow;
            _reportedGdiStep = Math.Max(_reportedGdiStep, gdiStep);
            Log.Info($"Health: gdi={gdi} user={user} handles={self.HandleCount} " +
                     $"memory={self.PrivateMemorySize64 / (1024 * 1024)}MB managed={GC.GetTotalMemory(false) / (1024 * 1024)}MB");
        }

        if (gdi > RestartThreshold || user > RestartThreshold)
            Unhealthy?.Invoke(this, $"GDI/USER objects near the Windows limit (gdi={gdi}, user={user})");
    }

    public void Dispose() => _timer.Stop();

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
