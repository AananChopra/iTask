using System.Runtime.InteropServices;
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

/// <summary>Applies Windows 11 materials (acrylic, rounded corners, dark frame) to a WPF window.</summary>
public static class Backdrop
{
    public static void Apply(HwndSource source, BackdropKind kind, bool isDark, CornerStyle corners, Color surface)
    {
        var hwnd = source.Handle;

        // Let DWM's backdrop show through wherever WPF draws transparent pixels; solid surfaces are
        // cleared to their own color so no frame ever shows black.
        source.CompositionTarget.BackgroundColor = kind == BackdropKind.Solid ? surface : Colors.Transparent;
        var margins = kind == BackdropKind.Solid ? new MARGINS() : new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        SetInt(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, isDark ? 1 : 0);
        SetInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, corners == CornerStyle.Rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND);
        // Rounded surfaces keep the native hairline border; square bars get none.
        SetInt(hwnd, DWMWA_BORDER_COLOR, corners == CornerStyle.Rounded ? DWMWA_COLOR_DEFAULT : DWMWA_COLOR_NONE);

        switch (kind)
        {
            case BackdropKind.Acrylic:
                SetAccent(hwnd, ACCENT_DISABLED, 0);
                SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW);
                break;
            case BackdropKind.Blur:
                SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_NONE);
                // AABBGGRR — a faint tint; the WPF tint layer does the rest.
                SetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, isDark ? 0x40202020u : 0x40F3F3F3u);
                break;
            default:
                SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_NONE);
                SetAccent(hwnd, ACCENT_DISABLED, 0);
                break;
        }
    }

    private static void SetInt(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    private static void SetAccent(IntPtr hwnd, int state, uint color)
    {
        var accent = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = color };
        int size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
