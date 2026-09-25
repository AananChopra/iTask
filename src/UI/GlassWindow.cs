using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using iTask.Utilities;
using iTask.WindowsIntegration;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using static iTask.WindowsIntegration.NativeMethods;
using WinColor = Windows.UI.Color;

namespace iTask.UI;

/// <summary>How a glass surface is drawn over its blur. All colors are straight (non-premultiplied) ARGB.</summary>
public sealed record GlassStyle(System.Windows.Media.Color Tint, System.Windows.Media.Color Border, bool Sheen);

/// <summary>
/// Frosted glass: a window that shows a blur of whatever is behind it, optionally tinted, with a
/// top sheen and a hairline border — all clipped to a rounded rectangle with anti-aliased corners.
/// Content goes in a separate, per-pixel transparent window owned by this one (so it always stays
/// on top of its glass).
///
/// Everything is drawn by the compositor in one visual tree, so blur, tint and border always move
/// and resize together. The shape can animate inside a larger window (<see cref="SetShape"/>),
/// which avoids resizing the window every frame.
///
/// Uses the compositor's host-backdrop brush (the blur behind WinUI's acrylic). Unlike Windows 11's
/// system acrylic it doesn't depend on window activation, so it never flickers when you switch
/// apps. (The older SetWindowCompositionAttribute accent blur renders solid black on current
/// Windows 11 builds.) It's a separate window because a compositor target hides WPF's own content
/// in the same window.
/// </summary>
public sealed class GlassWindow : OverlayWindow
{
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private CompositionRoundedRectangleGeometry? _clipShape;
    private CompositionRoundedRectangleGeometry? _borderShape;
    private SpriteVisual? _tint;
    private SpriteVisual? _sheen;
    private CompositionSpriteShape? _border;
    private ShapeVisual? _borderVisual;
    private RECT _window;
    private RECT _shape;
    private float _radius;
    private float _scale = 1;

    public GlassWindow(string title)
    {
        Title = title;
        Content = new Grid(); // something for WPF to render, so the window uncloaks after its first frame
        IsHitTestVisible = false;
    }

    /// <summary>Tint/border/sheen drawn over the blur; null = plain blur.</summary>
    public GlassStyle? GlassLook { get; set; }

    public override void ApplyTheme(ThemeService theme)
    {
        if (Source is null)
            return;
        if (_target is null && !CreateVisuals())
            return;
        ApplyStyle();
    }

    private bool CreateVisuals()
    {
        var hwnd = Source!.Handle;

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
            var c = CompositionHost.Compositor;
            _target = CompositionHost.CreateTarget(hwnd);

            _root = c.CreateContainerVisual();
            _clipShape = c.CreateRoundedRectangleGeometry();
            _root.Clip = c.CreateGeometricClip(_clipShape);

            var blur = c.CreateSpriteVisual();
            blur.RelativeSizeAdjustment = Vector2.One;
            blur.Brush = c.CreateHostBackdropBrush();
            _root.Children.InsertAtTop(blur);

            _tint = c.CreateSpriteVisual();
            _tint.RelativeSizeAdjustment = Vector2.One;
            _root.Children.InsertAtTop(_tint);

            _sheen = c.CreateSpriteVisual();
            _sheen.RelativeSizeAdjustment = Vector2.One;
            var gradient = c.CreateLinearGradientBrush();
            gradient.StartPoint = new Vector2(0, 0);
            gradient.EndPoint = new Vector2(0, 0.6f);
            gradient.ColorStops.Add(c.CreateColorGradientStop(0, WinColor.FromArgb(0x1A, 255, 255, 255)));
            gradient.ColorStops.Add(c.CreateColorGradientStop(1, WinColor.FromArgb(0, 255, 255, 255)));
            _sheen.Brush = gradient;
            _root.Children.InsertAtTop(_sheen);

            _borderVisual = c.CreateShapeVisual();
            _borderVisual.RelativeSizeAdjustment = Vector2.One;
            _borderShape = c.CreateRoundedRectangleGeometry();
            _border = c.CreateSpriteShape(_borderShape);
            _borderVisual.Shapes.Add(_border);
            _root.Children.InsertAtTop(_borderVisual);

            _target.Root = _root;
            UpdateShape();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Glass unavailable; surfaces will use their tint only", ex);
            _target = null;
            return false;
        }
    }

    private void ApplyStyle()
    {
        if (_tint is null || _sheen is null || _border is null)
            return;
        var c = CompositionHost.Compositor;
        var style = GlassLook;
        _tint.Brush = style is null ? null : c.CreateColorBrush(ToWin(style.Tint));
        _sheen.IsVisible = style?.Sheen == true;
        _border.StrokeBrush = style is null ? null : c.CreateColorBrush(ToWin(style.Border));
    }

    /// <summary>
    /// Places the window at <paramref name="window"/> and the visible glass at <paramref name="shape"/>
    /// (both physical px, screen coordinates; the shape must lie inside the window), with corners of
    /// <paramref name="radius"/> px. Moving only the shape is cheap; the window is only moved or
    /// resized when <paramref name="window"/> changes.
    /// </summary>
    public void SetShape(RECT window, RECT shape, float radius, float scale = 1, bool alreadyPlaced = false)
    {
        if (!window.Equals(_window))
        {
            _window = window;
            if (alreadyPlaced)
                NoteBounds(window); // moved together with its owner in one batched update
            else
                SetBounds(window);
        }
        _shape = shape;
        _radius = radius;
        _scale = scale;
        UpdateShape();
    }

    /// <summary>Glass covering exactly <paramref name="bounds"/>.</summary>
    public void SetShape(RECT bounds, float radius, float scale = 1) => SetShape(bounds, bounds, radius, scale);

    private void UpdateShape()
    {
        if (_root is null || _clipShape is null || _borderShape is null || _border is null || _shape.Width <= 0)
            return;
        // Visuals live in the window's physical-pixel space.
        var offset = new Vector3(_shape.Left - _window.Left, _shape.Top - _window.Top, 0);
        var size = new Vector2(_shape.Width, _shape.Height);
        _root.Offset = offset;
        _root.Size = size;
        _clipShape.Size = size;
        _clipShape.CornerRadius = new Vector2(_radius);

        // Hairline border, drawn on the pixel inside the edge.
        float stroke = Math.Max(1, (float)Math.Round(_scale));
        _border.StrokeThickness = stroke;
        _borderShape.Offset = new Vector2(stroke / 2);
        _borderShape.Size = size - new Vector2(stroke);
        _borderShape.CornerRadius = new Vector2(Math.Max(0, _radius - stroke / 2));
    }

    private static WinColor ToWin(System.Windows.Media.Color c) => WinColor.FromArgb(c.A, c.R, c.G, c.B);
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
