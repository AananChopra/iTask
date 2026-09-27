using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using iTask.Configuration;
using iTask.ShellIntegration;
using iTask.SystemInfo;
using iTask.TopBar.Flyouts;
using iTask.UI;
using iTask.WindowsIntegration;

namespace iTask.TopBar;

public partial class TopBarWindow : OverlayWindow
{
    private readonly ShellServices _services;
    private readonly TopBarSettings _settings;
    private readonly DisplayBrightness _brightness;

    public TopBarWindow(TopBarSettings settings, ShellServices services, string deviceName, bool glass)
    {
        _services = services;
        _settings = settings;
        // This display's own brightness (the laptop panel via WMI, an external monitor via DDC/CI).
        _brightness = services.Brightness.For(deviceName);
        if (glass)
            UseGlass();
        InitializeComponent();
        DataContext = services;
        BarContent.Height = settings.Height;

        if (settings.ShowRunningApps)
            RunningApps.ItemsSource = services.RunningApps.Apps;
        else
            RunningApps.Visibility = Visibility.Collapsed;

        if (!settings.ShowTrayIcons || services.Tray is null)
            TrayButton.Visibility = Visibility.Collapsed;

        // Visibility bindings handle "no battery" / "no audio device"; settings can hide them outright.
        if (!settings.ShowNetwork) NetworkButton.Visibility = Visibility.Collapsed;
        if (!settings.ShowVolume) VolumeButton.Visibility = Visibility.Collapsed;
        if (!settings.ShowBattery) BatteryButton.Visibility = Visibility.Collapsed;
        BrightnessButton.DataContext = _brightness;
        UpdateBrightnessButton();
        _brightness.PropertyChanged += OnBrightnessChanged;

        DateLabel.Visibility = settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
        TimeLabel.Visibility = settings.ShowTime ? Visibility.Visible : Visibility.Collapsed;
        DateLabel.Margin = settings.ShowTime ? new Thickness(0, 0, 10, 0) : new Thickness(0);
        if (!settings.ShowDate && !settings.ShowTime)
            ClockButton.Visibility = Visibility.Collapsed;
    }

    protected override BackdropKind GetBackdrop(ThemeService theme) => theme.TopBarBackdrop;

    private void MenuButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMenu("app", MenuButton, () => new AppMenuFlyout(_services.Settings.ShowWindow), 210);

    // ── Status menus (our own macOS-style dropdowns, opening from the bar) ───────────────────

    private void NetworkButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMenu("wifi", NetworkButton, () => new WifiFlyout(_services.Wifi, _services.Network), 290);

    private void VolumeButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMenu("sound", VolumeButton, () => new SoundFlyout(_services.Audio), 280);

    private void BrightnessButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMenu("brightness", BrightnessButton, () => new BrightnessFlyout(_brightness), 260);

    private void ClockButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMenu("calendar", ClockButton, () => new CalendarFlyout(), 270);

    private void BatteryButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMenu("battery", BatteryButton, () => new BatteryFlyout(_services.Battery), 250);

    private void TrayButton_Click(object sender, RoutedEventArgs e)
    {
        var icons = _services.Tray!.Icons;
        ToggleMenu("tray", TrayButton, () =>
        {
            var flyout = new TrayFlyout(icons);
            // Chevron points up while the menu is open.
            flyout.Loaded += (_, _) => TrayChevron.Text = "";
            flyout.Unloaded += (_, _) => TrayChevron.Text = "";
            return flyout;
        }, TrayFlyout.WidthFor(icons.Cast<object>().Count()));
    }

    private void ToggleMenu(string key, FrameworkElement anchor, Func<FrameworkElement> content, double width)
    {
        NativeMethods.GetWindowRect(Handle, out var bar);
        // Keyed per bar, so the same menu on another display opens there instead of toggling closed.
        _services.Flyouts.Toggle((this, key), anchor, bar.Bottom, content, width);
    }

    protected override void OnClosed(EventArgs e)
    {
        _brightness.PropertyChanged -= OnBrightnessChanged;
        base.OnClosed(e);
    }

    private void OnBrightnessChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateBrightnessButton);

    // Hidden on displays that can't be dimmed (no WMI panel, no DDC/CI support).
    private void UpdateBrightnessButton() =>
        BrightnessButton.Visibility = _settings.ShowBrightness && _brightness.HasBrightness
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void AppButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RunningApp app)
            app.Toggle();
    }

    private void AppButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RunningApp app } element)
            AppContextMenu.Show(app, element, PlacementMode.Bottom);
        e.Handled = true;
    }

    private void VolumeButton_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _services.Audio.Adjust(e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }

}
