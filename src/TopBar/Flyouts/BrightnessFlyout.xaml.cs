using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using iTask.ShellIntegration;
using iTask.SystemInfo;

namespace iTask.TopBar.Flyouts;

/// <summary>Display menu: a brightness slider for the built-in panel.</summary>
public partial class BrightnessFlyout : UserControl, IFlyoutContent
{
    private readonly BrightnessService _brightness;
    private bool _syncing;

    public BrightnessFlyout(BrightnessService brightness)
    {
        _brightness = brightness;
        InitializeComponent();
        Sync();
        _brightness.PropertyChanged += OnBrightnessChanged;
        Unloaded += (_, _) => _brightness.PropertyChanged -= OnBrightnessChanged;
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ContentChanged;

    private void OnBrightnessChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Sync);

    private void Sync()
    {
        _syncing = true;
        // Don't fight the user while they're dragging the knob.
        if (!BrightnessSlider.IsMouseCaptureWithin)
            BrightnessSlider.Value = _brightness.Brightness;
        _syncing = false;
        BrightnessText.Text = $"{_brightness.Brightness}%";
        BrightnessSlider.IsEnabled = _brightness.HasBrightness;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing)
            _brightness.SetBrightness((int)Math.Round(e.NewValue));
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShellCommands.Launch("ms-settings:display");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
