namespace iTask.Configuration;

public enum BackdropKind
{
    /// <summary>Windows 11 system acrylic (DWM system backdrop). Tied to window activation.</summary>
    Acrylic,
    /// <summary>Acrylic blur-behind via the accent API; steady regardless of activation.</summary>
    Blur,
    /// <summary>No transparency.</summary>
    Solid,
}

public enum DockVisibility
{
    /// <summary>Hidden while the active app is maximized; revealed by pushing the cursor to the bottom edge.</summary>
    Smart,
    /// <summary>Always shown.</summary>
    AlwaysVisible,
}

public sealed class AppSettings
{
    public const int CurrentVersion = 4;

    /// <summary>Schema version of the saved file (0 = written before versioning existed).</summary>
    public int SettingsVersion { get; set; }

    public TopBarSettings TopBar { get; set; } = new();
    public DockSettings Dock { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public ShellSettings Shell { get; set; } = new();
}

public sealed class TopBarSettings
{
    /// <summary>Height in device-independent pixels.</summary>
    public double Height { get; set; } = 28;
    /// <summary>Show icons of open apps at the left of the top bar.</summary>
    public bool ShowRunningApps { get; set; } = true;
    /// <summary>Offer other apps' notification-area (tray) icons in a dropdown on the right.</summary>
    public bool ShowTrayIcons { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowVolume { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowTime { get; set; } = true;
}

public sealed class DockSettings
{
    /// <summary>Icon size in DIPs. The dock body is this plus padding (12%, min 8) on each side.</summary>
    public double IconSize { get; set; } = 48;
    /// <summary>Peak hover magnification (1 = off). macOS-style cosine falloff.</summary>
    public double Magnification { get; set; } = 1.8;
    /// <summary>Width in DIPs of the magnification wave around the cursor.</summary>
    public double MagnificationRange { get; set; } = 240;
    /// <summary>Gap between the dock and the bottom edge of the screen, in DIPs.</summary>
    public double BottomMargin { get; set; } = 8;
    public DockVisibility Visibility { get; set; } = DockVisibility.Smart;
    public bool ShowRunningApps { get; set; } = true;
    /// <summary>
    /// With <see cref="DockVisibility.AlwaysVisible"/>: reserve screen space so maximized windows stop
    /// above the dock. Ignored in Smart mode (maximized windows use the full height there).
    /// </summary>
    public bool ReserveSpace { get; set; } = true;
    public List<PinnedApp> PinnedApps { get; set; } = new();
}

public sealed class PinnedApp
{
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
    public string? Name { get; set; }
}

public sealed class AppearanceSettings
{
    /// <summary>
    /// Top bar material. "Blur" is frosted glass that ignores window activation; avoid "Acrylic"
    /// (Windows' system acrylic follows activation, and our bars never activate, so it flickers).
    /// </summary>
    public BackdropKind TopBarBackdrop { get; set; } = BackdropKind.Blur;
    /// <summary>Frosted-glass blur behind the dock body.</summary>
    public bool DockGlass { get; set; } = true;
    /// <summary>Opacity (0–1) of the dock body's tint over the glass; 1 = solid.</summary>
    public double DockOpacity { get; set; } = 0.5;
    /// <summary>Opacity (0–1) of the tint over the top bar glass.</summary>
    public double TopBarOpacity { get; set; } = 0.45;
}

public sealed class ShellSettings
{
    public bool HideNativeTaskbar { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
}
