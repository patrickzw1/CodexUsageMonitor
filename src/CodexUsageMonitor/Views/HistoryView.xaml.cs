using System.Windows.Controls;

namespace CodexUsageMonitor.Views;

public partial class HistoryView : System.Windows.Controls.UserControl
{
    public HistoryView() => InitializeComponent();

    internal bool HasOpenInputPopup => IsVisible &&
        (HistoryYearPicker.IsDropDownOpen || HistoryMonthPicker.IsDropDownOpen ||
         HistoryStartDatePicker.IsDropDownOpen || HistoryEndDatePicker.IsDropDownOpen);
}
