using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace iTask.SystemInfo;

/// <summary>
/// Current date/time formatted with the user's regional settings. Ticks once per minute boundary
/// (not every second) and refreshes immediately on clock, time-zone, locale, or resume events.
/// </summary>
public sealed class ClockService : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherTimer _timer;

    public ClockService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += (_, _) => Update();
        SystemEvents.TimeChanged += OnSystemChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Update();
    }

    public string TimeText { get; private set; } = "";
    public string DateText { get; private set; } = "";
    public string LongDateText { get; private set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnSystemChanged(object? sender, EventArgs e) => _timer.Dispatcher.BeginInvoke(Refresh);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _timer.Dispatcher.BeginInvoke(Refresh);
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Locale)
            _timer.Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture.ClearCachedData();
        Update();
    }

    private void Update()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;

        TimeText = now.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        DateText = $"{now.Day}{OrdinalSuffix(now.Day)} {now.ToString("MMMM", culture)}";
        LongDateText = now.ToString(culture.DateTimeFormat.LongDatePattern, culture);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

        // Re-arm for just after the next minute boundary.
        var next = now.AddMinutes(1);
        next = new DateTime(next.Year, next.Month, next.Day, next.Hour, next.Minute, 0, next.Kind);
        _timer.Stop();
        _timer.Interval = next - now + TimeSpan.FromMilliseconds(50);
        _timer.Start();
    }

    /// <summary>1st, 2nd, 3rd, 4th … 11th, 12th, 13th … 21st.</summary>
    public static string OrdinalSuffix(int day) => (day % 100) switch
    {
        11 or 12 or 13 => "th",
        _ => (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" },
    };

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.TimeChanged -= OnSystemChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }
}
