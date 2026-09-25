using System.ComponentModel;
using iTask.WindowsIntegration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.SystemInfo;

/// <summary>
/// Battery state from GetSystemPowerStatus, refreshed on power notifications
/// (percentage, AC/DC and battery-saver changes) — no polling.
/// </summary>
public sealed class BatteryService : INotifyPropertyChanged, IDisposable
{
    private readonly MessageWindow _window = new("iTask.Power");
    private readonly List<IntPtr> _registrations = new();

    public BatteryService()
    {
        foreach (var guid in new[] { GUID_BATTERY_PERCENTAGE_REMAINING, GUID_ACDC_POWER_SOURCE, GUID_POWER_SAVING_STATUS })
        {
            var g = guid;
            var handle = RegisterPowerSettingNotification(_window.Handle, ref g, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
            if (handle != IntPtr.Zero)
                _registrations.Add(handle);
        }
        _window.MessageReceived += OnMessage;
        Refresh();
    }

    public bool HasBattery { get; private set; }
    /// <summary>0–100.</summary>
    public int Percent { get; private set; }
    public bool IsPluggedIn { get; private set; }
    public bool IsCharging { get; private set; }
    public bool IsSaverOn { get; private set; }
    /// <summary>Estimated time remaining on battery, when Windows knows it.</summary>
    public TimeSpan? TimeRemaining { get; private set; }

    public string Description
    {
        get
        {
            if (!HasBattery)
                return "No battery";
            var text = $"{Percent}%";
            if (IsCharging)
                text += " — charging";
            else if (IsPluggedIn)
                text += " — plugged in";
            else if (TimeRemaining is { } left)
                text += $" — {(int)left.TotalHours} hr {left.Minutes:00} min remaining";
            if (IsSaverOn)
                text += " (battery saver)";
            return text;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_POWERBROADCAST && (wParam.ToInt32() == PBT_POWERSETTINGCHANGE || wParam.ToInt32() == PBT_APMPOWERSTATUSCHANGE))
            Refresh();
    }

    private void Refresh()
    {
        if (!GetSystemPowerStatus(out var s))
            return;

        HasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255 && s.BatteryLifePercent <= 100;
        Percent = Math.Clamp((int)s.BatteryLifePercent, 0, 100);
        IsPluggedIn = s.ACLineStatus == 1;
        IsCharging = (s.BatteryFlag & 8) != 0;
        IsSaverOn = s.SystemStatusFlag == 1;
        TimeRemaining = !IsPluggedIn && s.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Dispose()
    {
        foreach (var handle in _registrations)
            UnregisterPowerSettingNotification(handle);
        _registrations.Clear();
        _window.Dispose();
    }
}
