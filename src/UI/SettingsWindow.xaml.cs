using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using iTask.Configuration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.UI;

/// <summary>iTask Settings: every change is applied (and saved) as soon as it's made.</summary>
public partial class SettingsWindow : Window
{
    private const double DefaultMagnification = 1.8;

    private readonly SettingsService _service;
    private readonly ThemeService _theme;
    private readonly List<(ToggleButton Switch, Action<bool> Apply)> _topBarSwitches = new();
    private bool _loading = true; // controls raise change events while being populated

    public SettingsWindow(SettingsService service, ThemeService theme)
    {
        _service = service;
        _theme = theme;
        InitializeComponent();
        // Grow to fit everything where the screen allows; scroll only on short screens.
        MaxHeight = SystemParameters.WorkArea.Height - 40;

        var t = service.Current.TopBar;
        AddTopBarRow("Open apps", t.ShowRunningApps, v => t.ShowRunningApps = v);
        AddTopBarRow("Hidden icons menu", t.ShowTrayIcons, v => t.ShowTrayIcons = v);
        AddTopBarRow("Wi-Fi", t.ShowNetwork, v => t.ShowNetwork = v);
        AddTopBarRow("Sound", t.ShowVolume, v => t.ShowVolume = v);
        AddTopBarRow("Brightness", t.ShowBrightness, v => t.ShowBrightness = v);
        AddTopBarRow("Battery", t.ShowBattery, v => t.ShowBattery = v);
        AddTopBarRow("Date", t.ShowDate, v => t.ShowDate = v);
        AddTopBarRow("Time", t.ShowTime, v => t.ShowTime = v, last: true);

        Load();
        _loading = false;
        theme.Changed += OnThemeChanged;
        Closed += (_, _) => theme.Changed -= OnThemeChanged;
    }

    private AppSettings S => _service.Current;

    private void Load()
    {
        StartupSwitch.IsChecked = StartupRegistration.IsEnabled;
        (S.Dock.Visibility switch
        {
            DockVisibility.AlwaysVisible => ModeAlways,
            DockVisibility.AutoHide => ModeAutoHide,
            _ => ModeSmart,
        }).IsChecked = true;
        IconSizeSlider.Value = S.Dock.IconSize;
        bool magnify = S.Dock.Magnification > 1.01;
        MagnifySwitch.IsChecked = magnify;
        MagnifySlider.Value = magnify ? S.Dock.Magnification : DefaultMagnification;
        UpdateLabels();
    }

    private void AddTopBarRow(string label, bool value, Action<bool> apply, bool last = false)
    {
        var toggle = new ToggleButton { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        toggle.SetResourceReference(StyleProperty, "FlyoutSwitchStyle");
        toggle.Checked += Changed;
        toggle.Unchecked += Changed;
        DockPanel.SetDock(toggle, System.Windows.Controls.Dock.Right);

        var row = new DockPanel();
        row.SetResourceReference(StyleProperty, "SettingsRow");
        row.Children.Add(toggle);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        TopBarRows.Children.Add(row);
        if (!last)
        {
            var separator = new Border();
            separator.SetResourceReference(StyleProperty, "SettingsRowSeparator");
            TopBarRows.Children.Add(separator);
        }
        _topBarSwitches.Add((toggle, apply));
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        S.Dock.Visibility = ModeAlways.IsChecked == true ? DockVisibility.AlwaysVisible
            : ModeAutoHide.IsChecked == true ? DockVisibility.AutoHide
            : DockVisibility.Smart;
        S.Dock.IconSize = Math.Round(IconSizeSlider.Value);
        S.Dock.Magnification = MagnifySwitch.IsChecked == true ? Math.Round(MagnifySlider.Value, 1) : 1.0;
        foreach (var (toggle, apply) in _topBarSwitches)
            apply(toggle.IsChecked == true);
        UpdateLabels();
        _service.Commit();
    }

    private void Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Changed(sender, (RoutedEventArgs)e);

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        bool enabled = StartupSwitch.IsChecked == true;
        StartupRegistration.Set(enabled);
        S.Shell.StartWithWindows = enabled;
        _service.Save(); // nothing on screen depends on it
        // Show what actually happened (e.g. if the registry write was blocked).
        _loading = true;
        StartupSwitch.IsChecked = StartupRegistration.IsEnabled;
        _loading = false;
    }

    private void UpdateLabels()
    {
        IconSizeText.Text = $"{IconSizeSlider.Value:0} px";
        MagnifySlider.IsEnabled = MagnifySwitch.IsChecked == true;
        MagnifyText.Text = MagnifySwitch.IsChecked == true ? $"{MagnifySlider.Value:0.0}×" : "Off";
        ModeHint.Text = ModeAlways.IsChecked == true
            ? "The dock stays on screen, and maximized apps stop above it."
            : ModeAutoHide.IsChecked == true
                ? "The dock stays hidden until you push the pointer against the bottom of the screen."
                : "The dock hides while an app fills the screen; push the pointer against the bottom edge to bring it back.";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTitleBarTheme();

    private void ApplyTitleBarTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int dark = _theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }
}
