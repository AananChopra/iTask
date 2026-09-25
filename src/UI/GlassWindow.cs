using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using iTask.Utilities;
using iTask.WindowsIntegration;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.UI;

/// <summary>
/// Frosted glass: a window that shows only a blur of whatever is behind it, clipped to a rounded
/// rectangle with anti-aliased corners. Content goes in a separate, per-pixel transparent window
/// owned by this one (so it always stays on top of its glass).
///
/// Uses the compositor's host-backdrop brush (the blur behind WinUI's acrylic). Unlike Windows 11's
/// system acrylic it doesn't depend on window activation, so it never flickers when you switch
/// apps. (The older SetWindowCompositionAttribute accent blur renders solid black on current
/// Windows 11 builds.)
/// It's a separate window because a compositor target hides WPF's own content in the same window.
/// </summary>
public sealed class GlassWindow : OverlayWindow
{
    private CompositionGeometricClip? _clip;
    private CompositionRoundedRectangleGeometry? _shape;
    private DesktopWindowTarget? _target;
    private RECT _bounds;
    private float _radius;

    public GlassWindow(string title)
    {
        Title = title;
        Content = new Grid(); // something for WPF to render, so the window uncloaks after its first frame
        IsHitTestVisible = false;
    }

    public override void ApplyTheme(ThemeService theme)
    {
        if (Source is null || _target is not null)
            return;
        var hwnd = Source.Handle;

        // Transparent everywhere outside the rounded clip.
        Source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int value = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
        value = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref value, sizeof(int));
        value = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_HOSTBACKDROPBRUSH, ref value, sizeof(int));

        try
        {
            var compositor = CompositionHost.Compositor;
            _target = CompositionHost.CreateTarget(hwnd);

            var root = compositor.CreateContainerVisual();
            root.RelativeSizeAdjustment = Vector2.One;
            _shape = compositor.CreateRoundedRectangleGeometry();
            _clip = compositor.CreateGeometricClip(_shape);
            root.Clip = _clip;

            var blur = compositor.CreateSpriteVisual();
            blur.RelativeSizeAdjustment = Vector2.One;
            blur.Brush = compositor.CreateHostBackdropBrush();
            root.Children.InsertAtTop(blur);
            _target.Root = root;
            UpdateShape();
        }
        catch (Exception ex)
        {
            Log.Error("Glass unavailable; surfaces will use their tint only", ex);
        }
    }

    /// <summary>Covers <paramref name="bounds"/> (physical px) with corners of <paramref name="radius"/> px.</summary>
    public void SetShape(RECT bounds, float radius)
    {
        if (bounds.Equals(_bounds) && radius == _radius)
            return;
        _bounds = bounds;
        _radius = radius;
        SetBounds(bounds);
        UpdateShape();
    }

    private void UpdateShape()
    {
        if (_shape is null || _bounds.Width <= 0)
            return;
        // The visual tree works in the window's physical pixels.
        _shape.Size = new Vector2(_bounds.Width, _bounds.Height);
        _shape.CornerRadius = new Vector2(_radius);
    }
}

/// <summary>One compositor (and the DispatcherQueue it needs) for the UI thread.</summary>
internal static class CompositionHost
{
    private static object? _queueController;
    private static Compositor? _compositor;

    public static Compositor Compositor
    {
        get
        {
            if (_compositor is null)
            {
                var options = new DispatcherQueueOptions
                {
                    dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                    threadType = 2,    // DQTYPE_THREAD_CURRENT
                    apartmentType = 2, // DQTAT_COM_STA
                };
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
                _queueController = controller;
                _compositor = new Compositor();
            }
            return _compositor;
        }
    }

    public static DesktopWindowTarget CreateTarget(IntPtr hwnd)
    {
        var unknown = ((WinRT.IWinRTObject)Compositor).NativeObject.ThisPtr;
        var iid = typeof(ICompositorDesktopInterop).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out var interopPtr));
        try
        {
            var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(interopPtr);
            interop.CreateDesktopWindowTarget(hwnd, false, out var targetPtr);
            return DesktopWindowTarget.FromAbi(targetPtr);
        }
        finally
        {
            Marshal.Release(interopPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] out object controller);

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
        void EnsureOnThread(uint threadId);
    }
}
