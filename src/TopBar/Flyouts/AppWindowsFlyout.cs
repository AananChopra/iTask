using System.Windows;
using System.Windows.Controls;
using iTask.WindowsIntegration;
using ManagedShell.WindowsTasks;

namespace iTask.TopBar.Flyouts;

/// <summary>Right-click menu for a running app (dock or top bar): pick one of its windows, or close them.</summary>
public sealed class AppWindowsFlyout : StackPanel, IFlyoutContent
{
    public const double MenuWidth = 280;

    public AppWindowsFlyout(RunningApp app)
    {
        foreach (var window in app.Windows)
        {
            var title = string.IsNullOrWhiteSpace(window.Title) ? app.Name : window.Title;
            Children.Add(Item(title, window.State == ApplicationWindow.WindowState.Active, () => window.BringToFront()));
        }

        var separator = new Border();
        separator.SetResourceReference(StyleProperty, "FlyoutSeparator");
        Children.Add(separator);

        Children.Add(Item(app.Windows.Count > 1 ? "Close all windows" : "Close window", bold: false, () =>
        {
            foreach (var w in app.Windows.ToList())
                w.Close();
        }));
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ContentChanged { add { } remove { } }

    private Button Item(string text, bool bold, Action action)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            },
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.SetResourceReference(StyleProperty, "FlyoutItemStyle");
        button.Click += (_, _) =>
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            action();
        };
        return button;
    }
}
