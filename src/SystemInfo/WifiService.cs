using System.Runtime.InteropServices;
using System.Text;
using iTask.Utilities;
using Windows.Devices.Radios;

namespace iTask.SystemInfo;

public sealed record WifiNetwork(string Ssid, string? ProfileName, int SignalQuality, bool IsSecure, bool IsConnected)
{
    public bool HasProfile => ProfileName is not null;
    /// <summary>0–4, the same scale as the top bar icon.</summary>
    public int Bars => SignalQuality >= 80 ? 4 : SignalQuality >= 55 ? 3 : SignalQuality >= 30 ? 2 : SignalQuality > 5 ? 1 : 0;
}

public sealed record WifiScanResult(IReadOnlyList<WifiNetwork> Networks, string? Unavailable);

/// <summary>
/// Wi-Fi networks (native WLAN API) and the Wi-Fi radio switch (Windows.Devices.Radios).
/// Listing networks can require location permission on newer Windows 11 builds; when it's denied
/// the result says so rather than failing silently.
/// </summary>
public sealed class WifiService : IDisposable
{
    private IntPtr _client;
    private Guid? _interface;

    public WifiService()
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out _client) != 0)
                _client = IntPtr.Zero;
            _interface = FirstInterface();
        }
        catch (Exception ex)
        {
            Log.Warn($"WLAN API unavailable: {ex.Message}");
        }
    }

    public bool HasWifiAdapter => _interface is not null;

    /// <summary>Asks the adapter to rescan; results show up in <see cref="GetNetworks"/> a moment later.</summary>
    public void RequestScan()
    {
        if (_client == IntPtr.Zero || _interface is not { } iface)
            return;
        WlanScan(_client, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    public WifiScanResult GetNetworks()
    {
        if (_client == IntPtr.Zero || _interface is not { } iface)
            return new WifiScanResult(Array.Empty<WifiNetwork>(), "No Wi-Fi adapter");

        int error = WlanGetAvailableNetworkList(_client, ref iface, 0, IntPtr.Zero, out var list);
        if (error == 5 /* ACCESS_DENIED */)
            return new WifiScanResult(Array.Empty<WifiNetwork>(), "Windows needs location access to list networks");
        if (error != 0)
            return new WifiScanResult(Array.Empty<WifiNetwork>(), error == 0x10DF /* not ready */ ? "Wi-Fi is off" : $"Couldn't list networks ({error})");

        var bySsid = new Dictionary<string, WifiNetwork>();
        try
        {
            int count = Marshal.ReadInt32(list, 0);
            for (int i = 0; i < count; i++)
            {
                var item = list + 8 + i * 628;
                int ssidLength = Math.Clamp(Marshal.ReadInt32(item, 512), 0, 32);
                if (ssidLength == 0)
                    continue; // hidden network
                var ssidBytes = new byte[ssidLength];
                Marshal.Copy(item + 516, ssidBytes, 0, ssidLength);
                string ssid = Encoding.UTF8.GetString(ssidBytes);
                string profile = Marshal.PtrToStringUni(item)!;
                int quality = Marshal.ReadInt32(item, 604);
                bool secure = Marshal.ReadInt32(item, 608) != 0;
                uint flags = (uint)Marshal.ReadInt32(item, 620);
                bool connected = (flags & 1) != 0;       // WLAN_AVAILABLE_NETWORK_CONNECTED
                bool hasProfile = (flags & 2) != 0;      // WLAN_AVAILABLE_NETWORK_HAS_PROFILE

                var network = new WifiNetwork(ssid, hasProfile && profile.Length > 0 ? profile : null, quality, secure, connected);
                // The same SSID can appear once per profile/BSS type: keep the most useful entry.
                if (bySsid.TryGetValue(ssid, out var existing))
                    network = existing with
                    {
                        ProfileName = existing.ProfileName ?? network.ProfileName,
                        SignalQuality = Math.Max(existing.SignalQuality, network.SignalQuality),
                        IsConnected = existing.IsConnected || network.IsConnected,
                    };
                bySsid[ssid] = network;
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        var networks = bySsid.Values
            .OrderByDescending(n => n.IsConnected)
            .ThenByDescending(n => n.HasProfile)
            .ThenByDescending(n => n.SignalQuality)
            .ToList();
        return new WifiScanResult(networks, null);
    }

    /// <summary>
    /// Connects using the saved profile. Networks without one need a password prompt, which only
    /// Windows can show, so those open Windows' network list instead.
    /// </summary>
    public bool Connect(WifiNetwork network)
    {
        if (_client == IntPtr.Zero || _interface is not { } iface || network.ProfileName is null)
            return false;
        var parameters = new WLAN_CONNECTION_PARAMETERS
        {
            wlanConnectionMode = 0, // wlan_connection_mode_profile
            strProfile = network.ProfileName,
            dot11BssType = 1,       // infrastructure
        };
        int error = WlanConnect(_client, ref iface, ref parameters, IntPtr.Zero);
        if (error != 0)
            Log.Warn($"WlanConnect('{network.ProfileName}') failed: {error}");
        return error == 0;
    }

    public void Disconnect()
    {
        if (_client != IntPtr.Zero && _interface is { } iface)
            WlanDisconnect(_client, ref iface, IntPtr.Zero);
    }

    /// <summary>Wi-Fi radio state; null when it can't be determined.</summary>
    public async Task<bool?> GetRadioOnAsync()
    {
        var radio = await GetWifiRadioAsync();
        return radio is null ? null : radio.State == RadioState.On;
    }

    public async Task<bool> SetRadioAsync(bool on)
    {
        var radio = await GetWifiRadioAsync();
        if (radio is null)
            return false;
        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        if (result != RadioAccessStatus.Allowed)
            Log.Warn($"Wi-Fi radio change refused: {result}");
        return result == RadioAccessStatus.Allowed;
    }

    private static async Task<Radio?> GetWifiRadioAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
                return null;
            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio API unavailable: {ex.Message}");
            return null;
        }
    }

    private Guid? FirstInterface()
    {
        if (_client == IntPtr.Zero || WlanEnumInterfaces(_client, IntPtr.Zero, out var list) != 0)
            return null;
        try
        {
            int count = Marshal.ReadInt32(list, 0);
            if (count == 0)
                return null;
            var guidBytes = new byte[16];
            Marshal.Copy(list + 8, guidBytes, 0, 16); // WLAN_INTERFACE_INFO.InterfaceGuid
            return new Guid(guidBytes);
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    public void Dispose()
    {
        if (_client != IntPtr.Zero)
            WlanCloseHandle(_client, IntPtr.Zero);
        _client = IntPtr.Zero;
    }

    // ── WLAN API ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string strProfile;
        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public int dot11BssType;
        public uint dwFlags;
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetAvailableNetworkList(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr networkList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr ssid, IntPtr ieData, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanConnect(IntPtr clientHandle, ref Guid interfaceGuid, ref WLAN_CONNECTION_PARAMETERS parameters, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanDisconnect(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
