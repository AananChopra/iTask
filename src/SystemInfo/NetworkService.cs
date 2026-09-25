using System.ComponentModel;
using System.Windows.Threading;
using iTask.Utilities;
using Windows.Networking.Connectivity;

namespace iTask.SystemInfo;

public enum NetworkKind
{
    None,
    Ethernet,
    Wifi,
    Cellular,
}

/// <summary>
/// Connection type, internet reachability and Wi-Fi signal strength via Windows.Networking.Connectivity.
/// Reacts to NetworkStatusChanged; signal strength (which raises no event) is re-read every 15 s,
/// and only while connected over Wi-Fi.
/// </summary>
public sealed class NetworkService : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _signalTimer;

    public NetworkService()
    {
        _signalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _signalTimer.Tick += (_, _) => Refresh();
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        Refresh();
    }

    public NetworkKind Kind { get; private set; }
    public bool HasInternet { get; private set; }
    /// <summary>0–4 for Wi-Fi/cellular (Windows reports 0–5; we map to the 4-step icon).</summary>
    public int SignalBars { get; private set; }
    public string? Name { get; private set; }
    public bool IsConnected => Kind != NetworkKind.None;
    public bool IsWired => Kind == NetworkKind.Ethernet;

    public string Description => Kind switch
    {
        NetworkKind.None => "Not connected",
        _ => $"{Name ?? Kind.ToString()}{(HasInternet ? "" : " — no internet")}",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnNetworkStatusChanged(object sender) => _dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile() ?? FindConnectedProfile();
            if (profile is null)
            {
                Publish(NetworkKind.None, false, 0, null);
                return;
            }

            var level = profile.GetNetworkConnectivityLevel();
            var kind = profile.IsWlanConnectionProfile ? NetworkKind.Wifi
                : profile.IsWwanConnectionProfile ? NetworkKind.Cellular
                : NetworkKind.Ethernet;
            int bars = 4;
            if (kind != NetworkKind.Ethernet && profile.GetSignalBars() is byte raw)
                bars = Math.Clamp((int)Math.Ceiling(raw * 4 / 5.0), 0, 4);

            string? name = null;
            try { name = profile.ProfileName; } catch { /* may require location permission */ }

            Publish(kind, level == NetworkConnectivityLevel.InternetAccess, bars, name);
        }
        catch (Exception ex)
        {
            Log.Error("Network status query failed", ex);
        }
    }

    private static ConnectionProfile? FindConnectedProfile() =>
        NetworkInformation.GetConnectionProfiles()
            .Where(p => p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
            .OrderByDescending(p => p.IsWlanConnectionProfile)
            .FirstOrDefault();

    private void Publish(NetworkKind kind, bool internet, int bars, string? name)
    {
        Kind = kind;
        HasInternet = internet;
        SignalBars = bars;
        Name = name;

        if (kind is NetworkKind.Wifi or NetworkKind.Cellular)
            _signalTimer.Start();
        else
            _signalTimer.Stop();

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Dispose()
    {
        _signalTimer.Stop();
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
    }
}
