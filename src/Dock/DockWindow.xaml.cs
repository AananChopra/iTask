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
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.Dock;

/// <summary>
/// macOS-style dock, ported from the "mac-os-dock" React design:
///  • icons magnify with a cosine falloff around the cursor, and the dock widens to fit;
///  • scales/positions ease toward their targets every frame (lerp 0.2 hovering, 0.12 leaving);
///  • click bounce, running dots, scale-dependent icon shadows.
/// The window is taller and wider than the body so magnified icons have room; that extra area is
/// fully transparent and click-through.
/// </summary>
public partial class DockWindow : OverlayWindow
{
    private const double SideMargin = 28; // room for the body's shadow

    private readonly DockSettings _settings;
    private readonly DockBlurWindow? _blur;
    private readonly List<DockItemView> _items = new();
    private readonly RunningAppsService _runningApps;

    private double[] _scales = Array.Empty<double>();
    private double[] _positions = Array.Empty<double>();
    private double? _mouseX;
    private bool _animating;
    private readonly Stopwatch _frameClock = new();
    private RECT _bounds;
    private DockItemView? _pressed;

    public DockWindow(DockSettings settings, RunningAppsService runningApps, bool blurBehind)
    {
        _settings = settings;
        _runningApps = runningApps;
        InitializeComponent();

        if (blurBehind)
            _blur = new DockBlurWindow();

        Panel.CornerRadius = new CornerRadius(Radius);
        InsetTop.CornerRadius = InsetBottom.CornerRadius = new CornerRadius(Math.Max(0, Radius - 1));

        _items.Add(DockItemView.ForStart());
        if (settings.ShowRunningApps)
            runningApps.Apps.CollectionChanged += OnAppsChanged;
        SyncItems();

        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => SetMouseX(null);
        IsVisibleChanged += OnVisibleChanged;
    }

    /// <summary>The set of items changed, so the dock needs a new size.</summary>
    public event EventHandler? ContentChanged;

    // ── Metrics (the design's desktop values, derived from the icon size) ─────────────────────

    private double IconDip => _settings.IconSize;
    private double Spacing => Math.Max(4, IconDip * 0.08);
    private double Pad => Math.Max(8, IconDip * 0.12);
    private double Radius => Math.Max(12, IconDip * 0.4);
    private double MaxScale => Math.Max(1, _settings.Magnification);
    private double EffectWidth => _settings.MagnificationRange;

    /// <summary>Height of the dock body in DIPs.</summary>
    public double PanelHeight => IconDip + 2 * Pad;

    /// <summary>Space above the body for magnified icons plus the click bounce.</summary>
    private double Headroom => IconDip * (MaxScale - 1) + IconDip * 0.2 + 6;

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
        return new Size(Math.Ceiling(widest + 2 * Pad + 2 * SideMargin),
                        Math.Ceiling(_settings.BottomMargin + PanelHeight + Headroom));
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

    // ── Items ────────────────────────────────────────────────────────────────────────────────

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncItems();
        ContentChanged?.Invoke(this, EventArgs.Empty);
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
        }
        foreach (var app in apps)
        {
            var view = _items.FirstOrDefault(i => i.App == app);
            if (view is null)
            {
                view = DockItemView.ForApp(app);
                app.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(view.Refresh);
                _items.Add(view);
            }
        }
        // Keep the running-apps order (Start first).
        _items.Sort((a, b) => a.IsStart ? -1 : b.IsStart ? 1 : apps.IndexOf(a.App!).CompareTo(apps.IndexOf(b.App!)));

        foreach (var item in _items.Where(i => !Icons.Children.Contains(i)))
        {
            item.MouseLeftButtonDown += OnItemMouseDown;
            item.MouseLeftButtonUp += OnItemMouseUp;
            item.MouseRightButtonUp += OnItemRightClick;
            Icons.Children.Add(item);
        }

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

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Like the design: cursor x relative to the body's left edge, minus its padding.
        SetMouseX(e.GetPosition(Panel).X - Pad);
    }

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
        // The design lerps a fixed fraction per 60 Hz frame; scale it to the real frame time.
        double dt = Math.Min(0.1, _frameClock.Elapsed.TotalSeconds);
        _frameClock.Restart();
        double perFrame = _mouseX is not null ? 0.2 : 0.12;
        double k = 1 - Math.Pow(1 - perFrame, dt * 60);

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
        if (settled)
        {
            Array.Copy(targetScales, _scales, _scales.Length);
            Array.Copy(targetPositions, _positions, _positions.Length);
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
        ApplyFrame();
    }

    private void ApplyFrame()
    {
        double content = _items.Count > 0 ? ContentWidth(_scales, _positions) : 0;
        Panel.Width = content + 2 * Pad;
        Panel.Height = PanelHeight;
        Panel.Margin = new Thickness(0, 0, 0, _settings.BottomMargin);
        Icons.Width = content;
        Icons.Height = IconDip;
        Icons.Margin = new Thickness(0, 0, 0, _settings.BottomMargin + Pad);

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            double size = IconDip * _scales[i];
            item.Width = item.Height = size;
            Canvas.SetLeft(item, _positions[i] - size / 2);
            Canvas.SetBottom(item, 0);
            System.Windows.Controls.Panel.SetZIndex(item, (int)Math.Round(_scales[i] * 10));
            item.ApplyScale(_scales[i], IconDip);
        }
        UpdateBlur();
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

    // ── Window plumbing: the blur layer follows the body ────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_blur is not null)
        {
            _blur.EnsureHandle();
            // Owned windows always stay above their owner: the dock can never end up under its blur.
            SetWindowLongPtr(Handle, GWLP_HWNDPARENT, _blur.Handle);
        }
    }

    public override void ApplyTheme(ThemeService theme)
    {
        // Per-pixel transparent window: no DWM material of its own; the blur window provides it.
        _blur?.ApplyTheme(theme);
    }

    public override void SetBounds(RECT r)
    {
        _bounds = r;
        base.SetBounds(r);
        UpdateBlur();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_blur is null)
            return;
        if (IsVisible)
        {
            UpdateBlur();
            _blur.ShowPassive();
        }
        else
        {
            _blur.Hide();
            SetMouseX(null);
        }
    }

    private void UpdateBlur()
    {
        if (_blur is null || _bounds.Width <= 0 || double.IsNaN(Panel.Width))
            return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var r = PanelScreenRect;
        // Inset 1px so the blur's (unantialiased) region edge hides under the body's border.
        _blur.SetShape(new RECT(r.Left + 1, r.Top + 1, r.Right - 1, r.Bottom - 1), (int)Math.Round((Radius - 1) * dpi));
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnFrame;
        _runningApps.Apps.CollectionChanged -= OnAppsChanged;
        _blur?.Close();
        base.OnClosed(e);
    }
}
