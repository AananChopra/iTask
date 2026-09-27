using System.Windows;
using System.Windows.Threading;
using iTask.Configuration;

namespace iTask.UI;

/// <summary>
/// The live settings object plus the iTask Settings window. Edits apply immediately: <see cref="Commit"/>
/// saves and raises <see cref="Changed"/> (debounced, so dragging a slider rebuilds the bars once).
/// </summary>
public sealed class SettingsService : IDisposable
{
    private readonly ThemeService _theme;
    private readonly DispatcherTimer _debounce;
    private SettingsWindow? _window;

    public SettingsService(AppSettings settings, ThemeService theme)
    {
        Current = settings;
        _theme = theme;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SettingsStore.Save(Current);
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public AppSettings Current { get; }

    /// <summary>Raised after settings that affect the bars changed (and were saved).</summary>
    public event EventHandler? Changed;

    public void Commit()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Persists without rebuilding anything (for settings the bars don't use).</summary>
    public void Save() => SettingsStore.Save(Current);

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new SettingsWindow(this, _theme);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        if (_debounce.IsEnabled)
        {
            // Don't lose a change made just before quitting.
            _debounce.Stop();
            SettingsStore.Save(Current);
        }
        _window?.Close();
    }
}
