using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using iTask.ShellIntegration;
using iTask.SystemInfo;

namespace iTask.TopBar.Flyouts;

/// <summary>
/// Wi-Fi menu: radio switch, connection status, nearby networks (saved ones connect directly;
/// new ones go to Windows' network list, which can ask for the password).
/// </summary>
public partial class WifiFlyout : UserControl, IFlyoutContent
{
    private const int MaxNetworks = 8;

    private readonly WifiService _wifi;
    private readonly NetworkService _network;
    private readonly DispatcherTimer _rescan;

    public WifiFlyout(WifiService wifi, NetworkService network)
    {
        _wifi = wifi;
        _network = network;
        InitializeComponent();

        // Show what Windows already knows immediately, then refresh once a fresh scan has landed.
        _rescan = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _rescan.Tick += (_, _) =>
        {
            _rescan.Stop();
            ShowNetworks();
        };
        Loaded += async (_, _) =>
        {
            ShowStatus();
            ShowNetworks();
            _wifi.RequestScan();
            _rescan.Start();
            await ShowRadioAsync();
        };
        Unloaded += (_, _) => _rescan.Stop();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ContentChanged;

    private void ShowStatus()
    {
        StatusText.Text = _network.Kind switch
        {
            NetworkKind.Wifi => $"Connected to {_network.Name ?? "Wi-Fi"}{(_network.HasInternet ? "" : " — no internet")}",
            NetworkKind.Ethernet => $"Using Ethernet{(_network.HasInternet ? "" : " — no internet")}",
            NetworkKind.Cellular => "Using cellular data",
            _ => "Not connected",
        };
    }

    private void ShowNetworks()
    {
        if (!_wifi.HasWifiAdapter)
        {
            NetworksSection.Visibility = Visibility.Collapsed;
            return;
        }
        var result = _wifi.GetNetworks();
        Networks.ItemsSource = result.Networks.Take(MaxNetworks).ToList();
        UnavailableText.Text = result.Unavailable ?? "";
        UnavailableText.Visibility = result.Unavailable is null ? Visibility.Collapsed : Visibility.Visible;
        NetworksHeader.Visibility = result.Networks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ShowRadioAsync()
    {
        var on = await _wifi.GetRadioOnAsync();
        RadioSwitch.IsEnabled = on is not null;
        RadioSwitch.IsChecked = on ?? _wifi.HasWifiAdapter;
    }

    private async void RadioSwitch_Click(object sender, RoutedEventArgs e)
    {
        RadioSwitch.IsEnabled = false;
        await _wifi.SetRadioAsync(RadioSwitch.IsChecked == true);
        await ShowRadioAsync();
        await Task.Delay(1500); // let the adapter come up / go down
        ShowStatus();
        ShowNetworks();
    }

    private void Network_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WifiNetwork network || network.IsConnected)
            return;
        if (!network.HasProfile || !_wifi.Connect(network))
            ShellCommands.Launch("ms-availablenetworks:"); // Windows asks for the password
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OtherNetworks_Click(object sender, RoutedEventArgs e)
    {
        ShellCommands.Launch("ms-availablenetworks:");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShellCommands.Launch("ms-settings:network-wifi");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
