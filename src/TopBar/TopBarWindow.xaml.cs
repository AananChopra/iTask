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
        BrightnessButton.DataContext = _brightness;

        ApplySettings();
        // Some items also depend on the hardware being there (a battery, an audio device, a dimmable display).
        _brightness.PropertyChanged += OnStatusChanged;
        services.Audio.PropertyChanged += OnStatusChanged;
        services.Battery.PropertyChanged += OnStatusChanged;
    }

    /// <summary>Shows or hides each item per the settings (and the hardware); safe to call again after a change.</summary>
    public void ApplySettings()
    {
        var s = _settings;
        RunningApps.ItemsSource = s.ShowRunningApps ? _services.RunningApps.Apps : null;
        RunningApps.Visibility = Shown(s.ShowRunningApps);
        TrayButton.Visibility = Shown(s.ShowTrayIcons && _services.Tray is not null);
        NetworkButton.Visibility = Shown(s.ShowNetwork);
        VolumeButton.Visibility = Shown(s.ShowVolume && _services.Audio.HasDevice);
        BatteryButton.Visibility = Shown(s.ShowBattery && _services.Battery.HasBattery);
        BrightnessButton.Visibility = Shown(s.ShowBrightness && _brightness.HasBrightness);

        DateLabel.Visibility = Shown(s.ShowDate);
        TimeLabel.Visibility = Shown(s.ShowTime);
        DateLabel.Margin = s.ShowTime ? new Thickness(0, 0, 10, 0) : new Thickness(0);
        ClockButton.Visibility = Shown(s.ShowDate || s.ShowTime);
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ApplySettings);

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
            flyout.Loaded += (_, _) => RotateChevron(180);
            flyout.Unloaded += (_, _) => RotateChevron(0);
            return flyout;
        }, TrayFlyout.WidthFor(icons.Cast<object>().Count()));
    }

    /// <summary>Tray arrow points down when closed and turns to point up while its menu is open.</summary>
    private void RotateChevron(double angle)
    {
        var animation = new System.Windows.Media.Animation.DoubleAnimation(angle, TimeSpan.FromMilliseconds(iTask.Utilities.SystemAnimations.Enabled ? 200 : 0))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        TrayChevronRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, animation);
    }

    private void ToggleMenu(object key, FrameworkElement anchor, Func<FrameworkElement> content, double width)
    {
        NativeMethods.GetWindowRect(Handle, out var bar);
        // Keyed per bar, so the same menu on another display opens there instead of toggling closed.
        _services.Flyouts.Toggle((this, key), anchor, bar.Bottom, content, width);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        // After this release's Click has run (which may have switched to another menu).
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, _services.Flyouts.OnBarMouseReleased);
    }

    protected override void OnClosed(EventArgs e)
    {
        _brightness.PropertyChanged -= OnStatusChanged;
        _services.Audio.PropertyChanged -= OnStatusChanged;
        _services.Battery.PropertyChanged -= OnStatusChanged;
        base.OnClosed(e);
    }

    private void AppButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RunningApp app)
            app.Toggle();
    }

    private void AppButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RunningApp app } element)
            ToggleMenu(("app-windows", app), element, () => new AppWindowsFlyout(app), AppWindowsFlyout.MenuWidth);
        e.Handled = true;
    }

    private void VolumeButton_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _services.Audio.Adjust(e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }

}
