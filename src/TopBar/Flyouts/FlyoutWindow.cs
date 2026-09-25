using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iTask.UI;

namespace iTask.TopBar.Flyouts;

/// <summary>Content of a top bar dropdown; asks to be closed after acting (e.g. opening Settings).</summary>
public interface IFlyoutContent
{
    event EventHandler? CloseRequested;

    /// <summary>Raised after the content changed in a way that may change its size (e.g. a rescan).</summary>
    event EventHandler? ContentChanged;
}

/// <summary>A macOS-style menu dropdown under the top bar: rounded frosted glass, never activated.</summary>
public sealed class FlyoutWindow : OverlayWindow
{
    private readonly Border _root;

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
}
