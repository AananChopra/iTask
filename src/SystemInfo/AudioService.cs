using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using iTask.Utilities;

namespace iTask.SystemInfo;

/// <summary>
/// Master volume and mute of the default playback device (Core Audio). Change callbacks keep it
/// current — including volume keys, other apps, and switching the default device.
/// </summary>
public sealed class AudioService : INotifyPropertyChanged, IDisposable
{
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const uint CLSCTX_ALL = 0x17;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly IMMDeviceEnumerator? _enumerator;
    private readonly DeviceNotifications _deviceNotifications;
    private readonly VolumeNotifications _volumeNotifications;
    private IAudioEndpointVolume? _endpoint;
    private Guid _eventContext = Guid.NewGuid();

    public AudioService()
    {
        _deviceNotifications = new DeviceNotifications(this);
        _volumeNotifications = new VolumeNotifications(this);
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(_deviceNotifications);
        }
        catch (Exception ex)
        {
            Log.Error("Core Audio unavailable", ex);
        }
        AttachDefaultDevice();
    }

    public bool HasDevice { get; private set; }
    /// <summary>0–100.</summary>
    public int Volume { get; private set; }
    public bool IsMuted { get; private set; }

    public string Description => !HasDevice ? "No audio output" : IsMuted ? "Muted" : $"Volume {Volume}%";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Nudges volume by <paramref name="delta"/> percentage points (e.g. from the scroll wheel).</summary>
    public void Adjust(int delta)
    {
        if (_endpoint is null)
            return;
        float level = Math.Clamp((Volume + delta) / 100f, 0f, 1f);
        _endpoint.SetMasterVolumeLevelScalar(level, ref _eventContext);
        if (IsMuted && delta > 0)
            _endpoint.SetMute(false, ref _eventContext);
        ReadCurrent();
    }

    public void ToggleMute()
    {
        if (_endpoint is null)
            return;
        _endpoint.SetMute(!IsMuted, ref _eventContext);
        ReadCurrent();
    }

    private void AttachDefaultDevice()
    {
        DetachEndpoint();
        if (_enumerator is null)
        {
            Publish(false, 0, false);
            return;
        }

        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) != 0 || device is null)
            {
                Publish(false, 0, false);
                return;
            }
            var iid = IID_IAudioEndpointVolume;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj));
            _endpoint = (IAudioEndpointVolume)obj;
            _endpoint.RegisterControlChangeNotify(_volumeNotifications);
            ReadCurrent();
        }
        catch (Exception ex)
        {
            Log.Error("Could not attach to the default audio device", ex);
            DetachEndpoint();
            Publish(false, 0, false);
        }
    }

    private void DetachEndpoint()
    {
        if (_endpoint is null)
            return;
        try { _endpoint.UnregisterControlChangeNotify(_volumeNotifications); } catch { /* device gone */ }
        Marshal.ReleaseComObject(_endpoint);
        _endpoint = null;
    }

    private void ReadCurrent()
    {
        if (_endpoint is null)
            return;
        _endpoint.GetMasterVolumeLevelScalar(out float level);
        _endpoint.GetMute(out bool muted);
        Publish(true, (int)Math.Round(level * 100), muted);
    }

    private void Publish(bool hasDevice, int volume, bool muted)
    {
        HasDevice = hasDevice;
        Volume = volume;
        IsMuted = muted;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    // Core Audio calls these on its own threads; hop to the UI thread.

    private void OnVolumeNotify(IntPtr data)
    {
        var n = Marshal.PtrToStructure<AudioVolumeNotificationData>(data);
        int volume = (int)Math.Round(n.fMasterVolume * 100);
        bool muted = n.bMuted;
        _dispatcher.BeginInvoke(() => Publish(true, volume, muted));
    }

    private void OnDefaultDeviceChanged() => _dispatcher.BeginInvoke(AttachDefaultDevice);

    public void Dispose()
    {
        DetachEndpoint();
        if (_enumerator is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_deviceNotifications); } catch { }
            Marshal.ReleaseComObject(_enumerator);
        }
    }

    [ComVisible(true)]
    private sealed class VolumeNotifications : IAudioEndpointVolumeCallback
    {
        private readonly AudioService _owner;
        public VolumeNotifications(AudioService owner) => _owner = owner;

        public int OnNotify(IntPtr notificationData)
        {
            _owner.OnVolumeNotify(notificationData);
            return 0;
        }
    }

    [ComVisible(true)]
    private sealed class DeviceNotifications : IMMNotificationClient
    {
        private readonly AudioService _owner;
        public DeviceNotifications(AudioService owner) => _owner = owner;

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.eRender && role == ERole.eMultimedia)
                _owner.OnDefaultDeviceChanged();
        }

        public void OnDeviceStateChanged(string deviceId, uint newState) { }
        public void OnDeviceAdded(string deviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}
