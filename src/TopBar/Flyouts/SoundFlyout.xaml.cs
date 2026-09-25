using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using iTask.ShellIntegration;
using iTask.SystemInfo;

namespace iTask.TopBar.Flyouts;

/// <summary>Sound menu: volume slider, mute, and choosing the output device.</summary>
public partial class SoundFlyout : UserControl, IFlyoutContent
{
    private readonly AudioService _audio;
    private bool _syncing;

    public SoundFlyout(AudioService audio)
    {
        _audio = audio;
        InitializeComponent();
        Sync();
        _audio.PropertyChanged += OnAudioChanged;
        Unloaded += (_, _) => _audio.PropertyChanged -= OnAudioChanged;
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ContentChanged;

    private void OnAudioChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Sync);

    private void Sync()
    {
        _syncing = true;
        // Don't fight the user while they're dragging the knob.
        if (!VolumeSlider.IsMouseCaptureWithin)
            VolumeSlider.Value = _audio.Volume;
        _syncing = false;
        VolumeText.Text = _audio.IsMuted ? "Muted" : $"{_audio.Volume}%";
        Speaker.Level = _audio.Volume;
        Speaker.IsMuted = _audio.IsMuted;
        MuteButton.ToolTip = _audio.IsMuted ? "Unmute" : "Mute";
        Devices.ItemsSource = _audio.Devices;
        VolumeSlider.IsEnabled = _audio.HasDevice;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing)
            _audio.SetVolume((int)Math.Round(e.NewValue));
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => _audio.ToggleMute();

    private void Device_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AudioDevice { IsDefault: false } device)
            _audio.SetDefaultDevice(device.Id);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShellCommands.Launch("ms-settings:sound");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
