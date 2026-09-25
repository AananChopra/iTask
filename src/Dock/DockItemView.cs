using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using iTask.WindowsIntegration;

namespace iTask.Dock;

/// <summary>
/// One dock icon at a fixed base size. Magnification is a render transform over a cached bitmap
/// (GPU-scaled, no re-layout or re-rasterizing per frame), anchored at the bottom center.
/// The running dot is a separate element so it doesn't grow with the icon.
/// </summary>
internal sealed class DockItemView : Grid
{
    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _bounce = new();
    private readonly Image? _image;

    private DockItemView(UIElement artwork, string name, double size, double maxScale)
    {
        Width = Height = size;
        Children.Add(artwork);
        artwork.Effect = new DropShadowEffect
        {
            Direction = 270, ShadowDepth = Math.Max(1, size * 0.03), BlurRadius = Math.Max(4, size * 0.12),
            Opacity = 0.3, Color = Colors.Black, RenderingBias = RenderingBias.Performance,
        };

        RenderTransformOrigin = new Point(0.5, 1);
        RenderTransform = new TransformGroup { Children = { _scale, _bounce } };
        // Rasterize once at the largest size it will be shown at; magnifying then just scales pixels.
        CacheMode = new BitmapCache(Math.Max(1, maxScale)) { SnapsToDevicePixels = false };

        Background = Brushes.Transparent; // hit-testable across the whole square
        ToolTip = name;
        ToolTipService.SetPlacement(this, PlacementMode.Top);
        ToolTipService.SetInitialShowDelay(this, 300);
        _image = artwork as Image;

        double dot = Math.Max(3, size * 0.06);
        Dot = new Ellipse { Width = dot, Height = dot, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Dot.SetResourceReference(Shape.FillProperty, "DockDotBrush");
    }

    public RunningApp? App { get; private set; }
    public bool IsStart { get; private init; }

    /// <summary>Running indicator; the dock places it under the icon.</summary>
    public Ellipse Dot { get; }

    public static DockItemView ForApp(RunningApp app, double size, double maxScale)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var view = new DockItemView(image, app.Name, size, maxScale) { App = app };
        view.Refresh();
        return view;
    }

    public static DockItemView ForStart(double size, double maxScale)
    {
        // Windows logo, sized like artwork inside a macOS icon's safe area.
        var logo = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(Math.Round(size * 0.2)) };
        var blue = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        blue.Freeze();
        double gap = Math.Max(1, size * 0.02);
        foreach (var margin in new[] { new Thickness(0, 0, gap, gap), new Thickness(gap, 0, 0, gap), new Thickness(0, gap, gap, 0), new Thickness(gap, gap, 0, 0) })
            logo.Children.Add(new Border { Background = blue, CornerRadius = new CornerRadius(1.5), Margin = margin });
        return new DockItemView(logo, "Start", size, maxScale) { IsStart = true };
    }

    /// <summary>Re-reads name/icon from the app (called when the running-apps list updates).</summary>
    public void Refresh()
    {
        if (App is null || _image is null)
            return;
        _image.Source = App.LargeIcon;
        ToolTip = App.Name;
        Dot.Visibility = Visibility.Visible; // everything but Start is a running app
    }

    public void SetScale(double scale) => _scale.ScaleX = _scale.ScaleY = scale;

    /// <summary>The design's click bounce: up and back, 0.2 s each way, ease-out.</summary>
    public void Bounce(double height)
    {
        var anim = new DoubleAnimation(0, -height, TimeSpan.FromSeconds(0.2))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        _bounce.BeginAnimation(TranslateTransform.YProperty, anim);
    }
}
