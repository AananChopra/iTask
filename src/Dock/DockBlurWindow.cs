using System.Windows.Controls;
using iTask.UI;
using iTask.WindowsIntegration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.Dock;

/// <summary>
/// The blur under the dock body (the design's <c>backdrop-blur-md</c>). It lives in its own window
/// because the dock itself is a per-pixel-transparent window (so magnified icons can rise above
/// it), and Windows can't blur behind only part of such a window. Clipped to the dock's rounded
/// shape with a window region, and always kept directly beneath the dock.
/// </summary>
internal sealed class DockBlurWindow : OverlayWindow
{
    private RECT _bounds;
    private int _radius;

    public DockBlurWindow()
    {
        Title = "iTask Dock Blur";
        Content = new Grid(); // something to render, so the window uncloaks after its first frame
        IsHitTestVisible = false;
    }

    public override void ApplyTheme(ThemeService theme)
    {
        if (Source is not null)
            Backdrop.ApplyBlurBehind(Source, theme.IsDark);
    }

    /// <summary>Positions the blur to cover <paramref name="bounds"/> with rounded corners (physical px).</summary>
    public void SetShape(RECT bounds, int radius)
    {
        if (Handle == IntPtr.Zero)
            return;
        bool resized = bounds.Width != _bounds.Width || bounds.Height != _bounds.Height || radius != _radius;
        if (bounds.Equals(_bounds) && !resized)
            return;
        _bounds = bounds;
        _radius = radius;
        SetWindowPos(Handle, HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOACTIVATE);
        if (resized)
            SetWindowRgn(Handle, CreateRoundRectRgn(0, 0, bounds.Width + 1, bounds.Height + 1, radius * 2, radius * 2), true);
    }
}
