using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using iTask.Utilities;

namespace iTask.SystemInfo;

/// <summary>
/// Per-display brightness: the laptop's built-in panel through WMI (what its Fn keys and Windows'
/// own slider use), external monitors through DDC/CI (what their on-screen menu buttons change).
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private readonly PanelBrightness _panel = new();
    private readonly Dictionary<string, DdcBrightness> _external = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The brightness control for the display with GDI name <paramref name="deviceName"/> (e.g. \\.\DISPLAY1).</summary>
    public DisplayBrightness For(string deviceName)
    {
        if (_panel.HasBrightness && _panel.Controls(deviceName))
            return _panel;
        if (!_external.TryGetValue(deviceName, out var ddc))
            _external[deviceName] = ddc = new DdcBrightness(deviceName);
        return ddc;
    }

    public void Dispose()
    {
        _panel.Dispose();
        foreach (var ddc in _external.Values)
            ddc.Dispose();
    }
}

/// <summary>
/// Built-in panel via WMI (root\WMI, WmiMonitorBrightness / WmiMonitorBrightnessMethods). Only the
/// internal panel (and rare externals) expose this.
/// </summary>
internal sealed class PanelBrightness : DisplayBrightness, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ManagementEventWatcher? _watcher;
    private string? _instance;

    public PanelBrightness()
    {
        Refresh();
        try
        {
            _watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"),
                new WqlEventQuery("SELECT * FROM __InstanceModificationEvent WITHIN 1 WHERE TargetInstance ISA 'WmiMonitorBrightness'"));
            _watcher.EventArrived += (_, _) => _dispatcher.BeginInvoke(Refresh);
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"Brightness change notifications unavailable: {ex.Message}");
        }
    }

    public override void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using var m = (ManagementObject)o;
                m.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
                break; // the internal panel is always the first (and usually only) instance
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not set brightness", ex);
        }
        Refresh();
    }

    public override void Refresh()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using var m = (ManagementObject)o;
                Brightness = Convert.ToInt32(m["CurrentBrightness"]);
                _instance = m["InstanceName"] as string;
                HasBrightness = true;
                Publish();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Brightness unavailable: {ex.Message}");
        }
        HasBrightness = false;
        Publish();
    }

    /// <summary>Whether this panel is the display with GDI name <paramref name="deviceName"/>.</summary>
    public bool Controls(string deviceName)
    {
        if (_instance is null)
            return false;
        // WMI: "DISPLAY\BOE0A0B\4&15ab2c86&0&UID8388688_0"; device interface path of the same
        // monitor: "\\?\DISPLAY#BOE0A0B#4&15ab2c86&0&UID8388688#{e6f07b5f-...}".
        string key = Regex.Replace(_instance, @"_\d+$", "").Replace('\\', '#');
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(deviceName, i, ref dd, EDD_GET_DEVICE_INTERFACE_NAME); i++)
        {
            if (dd.DeviceID.Contains(key, StringComparison.OrdinalIgnoreCase))
                return true;
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return false;
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch { /* already stopped */ }
        _watcher?.Dispose();
    }

    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE info, uint flags);
}
