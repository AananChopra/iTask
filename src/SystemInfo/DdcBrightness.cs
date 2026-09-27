using System.Runtime.InteropServices;
using System.Windows.Threading;
using iTask.Utilities;
using iTask.WindowsIntegration;

namespace iTask.SystemInfo;

/// <summary>
/// External monitor brightness over DDC/CI (MCCS VCP code 0x10, the same setting as the monitor's
/// own menu). Every call is a round trip over the display cable — tens of milliseconds, sometimes
/// far more — and a monitor handles one request at a time, so all I/O runs on one background worker
/// and only the latest requested level is ever sent (dragging the slider never queues up steps).
/// </summary>
internal sealed class DdcBrightness : DisplayBrightness, IDisposable
{
    private const byte VcpBrightness = 0x10;

    private readonly string _deviceName;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private int? _pendingSet;
    private bool _pendingRead = true;
    private uint _max = 100;

    public DdcBrightness(string deviceName)
    {
        _deviceName = deviceName;
        Task.Run(WorkLoop);
        _wake.Release(); // initial probe: does this monitor answer at all?
    }

    public override void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        // Show it right away; the monitor catches up in the background.
        Brightness = percent;
        Publish();
        lock (_lock)
            _pendingSet = percent;
        _wake.Release();
    }

    public override void Refresh()
    {
        lock (_lock)
            _pendingRead = true;
        _wake.Release();
    }

    private async Task WorkLoop()
    {
        while (true)
        {
            try { await _wake.WaitAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }

            int? set;
            bool read;
            lock (_lock)
            {
                (set, read) = (_pendingSet, _pendingRead);
                (_pendingSet, _pendingRead) = (null, false);
            }
            if (set is null && !read)
                continue; // already handled by an earlier pass
            try { Run(set, read); }
            catch (Exception ex) { Log.Warn($"DDC/CI brightness on {_deviceName} failed: {ex.Message}"); }
        }
    }

    private void Run(int? set, bool read)
    {
        var monitor = MonitorService.GetMonitors().FirstOrDefault(m => m.DeviceName == _deviceName);
        if (monitor is null || !GetNumberOfPhysicalMonitorsFromHMONITOR(monitor.Handle, out uint count) || count == 0)
        {
            Report(available: false, level: null);
            return;
        }
        var physical = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor.Handle, count, physical))
        {
            Report(available: false, level: null);
            return;
        }
        try
        {
            if (set is { } percent)
            {
                uint value = (uint)Math.Round(percent * _max / 100.0);
                foreach (var p in physical)
                    SetVCPFeature(p.hPhysicalMonitor, VcpBrightness, value);
            }
            if (read)
            {
                // Monitors sometimes drop a request (e.g. right after a set); one retry covers it.
                bool ok = TryRead(physical[0].hPhysicalMonitor, out uint current, out uint max)
                          || TryRead(physical[0].hPhysicalMonitor, out current, out max);
                if (ok)
                    _max = max;
                Report(ok, ok ? (int)Math.Round(current * 100.0 / max) : null);
            }
        }
        finally
        {
            DestroyPhysicalMonitors(count, physical);
        }
    }

    private static bool TryRead(IntPtr monitor, out uint current, out uint max) =>
        GetVCPFeatureAndVCPFeatureReply(monitor, VcpBrightness, IntPtr.Zero, out current, out max) && max > 0;

    private void Report(bool available, int? level) => _dispatcher.BeginInvoke(() =>
    {
        bool changed = available != HasBrightness;
        HasBrightness = available;
        // A read that started before the user moved the slider again is stale; keep their value.
        bool setPending;
        lock (_lock)
            setPending = _pendingSet is not null;
        if (level is { } l && !setPending && l != Brightness)
        {
            Brightness = l;
            changed = true;
        }
        if (changed)
            Publish();
    });

    public void Dispose() => _stop.Cancel();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] physical);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] physical);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}
