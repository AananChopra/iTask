using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace iTask.Utilities;

/// <summary>
/// Watches iTask's own GDI / USER object counts. Windows caps both at 10,000 per process, and WPF
/// stops rendering for good once GDI runs out (every window throws OutOfMemoryException), so we
/// restart well before that. Also logs the counts now and then, and a per-type breakdown whenever
/// they surge, to show what is leaking.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;
    private const int RestartThreshold = 6000;
    private const int SurgeStep = 150;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(30);

    private readonly DispatcherTimer _timer;
    private DateTime _lastReport = DateTime.MinValue;
    private DateTime _lastSurgeLog = DateTime.MinValue;
    private int _previousGdi = -1;
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
        bool surging = _previousGdi >= 0 && gdi - _previousGdi >= SurgeStep;
        _previousGdi = gdi;

        if (surging && DateTime.UtcNow - _lastSurgeLog > TimeSpan.FromSeconds(30))
        {
            _lastSurgeLog = DateTime.UtcNow;
            Log.Warn($"Health: GDI surge, gdi={gdi} user={user}; by type:{GdiBreakdown(self)}");
        }
        else if (DateTime.UtcNow - _lastReport >= ReportInterval || gdiStep > _reportedGdiStep)
        {
            _lastReport = DateTime.UtcNow;
            _reportedGdiStep = Math.Max(_reportedGdiStep, gdiStep);
            Log.Info($"Health: gdi={gdi} user={user} handles={self.HandleCount} " +
                     $"memory={self.PrivateMemorySize64 / (1024 * 1024)}MB managed={GC.GetTotalMemory(false) / (1024 * 1024)}MB" +
                     (gdi >= 1000 ? $"; by type:{GdiBreakdown(self)}" : ""));
        }

        if (gdi > RestartThreshold || user > RestartThreshold)
            Unhealthy?.Invoke(this, $"GDI/USER objects near the Windows limit (gdi={gdi}, user={user})");
    }

    public void Dispose() => _timer.Stop();

    /// <summary>
    /// Counts this process's GDI objects by type by reading the GDI handle table (PEB.GdiSharedHandleTable),
    /// the same trick Task Managerâ€“style tools use. Best effort: returns a note if it can't.
    /// </summary>
    private static string GdiBreakdown(Process self)
    {
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(self.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return " (unavailable)";

            var pointer = new byte[IntPtr.Size];
            if (!ReadProcessMemory(self.Handle, info.PebBaseAddress + (IntPtr.Size == 8 ? 0xF8 : 0x94), pointer, (IntPtr)pointer.Length, out _))
                return " (unavailable)";
            var table = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(pointer, 0) : (IntPtr)BitConverter.ToInt32(pointer, 0);
            if (table == IntPtr.Zero)
                return " (unavailable)";

            int entrySize = IntPtr.Size == 8 ? 24 : 16;
            var buffer = new byte[65536 * entrySize];
            if (!ReadProcessMemory(self.Handle, table, buffer, (IntPtr)buffer.Length, out _))
                return " (unavailable)";

            var counts = new SortedDictionary<int, int>();
            int pid = self.Id & 0xFFFF;
            int typeOffset = IntPtr.Size == 8 ? 14 : 10;
            for (int i = 0; i < 65536; i++)
            {
                int offset = i * entrySize;
                if (BitConverter.ToUInt16(buffer, offset + IntPtr.Size) != pid)
                    continue;
                int type = buffer[offset + typeOffset] & 0x7F;
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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);
}
