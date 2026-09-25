using System.Runtime.InteropServices;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.WindowsIntegration;

/// <summary>A physical display. All rectangles are in physical pixels (the process is Per-Monitor-V2 aware).</summary>
public sealed record MonitorInfo(IntPtr Handle, string DeviceName, RECT Bounds, RECT WorkArea, bool IsPrimary, double Scale)
{
    public int ToPixels(double dips) => (int)Math.Round(dips * Scale);
}

public static class MonitorService
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMonitor, ref info))
                return true;

            double scale = 1.0;
            if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                scale = dpiX / 96.0;

            monitors.Add(new MonitorInfo(hMonitor, info.szDevice, info.rcMonitor, info.rcWork,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0, scale));
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return monitors;
    }

    public static MonitorInfo? GetPrimary() => GetMonitors().FirstOrDefault(m => m.IsPrimary);
}
