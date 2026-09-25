using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using iTask.Configuration;
using iTask.ShellIntegration;
using iTask.UI;
using iTask.WindowsIntegration;

namespace iTask.Dock;

/// <summary>
/// macOS-style dock, ported from the "mac-os-dock" React design:
///  • icons magnify with a cosine falloff around the cursor, and the dock widens to fit;
///  • scales/positions ease toward their targets every frame (lerp 0.2 hovering, 0.12 leaving);
///  • click bounce and running dots.
/// The window is taller and wider than the body so magnified icons have room; that extra area is
/// fully transparent and click-through. Per frame only transforms, Canvas offsets and two widths
/// change — no image re-scaling, no effect updates.
/// </summary>
public partial class DockWindow : OverlayWindow
{
    private const double SideMargin = 20; // room for the body's shadow
    private const double HoverTimeConstant = 0.045; // s — how quickly icons follow the cursor
    private const double LeaveTimeConstant = 0.080; // s — how quickly they settle back

    private readonly DockSettings _settings;
    private readonly RunningAppsService _runningApps;
    private readonly List<DockItemView> _items = new();

    private double[] _scales = Array.Empty<double>();
    private double[] _positions = Array.Empty<double>();
    private double? _mouseX;
    private bool _animating;
    private readonly Stopwatch _frameClock = new();
    private RECT _bounds;
    private DockItemView? _pressed;

    public DockWindow(DockSettings settings, RunningAppsService runningApps, bool glass)
    {
        _settings = settings;
        _runningApps = runningApps;
        InitializeComponent();

        Panel.CornerRadius = new CornerRadius(Radius);
        Sheen.CornerRadius = new CornerRadius(Math.Max(0, Radius - 1));
        if (glass)
        {
            UseGlass();
            // The glass draws the body (tint, sheen, border) so it can never lag behind the blur.
            // The WPF body stays as an invisible hit target: alpha 1/255 keeps the gaps between
            // icons part of the dock (fully transparent pixels would pass the mouse through,
            // and hovering a gap would count as leaving the dock).
            Panel.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Panel.BorderThickness = new Thickness(0);
            Sheen.Visibility = Visibility.Collapsed;
        }

        // Sub-pixel icon motion: rounding positions to whole pixels makes moving icons judder.
        Icons.UseLayoutRounding = false;
        Icons.SnapsToDevicePixels = false;
        Panel.Height = PanelHeight;
        Panel.Margin = new Thickness(0, 0, 0, _settings.BottomMargin);
        Icons.Height = IconDip;
        Icons.Margin = new Thickness(0, 0, 0, _settings.BottomMargin + Pad);

        AddItem(DockItemView.ForStart(IconDip, MaxScale));
        if (settings.ShowRunningApps)
            runningApps.Apps.CollectionChanged += OnAppsChanged;
        SyncItems();

        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => SetMouseX(null);
        IsVisibleChanged += (_, _) => { if (!IsVisible) SetMouseX(null); };
    }

    /// <summary>The set of items changed, so the dock needs a new size.</summary>
    public event EventHandler? ContentChanged;

    // ── Metrics (the design's proportions, derived from the icon size) ───────────────────────

    private double IconDip => _settings.IconSize;
    private double Spacing => Math.Max(4, IconDip * 0.08);
    private double Pad => Math.Max(8, IconDip * 0.12);
    private double Radius => Math.Max(12, IconDip * 0.4);
    private double MaxScale => Math.Max(1, _settings.Magnification);
    private double EffectWidth => _settings.MagnificationRange;

    /// <summary>Height of the dock body in DIPs.</summary>
    public double PanelHeight => IconDip + 2 * Pad;

    /// <summary>Space above the body for magnified icons plus the click bounce.</summary>
    private double Headroom => IconDip * (MaxScale - 1) + IconDip * 0.2 + 4;

    /// <summary>Widest the body gets at full magnification (DIPs).</summary>
    private double _maxPanelWidth;

