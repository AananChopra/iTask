using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using iTask.ShellIntegration;
using iTask.SystemInfo;

namespace iTask.TopBar.Flyouts;

/// <summary>Battery menu: charge, power source, time remaining, battery saver.</summary>
public partial class BatteryFlyout : UserControl, IFlyoutContent
{
    private readonly BatteryService _battery;

    public BatteryFlyout(BatteryService battery)
    {
        _battery = battery;
        InitializeComponent();
        Sync();
        _battery.PropertyChanged += OnBatteryChanged;
        Unloaded += (_, _) => _battery.PropertyChanged -= OnBatteryChanged;
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ContentChanged;

    private void OnBatteryChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Sync);

    private void Sync()
    {
        var b = _battery;
        PercentText.Text = $"{b.Percent}%";
        SourceText.Text = $"Power source: {(b.IsPluggedIn ? "Power adapter" : "Battery")}";
        StateText.Text = b.IsCharging ? "Charging"
            : b.IsPluggedIn ? (b.Percent >= 99 ? "Fully charged" : "Plugged in, not charging")
            : b.TimeRemaining is { } left ? $"{(int)left.TotalHours}:{left.Minutes:00} remaining"
            : "Estimating time remaining…";
        SaverText.Text = $"Battery saver: {(b.IsSaverOn ? "On" : "Off")}";
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShellCommands.Launch("ms-settings:batterysaver");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
