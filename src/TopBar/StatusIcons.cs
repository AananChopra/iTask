using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace iTask.TopBar;

/// <summary>Base for small vector status icons drawn in the current (inherited) foreground color.</summary>
public abstract class StatusIcon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(StatusIcon),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected static DependencyProperty Register<T>(string name, Type owner, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), owner,
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    protected Brush WithOpacity(double opacity)
    {
        var b = Foreground.Clone();
        b.Opacity = opacity;
        b.Freeze();
        return b;
    }

    protected Pen Stroke(double thickness, double opacity = 1) =>
        new(WithOpacity(opacity), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    /// <summary>An arc centered on "up", <paramref name="sweep"/> degrees wide.</summary>
    protected static Geometry Arc(Point center, double radius, double sweep)
    {
        double half = sweep / 2 * Math.PI / 180;
        var start = new Point(center.X - radius * Math.Sin(half), center.Y - radius * Math.Cos(half));
        var end = new Point(center.X + radius * Math.Sin(half), center.Y - radius * Math.Cos(half));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Wi-Fi fan (dot + 3 arcs), or an Ethernet glyph.</summary>
public sealed class NetworkIcon : StatusIcon
{
    public static readonly DependencyProperty BarsProperty = Register("Bars", typeof(NetworkIcon), 4);
    public static readonly DependencyProperty IsConnectedProperty = Register("IsConnected", typeof(NetworkIcon), true);
    public static readonly DependencyProperty IsWiredProperty = Register("IsWired", typeof(NetworkIcon), false);
    public static readonly DependencyProperty HasInternetProperty = Register("HasInternet", typeof(NetworkIcon), true);

    public int Bars { get => (int)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public bool IsConnected { get => (bool)GetValue(IsConnectedProperty); set => SetValue(IsConnectedProperty, value); }
    public bool IsWired { get => (bool)GetValue(IsWiredProperty); set => SetValue(IsWiredProperty, value); }
    public bool HasInternet { get => (bool)GetValue(HasInternetProperty); set => SetValue(HasInternetProperty, value); }

    public NetworkIcon()
    {
        Width = 18;
        Height = 14;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double dim = 0.28;
        double lit = HasInternet ? 1 : 0.55;

        if (IsWired && IsConnected)
        {
            // Ethernet: ‹ • • ›
            var pen = Stroke(1.6, lit);
            double cy = Height / 2;
            dc.DrawLine(pen, new Point(5, cy - 4), new Point(1.5, cy));
            dc.DrawLine(pen, new Point(1.5, cy), new Point(5, cy + 4));
            dc.DrawLine(pen, new Point(Width - 5, cy - 4), new Point(Width - 1.5, cy));
            dc.DrawLine(pen, new Point(Width - 1.5, cy), new Point(Width - 5, cy + 4));
            var fill = WithOpacity(lit);
            dc.DrawEllipse(fill, null, new Point(Width / 2 - 2.5, cy), 1.3, 1.3);
            dc.DrawEllipse(fill, null, new Point(Width / 2 + 2.5, cy), 1.3, 1.3);
            return;
        }

        var origin = new Point(Width / 2, Height - 1.5);
        int bars = IsConnected ? Math.Clamp(Bars, 0, 4) : 0;
        dc.DrawEllipse(WithOpacity(bars >= 1 ? lit : dim), null, origin, 1.6, 1.6);
        double[] radii = { 5.0, 8.6, 12.2 };
        for (int i = 0; i < radii.Length; i++)
            dc.DrawGeometry(null, Stroke(1.8, bars >= i + 2 ? lit : dim), Arc(origin, radii[i], 92));
    }
}

/// <summary>Speaker with 0–3 sound waves, or a slash when muted.</summary>
public sealed class VolumeIcon : StatusIcon
{
    public static readonly DependencyProperty LevelProperty = Register("Level", typeof(VolumeIcon), 50);
    public static readonly DependencyProperty IsMutedProperty = Register("IsMuted", typeof(VolumeIcon), false);

    public int Level { get => (int)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }

    public VolumeIcon()
    {
        Width = 19;
        Height = 14;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double cy = Height / 2;
        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(1, cy - 2.6), true, true);
            ctx.LineTo(new Point(4, cy - 2.6), true, false);
            ctx.LineTo(new Point(8, cy - 6), true, false);
            ctx.LineTo(new Point(8, cy + 6), true, false);
            ctx.LineTo(new Point(4, cy + 2.6), true, false);
            ctx.LineTo(new Point(1, cy + 2.6), true, false);
        }
        body.Freeze();
        dc.DrawGeometry(WithOpacity(1), new Pen(WithOpacity(1), 0.8) { LineJoin = PenLineJoin.Round }, body);

        if (IsMuted)
        {
            var pen = Stroke(1.6);
            dc.DrawLine(pen, new Point(11.5, cy - 3), new Point(17.5, cy + 3));
            dc.DrawLine(pen, new Point(17.5, cy - 3), new Point(11.5, cy + 3));
            return;
        }

        int waves = Level <= 0 ? 0 : Level < 34 ? 1 : Level < 67 ? 2 : 3;
        var center = new Point(7, cy);
        double[] radii = { 3.6, 6.8, 10.0 };
        for (int i = 0; i < radii.Length; i++)
        {
            // Rotate an "up" arc to face right.
            var arc = Arc(center, radii[i], 80).Clone();
            arc.Transform = new RotateTransform(90, center.X, center.Y);
            arc.Freeze();
            dc.DrawGeometry(null, Stroke(1.6, i < waves ? 1 : 0.28), arc);
        }
    }
}

/// <summary>macOS-style battery: outline, proportional fill, terminal nub, charging bolt.</summary>
public sealed class BatteryIcon : StatusIcon
{
    private static readonly Brush LowBrush = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush SaverBrush = Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A));

    public static readonly DependencyProperty PercentProperty = Register("Percent", typeof(BatteryIcon), 100);
    public static readonly DependencyProperty IsChargingProperty = Register("IsCharging", typeof(BatteryIcon), false);
    public static readonly DependencyProperty IsPluggedInProperty = Register("IsPluggedIn", typeof(BatteryIcon), false);
    public static readonly DependencyProperty IsSaverOnProperty = Register("IsSaverOn", typeof(BatteryIcon), false);

    public int Percent { get => (int)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool IsCharging { get => (bool)GetValue(IsChargingProperty); set => SetValue(IsChargingProperty, value); }
    public bool IsPluggedIn { get => (bool)GetValue(IsPluggedInProperty); set => SetValue(IsPluggedInProperty, value); }
    public bool IsSaverOn { get => (bool)GetValue(IsSaverOnProperty); set => SetValue(IsSaverOnProperty, value); }

    public BatteryIcon()
    {
        Width = 26;
        Height = 13;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bodyRect = new Rect(0.75, 0.75, Width - 4, Height - 1.5);
        dc.DrawRoundedRectangle(null, new Pen(WithOpacity(0.45), 1.1), bodyRect, 3.2, 3.2);
        dc.DrawRoundedRectangle(WithOpacity(0.45), null, new Rect(Width - 2.6, Height / 2 - 2.2, 1.9, 4.4), 0.9, 0.9);

        bool powered = IsCharging || IsPluggedIn;
        Brush fill = IsSaverOn ? SaverBrush
            : Percent <= 20 && !powered ? LowBrush
            : WithOpacity(powered ? 0.55 : 1);
        var inner = new Rect(bodyRect.X + 1.6, bodyRect.Y + 1.6, bodyRect.Width - 3.2, bodyRect.Height - 3.2);
        double w = Math.Max(1.5, inner.Width * Math.Clamp(Percent, 0, 100) / 100.0);
        dc.DrawRoundedRectangle(fill, null, new Rect(inner.X, inner.Y, w, inner.Height), 1.6, 1.6);

        if (powered)
        {
            // Lightning bolt, centered on the body.
            double cx = bodyRect.X + bodyRect.Width / 2, cy = Height / 2;
            var bolt = new StreamGeometry();
            using (var ctx = bolt.Open())
            {
                ctx.BeginFigure(new Point(cx + 1.2, cy - 5.2), true, true);
                ctx.LineTo(new Point(cx - 3.2, cy + 0.8), true, false);
                ctx.LineTo(new Point(cx - 0.3, cy + 0.8), true, false);
                ctx.LineTo(new Point(cx - 1.2, cy + 5.2), true, false);
                ctx.LineTo(new Point(cx + 3.2, cy - 0.8), true, false);
                ctx.LineTo(new Point(cx + 0.3, cy - 0.8), true, false);
            }
            bolt.Freeze();
            dc.DrawGeometry(WithOpacity(1), new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 0.6), bolt);
        }
    }
}
