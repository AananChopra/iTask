using System.Windows;
using System.Windows.Media;
using iTask.Configuration;
using Microsoft.Win32;

namespace iTask.UI;

/// <summary>
/// Tracks the Windows shell light/dark setting ("SystemUsesLightTheme", the one the taskbar follows)
/// and publishes theme brushes as application resources for DynamicResource lookups.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly AppearanceSettings _appearance;

    public ThemeService(AppearanceSettings appearance)
    {
        _appearance = appearance;
        IsDark = ReadIsDark();
        ApplyResources();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsDark { get; private set; }

    public BackdropKind TopBarBackdrop => _appearance.TopBarBackdrop;

    /// <summary>Base surface color for the current theme (also used behind solid surfaces).</summary>
    public Color Surface => IsDark ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xF3, 0xF3, 0xF3);

    public event EventHandler? Changed;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            return;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            bool dark = ReadIsDark();
            if (dark == IsDark)
                return;
            IsDark = dark;
            ApplyResources();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private static bool ReadIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("SystemUsesLightTheme") is int light ? light == 0 : true;
    }

    private void ApplyResources()
    {
        var res = Application.Current.Resources;
        Color surface = Surface;
        Color ink = IsDark ? Colors.White : Colors.Black;

        res["TextPrimaryBrush"] = Brush(ink, IsDark ? 0.92 : 0.90);
        res["TextSecondaryBrush"] = Brush(ink, IsDark ? 0.65 : 0.60);
        res["ItemHoverBrush"] = Brush(ink, IsDark ? 0.09 : 0.06);
        res["ItemPressedBrush"] = Brush(ink, IsDark ? 0.05 : 0.04);
        // Dock, from the macOS dock design: rgba(45,45,45) body, faint hairline border, white .8 dots.
        res["DockPanelBrush"] = Brush(IsDark ? Color.FromRgb(45, 45, 45) : Color.FromRgb(246, 246, 246), Clamp(_appearance.DockOpacity));
        res["DockBorderBrush"] = Brush(IsDark ? Colors.White : Colors.Black, IsDark ? 0.16 : 0.10);
        // Glass sheen: light falling on the top of the body, fading out by the middle.
        var sheen = new LinearGradientBrush(
            Color.FromArgb((byte)(IsDark ? 26 : 90), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90)
        { EndPoint = new System.Windows.Point(0, 0.6) };
        sheen.Freeze();
        res["DockSheenBrush"] = sheen;
        res["DockDotBrush"] = Brush(IsDark ? Colors.White : Colors.Black, IsDark ? 0.8 : 0.65);
        res["TopBarTintBrush"] = Brush(surface, TopBarBackdrop == BackdropKind.Solid ? 1.0 : Clamp(_appearance.TopBarOpacity));
        res["MenuBackgroundBrush"] = Brush(IsDark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0xF9, 0xF9, 0xF9), 1.0);
        res["MenuBorderBrush"] = Brush(Colors.Black, IsDark ? 0.45 : 0.10);
        res["MenuHoverBrush"] = Brush(ink, IsDark ? 0.08 : 0.05);
        res["SeparatorBrush"] = Brush(ink, IsDark ? 0.10 : 0.08);
    }

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);

    private static SolidColorBrush Brush(Color c, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
