using System.Windows.Interop;
using System.Windows.Media;
using iTask.Configuration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.UI;

public enum CornerStyle
{
    Square,
    Rounded,
}

/// <summary>
/// DWM materials for a (non per-pixel-transparent) WPF window: solid, or Windows 11 system acrylic.
/// Frosted glass ("Blur") is not a window attribute; see <see cref="GlassWindow"/>.
/// </summary>
public static class Backdrop
{
    public static void Apply(HwndSource source, BackdropKind kind, bool isDark, CornerStyle corners, Color surface)
    {
        var hwnd = source.Handle;
        bool acrylic = kind == BackdropKind.Acrylic;

        // Let DWM's backdrop show through wherever WPF draws transparent pixels; solid surfaces are
        // cleared to their own color so no frame ever shows black.
        source.CompositionTarget.BackgroundColor = acrylic ? Colors.Transparent : surface;
        var margins = acrylic ? new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new MARGINS();
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        SetInt(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, isDark ? 1 : 0);
        SetInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, corners == CornerStyle.Rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND);
        // Rounded surfaces keep the native hairline border; square bars get none.
        SetInt(hwnd, DWMWA_BORDER_COLOR, corners == CornerStyle.Rounded ? DWMWA_COLOR_DEFAULT : DWMWA_COLOR_NONE);
        SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, acrylic ? DWMSBT_TRANSIENTWINDOW : DWMSBT_NONE);
    }

    private static void SetInt(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
