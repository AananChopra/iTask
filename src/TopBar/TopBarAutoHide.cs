using System.Diagnostics;
using System.Windows.Media;
using iTask.Utilities;
using iTask.WindowsIntegration;

namespace iTask.TopBar;

/// <summary>
/// Slides the top bar up out of the way while a full-screen app (a game, a video) has the display,
/// and back down when it leaves. No edge reveal: full-screen apps get the whole screen.
/// </summary>
public sealed class TopBarAutoHide : IDisposable
{
    private const double SlideMs = 180;

    private readonly TopBarWindow _bar;
    private RECT _home;
    private bool _fullScreen;

    // 0 = fully shown, 1 = fully hidden above the top edge.
    private double _offset;
    private double _animFrom, _animTo;
    private readonly Stopwatch _animClock = new();
    private bool _animating;

    public TopBarAutoHide(TopBarWindow bar) => _bar = bar;

    /// <summary>Where the bar sits when shown (from layout).</summary>
    public void SetHome(RECT home)
    {
        _home = home;
        Apply();
    }

    public void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == _fullScreen)
            return;
        _fullScreen = fullScreen;
        AnimateTo(fullScreen ? 1 : 0);
    }

    private void AnimateTo(double target)
    {
        _animFrom = _offset;
        _animTo = target;
        _animClock.Restart();
        if (target < 1)
            _bar.ShowPassive();
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnRendering;
            FrameTiming.Begin();
        }
    }

    private bool _inRendering;

    private void OnRendering(object? sender, EventArgs e)
    {
        // WPF raises this synchronously from inside window moves, including Apply()'s own; see
        // DockAutoHide.OnRendering. Nested calls are ignored.
        if (_inRendering)
            return;
        _inRendering = true;
        try
        {
            double duration = SlideMs * Math.Abs(_animTo - _animFrom);
            double t = duration <= 0 || !SystemAnimations.Enabled ? 1 : Math.Min(1, _animClock.Elapsed.TotalMilliseconds / duration);
            // Ease-out coming in, ease-in going away.
            double eased = _animTo < _animFrom ? 1 - Math.Pow(1 - t, 3) : t * t * t;
            _offset = _animFrom + (_animTo - _animFrom) * eased;
            if (t >= 1)
            {
                _animating = false;
                CompositionTarget.Rendering -= OnRendering;
                FrameTiming.End();
            }
            Apply();
        }
        finally
        {
            _inRendering = false;
        }
    }

    private void Apply()
    {
        if (_home.Width <= 0)
            return;
        if (_offset >= 0.999 && !_animating)
        {
            _bar.Hide();
            return;
        }
        int dy = (int)Math.Round(_home.Height * _offset);
        _bar.SetBounds(new RECT(_home.Left, _home.Top - dy, _home.Right, _home.Bottom - dy));
        _bar.ShowPassive();
    }

    public void Dispose()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
            FrameTiming.End();
        }
    }
}
