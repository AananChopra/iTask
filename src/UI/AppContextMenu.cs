using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using iTask.WindowsIntegration;

namespace iTask.UI;

/// <summary>Right-click menu for a running app: pick one of its windows, or close them.</summary>
public static class AppContextMenu
{
    public static void Show(RunningApp app, UIElement target, PlacementMode placement)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };

        foreach (var window in app.Windows)
        {
            var title = string.IsNullOrWhiteSpace(window.Title) ? app.Name : window.Title;
            var item = new MenuItem { Header = Trim(title), FontWeight = window.State == ManagedShell.WindowsTasks.ApplicationWindow.WindowState.Active ? FontWeights.SemiBold : FontWeights.Normal };
            item.Click += (_, _) => window.BringToFront();
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var close = new MenuItem { Header = app.Windows.Count > 1 ? "Close all windows" : "Close window" };
        close.Click += (_, _) =>
        {
            foreach (var w in app.Windows.ToList())
                w.Close();
        };
        menu.Items.Add(close);
        menu.IsOpen = true;
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..57] + "…";
}
