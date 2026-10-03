using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.Utilities;

/// <summary>
/// Watches iTask's own GDI / USER object counts. Windows caps both at 10,000 per process, and WPF
/// stops rendering for good once GDI runs out (every window throws OutOfMemoryException), so we
/// restart before that. Windows' own leak detector has caught iTask going from normal to the limit
/// in about 12 seconds, so this samples every second from a background thread (a wedged UI thread
/// can't delay it) and, when the count surges, records what was going on: the ramp, the object
/// types, every iTask window, and what was in front.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;
    private const int RestartThreshold = 3000;
    private const int SurgeStep = 200;
    private const int HistoryLength = 20;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(30);

    private readonly Timer _timer;
    private readonly Queue<int> _history = new();
    private DateTime _lastReport = DateTime.MinValue;
    private DateTime _lastSurgeLog = DateTime.MinValue;
    private int _previousGdi = -1;
    private int _reportedGdiStep;
    private bool _disposed;

    /// <summary>Raised on the UI thread while iTask should restart; the argument says why.</summary>
    public event EventHandler<string>? Unhealthy;

    public HealthMonitor()
    {
        _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Check()
    {
        if (_disposed)
            return;
        try
        {
            using var self = Process.GetCurrentProcess();
            int gdi = (int)GetGuiResources(self.Handle, GR_GDIOBJECTS);
            int user = (int)GetGuiResources(self.Handle, GR_USEROBJECTS);

            _history.Enqueue(gdi);
            while (_history.Count > HistoryLength)
                _history.Dequeue();

            bool surging = _previousGdi >= 0 && gdi - _previousGdi >= SurgeStep;
            _previousGdi = gdi;

            if (surging && DateTime.UtcNow - _lastSurgeLog > TimeSpan.FromSeconds(30))
            {
                _lastSurgeLog = DateTime.UtcNow;
                Log.Warn($"Health: GDI surge, gdi={gdi} user={user}; last {_history.Count}s: {string.Join(",", _history)}; " +
                         $"by type:{GdiBreakdown(self)}\n  front: {DescribeFront()}\n  windows: {DescribeOwnWindows()}");
            }
            else if (DateTime.UtcNow - _lastReport >= ReportInterval || gdi / 1000 > _reportedGdiStep)
            {
                _lastReport = DateTime.UtcNow;
                _reportedGdiStep = Math.Max(_reportedGdiStep, gdi / 1000);
                Log.Info($"Health: gdi={gdi} user={user} handles={self.HandleCount} " +
                         $"memory={self.PrivateMemorySize64 / (1024 * 1024)}MB managed={GC.GetTotalMemory(false) / (1024 * 1024)}MB" +
                         (gdi >= 1000 ? $"; by type:{GdiBreakdown(self)}" : ""));
            }

            if (gdi > RestartThreshold || user > RestartThreshold)
            {
                var reason = $"GDI/USER objects near the Windows limit (gdi={gdi}, user={user})";
                Application.Current?.Dispatcher.BeginInvoke(() => Unhealthy?.Invoke(this, reason));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Health check failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    /// <summary>The foreground window and its rectangle, to see what the user was doing.</summary>
    private static string DescribeFront()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return "(none)";
        GetWindowThreadProcessId(fg, out uint pid);
        string name = "?";
        try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
        string rect = GetWindowRect(fg, out var r) ? r.ToString() : "?";
        return $"{name} '{Truncate(WindowTitle(fg), 40)}' [{WindowUtils(fg)}] {rect} zoomed={IsZoomed(fg)}";
    }

    /// <summary>Every top-level window this process owns: class/title, visibility and position.</summary>
    private static string DescribeOwnWindows()
    {
        var sb = new StringBuilder();
        int self = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != self)
                return true;
            string title = Truncate(WindowTitle(hwnd), 24);
            if (title.Length == 0 && !IsWindowVisible(hwnd))
                return true; // message-only plumbing
            sb.Append($"[{title} vis={(IsWindowVisible(hwnd) ? 1 : 0)} {(GetWindowRect(hwnd, out var r) ? r.ToString() : "?")}] ");
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }

    private static string WindowUtils(IntPtr hwnd) => iTask.WindowsIntegration.WindowUtils.GetClassName(hwnd);

    private static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// Counts this process's GDI objects by type by reading the GDI handle table (PEB.GdiSharedHandleTable).
    /// Best effort: returns a note if it can't.
    /// </summary>
    private static string GdiBreakdown(Process self)
    {
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(self.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return " (unavailable)";

            var pointer = new byte[IntPtr.Size];
            if (!ReadProcessMemory(self.Handle, info.PebBaseAddress + 0xF8, pointer, (IntPtr)pointer.Length, out _))
                return " (unavailable)";
            var table = (IntPtr)BitConverter.ToInt64(pointer, 0);
            if (table == IntPtr.Zero)
                return " (unavailable)";

            const int entrySize = 24;
            var buffer = new byte[65536 * entrySize];
            if (!ReadProcessMemory(self.Handle, table, buffer, (IntPtr)buffer.Length, out _))
                return " (unavailable)";

            var counts = new SortedDictionary<int, int>();
            int pid = self.Id & 0xFFFF;
            for (int i = 0; i < 65536; i++)
            {
                int offset = i * entrySize;
                if (BitConverter.ToUInt16(buffer, offset + 8) != pid)
                    continue;
                int type = buffer[offset + 14] & 0x7F;
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }
            return string.Concat(counts.Select(kv => $" {TypeName(kv.Key)}={kv.Value}"));
        }
        catch (Exception ex)
        {
            return $" (unavailable: {ex.Message})";
        }
    }

    private static string TypeName(int type) => type switch
    {
        1 => "dc", 4 => "region", 5 => "bitmap", 8 => "palette", 0xA => "font", 0x10 => "brush",
        0x30 => "pen", 0x21 => "metafile", 0x66 => "colorspace", _ => $"type{type:X2}",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, Reserved3;
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);
}
