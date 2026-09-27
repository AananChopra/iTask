using System.Windows;
using System.Windows.Controls;
using iTask.ShellIntegration;

namespace iTask.TopBar.Flyouts;

/// <summary>The top-left iTask menu (settings, shortcuts, quit).</summary>
public partial class AppMenuFlyout : UserControl, IFlyoutContent
{
    private readonly Action _openSettings;

    public AppMenuFlyout(Action openSettings)
    {
        _openSettings = openSettings;
        InitializeComponent();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ContentChanged { add { } remove { } }

    private void Run(Action action)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        action();
    }

    private void ITaskSettings_Click(object sender, RoutedEventArgs e) => Run(_openSettings);

    private void WindowsSettings_Click(object sender, RoutedEventArgs e) => Run(ShellCommands.OpenSettings);

    private void TaskManager_Click(object sender, RoutedEventArgs e) => Run(ShellCommands.OpenTaskManager);

    private void Quit_Click(object sender, RoutedEventArgs e) => Run(() => Application.Current.Shutdown());
}
