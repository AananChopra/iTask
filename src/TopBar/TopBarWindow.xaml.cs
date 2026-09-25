using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using iTask.Configuration;
using iTask.ShellIntegration;
using iTask.UI;
using iTask.WindowsIntegration;

namespace iTask.TopBar;

public partial class TopBarWindow : OverlayWindow
{
    private readonly ShellServices _services;

    public TopBarWindow(TopBarSettings settings, ShellServices services)
    {
        _services = services;
        InitializeComponent();
        DataContext = services;

        if (settings.ShowRunningApps)
            RunningApps.ItemsSource = services.RunningApps.Apps;
        else
            RunningApps.Visibility = Visibility.Collapsed;

        if (settings.ShowTrayIcons && services.Tray is not null)
        {
            TrayIcons.ItemsSource = services.Tray.Icons;
            services.Foreground.Changed += OnForegroundChanged;
        }
        else
        {
            TrayButton.Visibility = Visibility.Collapsed;
        }

        // Visibility bindings handle "no battery" / "no audio device"; settings can hide them outright.
        if (!settings.ShowNetwork) NetworkButton.Visibility = Visibility.Collapsed;
        if (!settings.ShowVolume) VolumeButton.Visibility = Visibility.Collapsed;
        if (!settings.ShowBattery) BatteryButton.Visibility = Visibility.Collapsed;

        DateLabel.Visibility = settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
        TimeLabel.Visibility = settings.ShowTime ? Visibility.Visible : Visibility.Collapsed;
        DateLabel.Margin = settings.ShowTime ? new Thickness(0, 0, 10, 0) : new Thickness(0);
        if (!settings.ShowDate && !settings.ShowTime)
            ClockPanel.Visibility = Visibility.Collapsed;
    }

    protected override BackdropKind GetBackdrop(ThemeService theme) => theme.TopBarBackdrop;

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        AppMenu.PlacementTarget = MenuButton;
        AppMenu.Placement = PlacementMode.Bottom;
        AppMenu.IsOpen = true;
    }

    private void QuickSettings_Click(object sender, RoutedEventArgs e) => ShellCommands.OpenQuickSettings();

    // The dropdown can't use StaysOpen=False: that relies on the owner window being active, and ours
    // never is. It closes on: the chevron again, any other click on the bar, or a foreground change
    // (clicking another app or the desktop).
    private void TrayButton_Click(object sender, RoutedEventArgs e) => TrayPopup.IsOpen = !TrayPopup.IsOpen;

    private void OnForegroundChanged(object? sender, EventArgs e) => TrayPopup.IsOpen = false;

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (TrayPopup.IsOpen && !TrayButton.IsMouseOver)
            TrayPopup.IsOpen = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _services.Foreground.Changed -= OnForegroundChanged;
        base.OnClosed(e);
    }

    // Chevron points down when closed, up while the dropdown is open.
    private void TrayPopup_OpenedChanged(object? sender, EventArgs e) =>
        TrayChevron.Text = TrayPopup.IsOpen ? "" : "";

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

    private void Settings_Click(object sender, RoutedEventArgs e) => ShellCommands.OpenSettings();

    private void TaskManager_Click(object sender, RoutedEventArgs e) => ShellCommands.OpenTaskManager();

    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
