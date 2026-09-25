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
/// One dock icon: artwork with a scale-dependent drop shadow, an optional running dot, and the
/// click bounce. Size and position are driven every frame by <see cref="DockWindow"/>.
/// </summary>
internal sealed class DockItemView : Grid
{
    private readonly DropShadowEffect _shadow;
    private readonly TranslateTransform _bounce = new();
    private readonly Ellipse _dot;
    private readonly Image? _image;

    private DockItemView(UIElement artwork, string name)
    {
        _shadow = new DropShadowEffect { Direction = 270, Color = Colors.Black, RenderingBias = RenderingBias.Performance };
        if (artwork is FrameworkElement fe)
            fe.Effect = _shadow;
        Children.Add(artwork);

        _dot = new Ellipse
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.3, Color = Colors.Black },
        };
        _dot.SetResourceReference(Shape.FillProperty, "DockDotBrush");
        Children.Add(_dot);

        RenderTransform = _bounce;
        Background = Brushes.Transparent; // hit-testable across the whole square
        ToolTip = name;
        ToolTipService.SetPlacement(this, PlacementMode.Top);
        ToolTipService.SetInitialShowDelay(this, 300);
        _image = artwork as Image;
    }

    public RunningApp? App { get; private set; }
    public bool IsStart { get; private init; }

    public static DockItemView ForApp(RunningApp app)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var view = new DockItemView(image, app.Name) { App = app };
        view.Refresh();
        return view;
    }

    public static DockItemView ForStart()
    {
        // Windows logo, sized like artwork inside a macOS icon's safe area.
        var logo = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(14) };
        var blue = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        blue.Freeze();
        foreach (var margin in new[] { new Thickness(0, 0, 1.5, 1.5), new Thickness(1.5, 0, 0, 1.5), new Thickness(0, 1.5, 1.5, 0), new Thickness(1.5, 1.5, 0, 0) })
            logo.Children.Add(new Border { Background = blue, CornerRadius = new CornerRadius(1.5), Margin = margin });
        return new DockItemView(logo, "Start") { IsStart = true };
    }

    /// <summary>Re-reads name/icon from the app (called when the running-apps list updates).</summary>
    public void Refresh()
    {
        if (App is null || _image is null)
            return;
        _image.Source = App.LargeIcon;
        ToolTip = App.Name;
        _dot.Visibility = Visibility.Visible; // everything but Start is a running app
    }

    /// <summary>Applies the per-frame scale-dependent styling from the design.</summary>
    public void ApplyScale(double scale, double icon)
    {
        bool lifted = scale > 1.2;
        _shadow.ShadowDepth = lifted ? Math.Max(2, icon * 0.05) : Math.Max(1, icon * 0.03);
        _shadow.BlurRadius = 2 * (lifted ? Math.Max(4, icon * 0.1) : Math.Max(2, icon * 0.06));
        _shadow.Opacity = 0.2 + (scale - 1) * 0.15;

        double dot = Math.Max(3, icon * 0.06);
        _dot.Width = _dot.Height = dot;
        _dot.Margin = new Thickness(0, 0, 0, Math.Max(-2, -icon * 0.05));
    }

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
