using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using iTask.UI;

namespace iTask.TopBar.Flyouts;

/// <summary>Content of a top bar dropdown; asks to be closed after acting (e.g. opening Settings).</summary>
public interface IFlyoutContent
{
    event EventHandler? CloseRequested;

    /// <summary>Raised after the content changed in a way that may change its size (e.g. a rescan).</summary>
    event EventHandler? ContentChanged;
}

/// <summary>A macOS-style menu: rounded frosted glass, never activated, fading/growing in from its item.</summary>
public sealed class FlyoutWindow : OverlayWindow
{
    private const float StartScale = 0.95f;
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(120);

    private readonly Border _root;
    private readonly ScaleTransform _scale = new(1, 1);

    public FlyoutWindow(FrameworkElement content, double width)
    {
        UseGlass();
        Title = "iTask Menu";
        _root = new Border
        {
            // Alpha 1/255: invisible, but keeps the whole menu clickable (fully transparent pixels
            // of a per-pixel transparent window pass clicks through to whatever is underneath).
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Padding = new Thickness(5, 5, 5, 6),
            Width = width,
            Child = content,
            RenderTransform = _scale,
        };
        Content = _root;
    }

    protected override double GlassCornerRadius => 11;

    protected override GlassStyle GetGlassStyle(ThemeService theme) => theme.IsDark
        ? new GlassStyle(Color.FromArgb(214, 32, 32, 32), Color.FromArgb(41, 255, 255, 255), Sheen: false)
        : new GlassStyle(Color.FromArgb(222, 246, 246, 246), Color.FromArgb(26, 0, 0, 0), Sheen: false);

    /// <summary>Desired size in DIPs.</summary>
    public Size MeasureContent()
    {
        _root.Measure(new Size(_root.Width, double.PositiveInfinity));
        return _root.DesiredSize;
    }

    private Point? _pendingIn;

    /// <summary>Fades and grows in from <paramref name="origin"/> (0–1 within the menu: where its item is).</summary>
    public void AnimateIn(Point origin)
    {
        // Start invisible, and only animate once the first frame is really on screen: the window
        // stays cloaked for its first frames, which would otherwise eat a third of the animation.
        _root.Opacity = 0;
        _root.RenderTransformOrigin = origin;
        _scale.ScaleX = _scale.ScaleY = StartScale;
        Glass?.SetAppearance(0, StartScale, new Vector2((float)origin.X, (float)origin.Y));
        _pendingIn = origin;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e); // schedules the uncloak at Render priority
        if (_pendingIn is not { } origin)
            return;
        _pendingIn = null;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
            () => Run(0, 1, StartScale, 1, origin, OpenDuration, easeIn: false, done: null));
    }

    /// <summary>Fades out (and shrinks a touch towards its origin), then calls <paramref name="done"/>.</summary>
    public void AnimateOut(Action done)
    {
        _root.IsHitTestVisible = false;
        Run(_root.Opacity, 0, 1, 0.98f, _root.RenderTransformOrigin, CloseDuration, easeIn: true, done);
    }

    private void Run(double fromOpacity, double toOpacity, float fromScale, float toScale, Point origin, TimeSpan duration, bool easeIn, Action? done)
    {
        var ease = new CubicEase { EasingMode = easeIn ? EasingMode.EaseIn : EasingMode.EaseOut };
        _root.RenderTransformOrigin = origin;

        var opacity = new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease };
        if (done is not null)
            opacity.Completed += (_, _) => done();
        _root.BeginAnimation(OpacityProperty, opacity);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, toScale, duration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, toScale, duration) { EasingFunction = ease });

        Glass?.Animate((float)fromOpacity, (float)toOpacity, fromScale, toScale,
            new Vector2((float)origin.X, (float)origin.Y), duration, easeIn);
    }
}