    /// <summary>Window size (DIPs) that fits the dock at its widest magnification.</summary>
    public Size GetWindowSize()
    {
        double widest = BaseContentWidth();
        if (MaxScale > 1)
        {
            // Sweep the cursor across the dock and keep the widest layout it produces.
            for (double x = -EffectWidth / 2; x <= widest + EffectWidth / 2; x += 4)
                widest = Math.Max(widest, ContentWidth(TargetScales(x), null));
        }
        _maxPanelWidth = Math.Ceiling(widest + 2 * Pad);
        return new Size(_maxPanelWidth + 2 * SideMargin,
                        Math.Ceiling(_settings.BottomMargin + PanelHeight + Headroom));
    }

    protected override GlassStyle GetGlassStyle(ThemeService theme)
    {
        byte alpha = (byte)Math.Round(Math.Clamp(theme.DockOpacity, 0, 1) * 255);
        return theme.IsDark
            ? new GlassStyle(Color.FromArgb(alpha, 45, 45, 45), Color.FromArgb(41, 255, 255, 255), Sheen: true)
            : new GlassStyle(Color.FromArgb(alpha, 246, 246, 246), Color.FromArgb(26, 0, 0, 0), Sheen: true);
    }

    /// <summary>The dock body's current rectangle on screen (physical px).</summary>
    public RECT PanelScreenRect
    {
        get
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int width = (int)Math.Round(Panel.Width * dpi);
            int bottom = _bounds.Bottom - (int)Math.Round(_settings.BottomMargin * dpi);
            int left = _bounds.Left + (_bounds.Width - width) / 2;
            return new RECT(left, bottom - (int)Math.Round(PanelHeight * dpi), left + width, bottom);
        }
    }

    public override void SetBounds(RECT r)
    {
        _bounds = r;
        NoteBounds(r);
        if (Glass is null || !TryGetGlassPlacement(out var glassWindow, out var shape, out float radius, out float dpi))
        {
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, NativeMethods.SWP_NOACTIVATE);
            return;
        }

        // Move the dock and its glass in one batch so they land in the same frame (no wobble
        // between them while sliding in and out).
        var batch = NativeMethods.BeginDeferWindowPos(2);
        batch = NativeMethods.DeferWindowPos(batch, Glass.Handle, NativeMethods.HWND_TOPMOST,
            glassWindow.Left, glassWindow.Top, glassWindow.Width, glassWindow.Height, NativeMethods.SWP_NOACTIVATE);
        batch = NativeMethods.DeferWindowPos(batch, Handle, NativeMethods.HWND_TOPMOST,
            r.Left, r.Top, r.Width, r.Height, NativeMethods.SWP_NOACTIVATE);
        NativeMethods.EndDeferWindowPos(batch);
        Glass.SetShape(glassWindow, shape, radius, dpi, alreadyPlaced: true);
    }

    /// <summary>The glass follows the body (not the whole window), including while it widens.</summary>
    protected override void UpdateGlass()
    {
        if (Glass is not null && TryGetGlassPlacement(out var glassWindow, out var shape, out float radius, out float dpi))
            Glass.SetShape(glassWindow, shape, radius, dpi);
    }

    /// <summary>
    /// Where the glass window goes and where the body sits inside it. At rest the window hugs the
    /// body (so nothing beside the dock is covered); while magnifying it spans the widest the body
    /// can get, so each frame only moves the compositor shape instead of resizing a window.
    /// </summary>
    private bool TryGetGlassPlacement(out RECT window, out RECT shape, out float radius, out float dpi)
    {
        window = shape = default;
        radius = dpi = 1;
        if (_bounds.Width <= 0 || double.IsNaN(Panel.Width))
            return false;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        dpi = (float)scale;
        radius = (float)(Radius * scale);
        shape = PanelScreenRect;
        if (_animating || _mouseX is not null)
        {
            int wide = (int)Math.Ceiling(_maxPanelWidth * scale);
            int left = _bounds.Left + (_bounds.Width - wide) / 2;
            window = new RECT(Math.Min(left, shape.Left), shape.Top, Math.Max(left + wide, shape.Right), shape.Bottom);
        }
        else
        {
            window = shape;
        }
        return true;
    }

    // ── Items ────────────────────────────────────────────────────────────────────────────────

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncItems();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddItem(DockItemView item)
    {
        item.MouseLeftButtonDown += OnItemMouseDown;
        item.MouseLeftButtonUp += OnItemMouseUp;
        item.MouseRightButtonUp += OnItemRightClick;
        Icons.Children.Add(item);
        Icons.Children.Add(item.Dot);
        Canvas.SetBottom(item, 0);
        Canvas.SetBottom(item.Dot, Math.Max(-2, -IconDip * 0.05));
        System.Windows.Controls.Panel.SetZIndex(item.Dot, 100);
        _items.Add(item);
    }

    private void SyncItems()
    {
        var oldScales = _items.Select((item, i) => (item, scale: i < _scales.Length ? _scales[i] : 1.0))
                              .ToDictionary(p => p.item, p => p.scale);
        var apps = _settings.ShowRunningApps ? _runningApps.Apps.ToList() : new List<RunningApp>();

        foreach (var gone in _items.Where(i => !i.IsStart && !apps.Contains(i.App!)).ToList())
        {
            _items.Remove(gone);
            Icons.Children.Remove(gone);
            Icons.Children.Remove(gone.Dot);
        }
        foreach (var app in apps.Where(a => _items.All(i => i.App != a)))
        {
            var view = DockItemView.ForApp(app, IconDip, MaxScale);
            app.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(view.Refresh);
            AddItem(view);
        }
        // Keep the running-apps order (Start first).
        _items.Sort((a, b) => a.IsStart ? -1 : b.IsStart ? 1 : apps.IndexOf(a.App!).CompareTo(apps.IndexOf(b.App!)));

        _scales = _items.Select(i => oldScales.TryGetValue(i, out var s) ? s : 1.0).ToArray();
        _positions = CalculatePositions(_scales);
        ApplyFrame();
        StartAnimation();
    }

    // ── Magnification (the design's algorithm) ───────────────────────────────────────────────

    private double[] TargetScales(double? mouseX)
    {
        var scales = new double[_items.Count];
        for (int i = 0; i < scales.Length; i++)
        {
            scales[i] = 1;
            if (mouseX is not { } x || MaxScale <= 1)
                continue;
            double center = i * (IconDip + Spacing) + IconDip / 2;
            double minX = x - EffectWidth / 2, maxX = x + EffectWidth / 2;
            if (center < minX || center > maxX)
                continue;
            double theta = Math.Clamp((center - minX) / EffectWidth * 2 * Math.PI, 0, 2 * Math.PI);
            scales[i] = 1 + (1 - Math.Cos(theta)) / 2 * (MaxScale - 1);
        }
        return scales;
    }

    private double[] CalculatePositions(double[] scales)
    {
        var centers = new double[scales.Length];
        double x = 0;
        for (int i = 0; i < scales.Length; i++)
        {
            double w = IconDip * scales[i];
            centers[i] = x + w / 2;
            x += w + Spacing;
        }
        return centers;
    }

    private double BaseContentWidth() => _items.Count == 0 ? 0 : _items.Count * (IconDip + Spacing) - Spacing;

    private double ContentWidth(double[] scales, double[]? positions)
    {
        positions ??= CalculatePositions(scales);
        double width = 0;
        for (int i = 0; i < scales.Length; i++)
            width = Math.Max(width, positions[i] + IconDip * scales[i] / 2);
        return width;
    }

    // Like the design: cursor x relative to the body's left edge, minus its padding.
    private void OnMouseMove(object sender, MouseEventArgs e) => SetMouseX(e.GetPosition(Panel).X - Pad);

    private void SetMouseX(double? x)
    {
        _mouseX = x;
        StartAnimation();
    }

    private void StartAnimation()
    {
        if (_animating)
            return;
        _animating = true;
        _frameClock.Restart();
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // Exponential approach toward the target, in real time so it feels the same at any refresh
        // rate. (The design's 0.2 / 0.12 per 60 Hz frame is τ ≈ 75 / 130 ms, which felt sluggish.)
        double dt = Math.Min(0.05, _frameClock.Elapsed.TotalSeconds);
        _frameClock.Restart();
        double tau = _mouseX is not null ? HoverTimeConstant : LeaveTimeConstant;
        double k = 1 - Math.Exp(-dt / tau);

        var targetScales = TargetScales(_mouseX);
        var targetPositions = CalculatePositions(targetScales);
        bool settled = true;
        for (int i = 0; i < _scales.Length; i++)
        {
            _scales[i] += (targetScales[i] - _scales[i]) * k;
            _positions[i] += (targetPositions[i] - _positions[i]) * k;
            if (Math.Abs(_scales[i] - targetScales[i]) > 0.002 || Math.Abs(_positions[i] - targetPositions[i]) > 0.1)
                settled = false;
        }
        if (PerfLogging)
            _frameTimes.Add(dt * 1000);
        if (settled)
        {
            Array.Copy(targetScales, _scales, _scales.Length);
            Array.Copy(targetPositions, _positions, _positions.Length);
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            LogFrameStats();
        }
        ApplyFrame();
    }

    // Set ITASK_PERF=1 to log animation frame timing (for tuning smoothness).
    private static readonly bool PerfLogging = Environment.GetEnvironmentVariable("ITASK_PERF") == "1";
    private readonly List<double> _frameTimes = new();

    private void LogFrameStats()
    {
        if (!PerfLogging || _frameTimes.Count < 10)
        {
            _frameTimes.Clear();
            return;
        }
        var sorted = _frameTimes.Skip(1).OrderBy(t => t).ToList(); // first frame measures idle time
        double avg = sorted.Average(), p95 = sorted[(int)(sorted.Count * 0.95)], max = sorted[^1];
        int slow = sorted.Count(t => t > 25);
        iTask.Utilities.Log.Info($"Dock perf: {sorted.Count} frames, avg {avg:0.0} ms ({1000 / avg:0} fps), p95 {p95:0.0} ms, max {max:0.0} ms, >25ms: {slow}");
        _frameTimes.Clear();
    }

    private void ApplyFrame()
    {
        double content = _items.Count > 0 ? ContentWidth(_scales, _positions) : 0;
        Panel.Width = content + 2 * Pad;
        Icons.Width = content;

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            item.SetScale(_scales[i]);
            Canvas.SetLeft(item, _positions[i] - IconDip / 2);
            Canvas.SetLeft(item.Dot, _positions[i] - item.Dot.Width / 2);
            System.Windows.Controls.Panel.SetZIndex(item, (int)Math.Round(_scales[i] * 10));
        }
        UpdateGlass();
    }

    // ── Clicks ───────────────────────────────────────────────────────────────────────────────

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = sender as DockItemView;
        e.Handled = true;
    }

    private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not DockItemView item || item != _pressed)
            return;
        _pressed = null;

        int index = _items.IndexOf(item);
        double scale = index >= 0 ? _scales[index] : 1;
        item.Bounce(scale > 1.3 ? IconDip * 0.2 : IconDip * 0.15);

        if (item.IsStart)
            ShellCommands.OpenStartMenu();
        else
            item.App?.Toggle();
    }

    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DockItemView { App: { } app } item)
            AppContextMenu.Show(app, item, PlacementMode.Top);
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnFrame;
        _runningApps.Apps.CollectionChanged -= OnAppsChanged;
        base.OnClosed(e);
    }
}
