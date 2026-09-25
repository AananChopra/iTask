using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using iTask.WindowsIntegration;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.Dock;

/// <summary>
/// "Smart" dock visibility:
///  • Desktop showing, or the active app isn't maximized → dock visible.
///  • Active app maximized / full-screen → dock slides away; pushing the cursor against the bottom
///    edge of the screen slides it back until the cursor leaves it.
/// The cursor is only sampled while the dock is auto-hidden or revealed, never while plainly visible.
/// </summary>
public sealed class DockAutoHide : IDisposable
{
    private static readonly TimeSpan RevealDwell = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(600);
    private const double SlideInMs = 240;
    private const double SlideOutMs = 200;
    private const int EdgeTolerancePx = 2;

    private readonly DockWindow _dock;
    private readonly ForegroundWatcher _foreground;
    private readonly DispatcherTimer _cursorTimer;
    private MonitorInfo _monitor;
    private RECT _home;           // where the dock sits when shown (physical px)
    private bool _enabled;
    private bool _suspended;      // e.g. a full-screen app owns the monitor
    private bool _wantHidden;     // foreground app is maximized
    private bool _revealed;       // shown because of the cursor, despite _wantHidden
    private DateTime _edgeSince = DateTime.MaxValue;
    private DateTime _leftSince = DateTime.MaxValue;

    // Animation state: 0 = fully shown, 1 = fully hidden below the screen edge.
    private double _offset;
    private double _animFrom, _animTo, _animMs;
    private readonly Stopwatch _animClock = new();
    private bool _animating;

    public DockAutoHide(DockWindow dock, ForegroundWatcher foreground, MonitorInfo monitor)
    {
        _dock = dock;
        _foreground = foreground;
        _monitor = monitor;
        _cursorTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        _cursorTimer.Tick += (_, _) => SampleCursor();
        _foreground.Changed += OnForegroundChanged;
    }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Evaluate();
        }
    }

    /// <summary>Sets where the dock lives when visible (called from layout).</summary>
    public void SetHome(RECT home, MonitorInfo monitor)
    {
        _home = home;
        _monitor = monitor;
        Apply();
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        Evaluate();
    }

    private void OnForegroundChanged(object? sender, EventArgs e) => Evaluate();

    public void Evaluate()
    {
        if (_suspended)
        {
            _cursorTimer.Stop();
            _revealed = false;
            StopAnimation();
            _offset = 1;
            _dock.Hide();
            return;
        }

        if (!_enabled)
        {
            _wantHidden = false;
        }
        else
        {
            var kind = ForegroundWatcher.Classify(_monitor.Handle, _monitor.Bounds);
            bool wasHidden = _wantHidden;
            _wantHidden = kind == ForegroundKind.Maximized;
            // Switching to a maximized app *from* the dock: stay put until the cursor leaves it.
            if (_wantHidden && !wasHidden && _offset < 0.5 && IsCursorOverDock())
            {
                _revealed = true;
                _leftSince = DateTime.MaxValue;
            }
        }

        if (!_wantHidden)
            _revealed = false;

        UpdateCursorTimer();
        AnimateTo(_wantHidden && !_revealed ? 1 : 0);
    }

    private void UpdateCursorTimer()
    {
        if (_wantHidden && _enabled && !_suspended)
        {
            if (!_cursorTimer.IsEnabled)
            {
                _edgeSince = _leftSince = DateTime.MaxValue;
                _cursorTimer.Start();
            }
        }
        else
        {
            _cursorTimer.Stop();
        }
    }

    private void SampleCursor()
    {
        if (!GetCursorPos(out var p))
            return;
        var now = DateTime.UtcNow;
        var m = _monitor.Bounds;
        bool onMonitorX = p.X >= m.Left && p.X < m.Right;
        bool atEdge = onMonitorX && p.Y >= m.Bottom - EdgeTolerancePx && p.Y < m.Bottom + EdgeTolerancePx;

        if (!_revealed)
        {
            if (!atEdge)
            {
                _edgeSince = DateTime.MaxValue;
                return;
            }
            if (_edgeSince == DateTime.MaxValue)
                _edgeSince = now;
            if (now - _edgeSince >= RevealDwell)
            {
                _revealed = true;
                _leftSince = DateTime.MaxValue;
                AnimateTo(0);
            }
            return;
        }

        // Revealed: stay while the cursor is on the dock, in the gap below it, or at the edge.
        if (IsCursorOverDock(p) || atEdge)
        {
            _leftSince = DateTime.MaxValue;
            return;
        }
        if (_leftSince == DateTime.MaxValue)
            _leftSince = now;
        if (now - _leftSince >= HideDelay)
        {
            _revealed = false;
            _edgeSince = DateTime.MaxValue;
            AnimateTo(1);
        }
    }

    private bool IsCursorOverDock() => GetCursorPos(out var p) && IsCursorOverDock(p);

    private bool IsCursorOverDock(POINT p)
    {
        // Horizontally: the body (the window's sides are transparent margin). Vertically: the whole
        // window, which includes the room magnified icons rise into.
        var body = _dock.PanelScreenRect;
        int slack = _monitor.ToPixels(12);
        return p.X >= body.Left - slack && p.X < body.Right + slack &&
               p.Y >= _home.Top && p.Y < _monitor.Bounds.Bottom + EdgeTolerancePx;
    }

    // ── Slide animation (moves the real window so the acrylic backdrop moves with it) ──────────

    private void AnimateTo(double target)
    {
        if (!_animating && Math.Abs(_offset - target) < 0.001)
        {
            Apply();
            return;
        }
        if (_animating && Math.Abs(_animTo - target) < 0.001)
            return;

        _animFrom = _offset;
        _animTo = target;
        _animMs = (target < _offset ? SlideInMs : SlideOutMs) * Math.Abs(target - _offset);
        _animClock.Restart();
        if (target < 1)
            _dock.ShowPassive();
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double t = _animMs <= 0 ? 1 : Math.Min(1, _animClock.Elapsed.TotalMilliseconds / _animMs);
        // Ease-out when sliding in, ease-in when sliding away.
        double eased = _animTo < _animFrom ? 1 - Math.Pow(1 - t, 3) : t * t * t;
        _offset = _animFrom + (_animTo - _animFrom) * eased;
        if (t >= 1)
            StopAnimation();
        Apply();
    }

    private void StopAnimation()
    {
        if (!_animating)
            return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void Apply()
    {
        if (_home.Width <= 0)
            return;
        if (_offset >= 0.999 && !_animating)
        {
            _dock.Hide();
            return;
        }
        // Travel from the home position to just below the bottom edge of the monitor.
        int travel = _monitor.Bounds.Bottom - _home.Top;
        int dy = (int)Math.Round(travel * _offset);
        _dock.SetBounds(new RECT(_home.Left, _home.Top + dy, _home.Right, _home.Bottom + dy));
        if (!_suspended)
            _dock.ShowPassive();
    }

    public void Dispose()
    {
        _foreground.Changed -= OnForegroundChanged;
        _cursorTimer.Stop();
        StopAnimation();
    }
}
