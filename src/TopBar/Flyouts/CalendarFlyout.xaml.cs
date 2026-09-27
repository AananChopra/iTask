using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using iTask.ShellIntegration;

namespace iTask.TopBar.Flyouts;

/// <summary>macOS-style month calendar under the clock: today highlighted, arrows or the wheel to browse.</summary>
public partial class CalendarFlyout : UserControl, IFlyoutContent
{
    private readonly DateTimeFormatInfo _format = CultureInfo.CurrentCulture.DateTimeFormat;
    private DateTime _month; // first day of the month shown

    public CalendarFlyout()
    {
        InitializeComponent();
        var today = DateTime.Today;
        TodayText.Text = today.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
        _month = new DateTime(today.Year, today.Month, 1);

        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)_format.FirstDayOfWeek + i) % 7);
            var label = new TextBlock
            {
                Text = _format.GetShortestDayName(day),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 4),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Weekdays.Children.Add(label);
        }
        Render();
        MouseWheel += OnMouseWheel;
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ContentChanged;

    private void Render()
    {
        var today = DateTime.Today;
        MonthText.Text = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        // Hidden rather than collapsed, so the arrows don't jump around.
        TodayButton.Visibility = _month.Year == today.Year && _month.Month == today.Month
            ? Visibility.Hidden
            : Visibility.Visible;

        Days.Children.Clear();
        int offset = ((int)_month.DayOfWeek - (int)_format.FirstDayOfWeek + 7) % 7;
        var start = _month.AddDays(-offset);
        // Always six rows, so the menu keeps its size from month to month.
        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            Days.Children.Add(DayCell(date, date.Month == _month.Month, date == today));
        }
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private static FrameworkElement DayCell(DateTime date, bool inMonth, bool isToday)
    {
        var text = new TextBlock
        {
            Text = date.Day.ToString(CultureInfo.CurrentCulture),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var cell = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(1), Child = text };
        if (isToday && inMonth)
        {
            cell.SetResourceReference(Border.BackgroundProperty, "AccentFillBrush");
            text.Foreground = Brushes.White;
            text.FontWeight = FontWeights.SemiBold;
        }
        else if (isToday)
        {
            // Today spilling into a neighbouring month: marked, but not as loudly.
            text.SetResourceReference(TextBlock.ForegroundProperty, "AccentFillBrush");
            text.FontWeight = FontWeights.SemiBold;
        }
        else if (!inMonth)
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            text.Opacity = 0.55;
        }
        return cell;
    }

    private void Show(DateTime month)
    {
        _month = month;
        Render();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Show(_month.AddMonths(-1));

    private void Next_Click(object sender, RoutedEventArgs e) => Show(_month.AddMonths(1));

    private void Today_Click(object sender, RoutedEventArgs e) => Show(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Show(_month.AddMonths(e.Delta > 0 ? -1 : 1));
        e.Handled = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShellCommands.Launch("ms-settings:dateandtime");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
