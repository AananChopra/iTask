using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using iTask.WindowsIntegration;
using ManagedShell.WindowsTray;
using MsNative = ManagedShell.Interop.NativeMethods;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.TopBar;

/// <summary>
/// One app tray icon in the top bar. Mouse input is forwarded to the owning app exactly the way
/// Explorer would (callback message + NIN_SELECT / WM_CONTEXTMENU), so its own menus and flyouts open.
/// </summary>
public sealed class TrayIconView : Border
{
    private readonly Image _image;
    private NotifyIcon? _icon;

    public TrayIconView()
    {
        _image = new Image { Width = 16, Height = 16, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Child = _image;
        Padding = new Thickness(5, 0, 5, 0);
        Margin = new Thickness(1, 3, 1, 3);
        CornerRadius = new CornerRadius(4);
        Background = Brushes.Transparent;
        DataContextChanged += (_, _) => Attach(DataContext as NotifyIcon);
        // The dropdown unloads its content when it closes; re-attach when it opens again.
        Loaded += (_, _) => Attach(DataContext as NotifyIcon);
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(NotifyIcon? icon)
    {
        if (_icon is not null)
            _icon.PropertyChanged -= OnIconChanged;
        _icon = icon;
        if (_icon is not null)
            _icon.PropertyChanged += OnIconChanged;
        Sync();
    }

    private void OnIconChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Sync);

    private void Sync()
    {
        _image.Source = _icon?.Icon;
        // Version-4 icons draw their own rich tooltip on NIN_POPUPOPEN; older ones rely on the host.
        ToolTip = _icon is { Version: < 4 } && !string.IsNullOrWhiteSpace(_icon.Title) ? _icon.Title : null;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        SetResourceReference(BackgroundProperty, "ItemHoverBrush");
        UpdatePlacement();
        _icon?.IconMouseEnter(CursorParam());
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Background = Brushes.Transparent;
        _icon?.IconMouseLeave(CursorParam());
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _icon?.IconMouseMove(CursorParam());
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        e.Handled = true;
        UpdatePlacement();
        _icon?.IconMouseDown(e.ChangedButton, CursorParam(), DoubleClickTime);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        e.Handled = true;
        _icon?.IconMouseUp(e.ChangedButton, CursorParam(), DoubleClickTime);
    }

    private static int DoubleClickTime => (int)GetDoubleClickTime();

    /// <summary>Screen position packed like a mouse lParam (x low word, y high word), physical px.</summary>
    private static uint CursorParam()
    {
        GetCursorPos(out var p);
        return ((uint)(ushort)p.Y << 16) | (ushort)p.X;
    }

    /// <summary>Publishes our on-screen rect so apps can anchor popups to the icon (Shell_NotifyIconGetRect).</summary>
    private void UpdatePlacement()
    {
        if (_icon is null || PresentationSource.FromVisual(this) is null)
            return;
        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        _icon.Placement = new MsNative.Rect
        {
            Left = (int)topLeft.X,
            Top = (int)topLeft.Y,
            Right = (int)bottomRight.X,
            Bottom = (int)bottomRight.Y,
        };
    }
}
