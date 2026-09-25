using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using iTask.Utilities;

namespace iTask.SystemInfo;

/// <summary>
/// Master volume and mute of the default playback device (Core Audio). Change callbacks keep it
/// current — including volume keys, other apps, and switching the default device.
/// </summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

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

    /// <summary>Active playback devices; the default one is flagged.</summary>
    public IReadOnlyList<AudioDevice> Devices { get; private set; } = Array.Empty<AudioDevice>();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets the master volume (0–100); unmutes when raising it from zero-ish.</summary>
    public void SetVolume(int percent)
    {
        if (_endpoint is null)
            return;
        float level = Math.Clamp(percent / 100f, 0f, 1f);
        _endpoint.SetMasterVolumeLevelScalar(level, ref _eventContext);
        if (IsMuted && percent > 0)
            _endpoint.SetMute(false, ref _eventContext);
        ReadCurrent();
    }

    /// <summary>Makes <paramref name="deviceId"/> the default output for all roles.</summary>
    public void SetDefaultDevice(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the default audio device", ex);
        }
    }

    private void RefreshDevices()
    {
        var devices = new List<AudioDevice>();
        if (_enumerator is not null)
        {
            try
            {
                string? defaultId = null;
                if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var def) == 0 && def is not null)
                {
                    def.GetId(out defaultId);
                    Marshal.ReleaseComObject(def);
                }
                if (_enumerator.EnumAudioEndpoints(EDataFlow.eRender, 1 /* DEVICE_STATE_ACTIVE */, out var collection) == 0)
                {
                    collection.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out var device) != 0 || device is null)
                            continue;
                        device.GetId(out string id);
                        devices.Add(new AudioDevice(id, FriendlyName(device) ?? "Audio device", id == defaultId));
                        Marshal.ReleaseComObject(device);
                    }
                    Marshal.ReleaseComObject(collection);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not list audio devices", ex);
            }
        }
        Devices = devices;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Devices)));
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0 || store is null)
            return null;
        try
        {
            var key = PropertyKeys.DeviceFriendlyName;
            if (store.GetValue(ref key, out var value) != 0)
                return null;
            try
            {
                return value.vt == PropVariant.VT_LPWSTR ? Marshal.PtrToStringUni(value.pointer) : null;
            }
            finally
            {
                PropertyKeys.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

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
        RefreshDevices();
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

    private void OnDevicesChanged() => _dispatcher.BeginInvoke(RefreshDevices);

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

        public void OnDeviceStateChanged(string deviceId, uint newState) => _owner.OnDevicesChanged();
        public void OnDeviceAdded(string deviceId) => _owner.OnDevicesChanged();
        public void OnDeviceRemoved(string deviceId) => _owner.OnDevicesChanged();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}
