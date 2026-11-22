using Avalonia.Controls;

namespace Kinora.Views;

public partial class CalendarView : UserControl
{
    /// <summary>Wider than this, a row is hard to follow from the time to the chips.</summary>
    private const double MostWidth = 1080;

    public CalendarView()
    {
        InitializeComponent();
        // As wide as the window, up to MostWidth, on the left like the other pages
        SizeChanged += (_, e) => Page.Width = Math.Max(0, Math.Min(MostWidth, e.NewSize.Width - Page.Margin.Left - Page.Margin.Right));
    }
}
