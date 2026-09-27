using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace iTask.TopBar.Flyouts;

/// <summary>Other apps' notification-area icons, in a glass menu under the chevron.</summary>
public partial class TrayFlyout : UserControl, IFlyoutContent
{
    /// <summary>Icon cell width including its margin (TrayIconView: 36 wide + 1 px each side).</summary>
    public const double CellWidth = 38;
    public const int MaxColumns = 4;

    private readonly ICollectionView _icons;

    public TrayFlyout(ICollectionView icons)
    {
        _icons = icons;
        InitializeComponent();
        Icons.ItemsSource = icons;
        Sync();
        ((INotifyCollectionChanged)icons).CollectionChanged += OnIconsChanged;
        Unloaded += (_, _) => ((INotifyCollectionChanged)icons).CollectionChanged -= OnIconsChanged;
    }

    // Never raised: the menu closes when the clicked icon's app takes focus for its own menu/window.
    public event EventHandler? CloseRequested { add { } remove { } }
    public event EventHandler? ContentChanged;

    /// <summary>Menu width for <paramref name="count"/> icons: up to four per row.</summary>
    public static double WidthFor(int count) => Math.Clamp(count, 1, MaxColumns) * CellWidth + 14;

    private void OnIconsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(Sync);

    private void Sync()
    {
        EmptyText.Visibility = _icons.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
}
