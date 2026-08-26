using System.Windows;

namespace CodexUsageMonitor.Views;

public partial class ResetCardsView : System.Windows.Controls.UserControl
{
    public ResetCardsView() => InitializeComponent();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
        {
            window.ToggleSettingsPopup((System.Windows.Controls.Button)sender);
        }
    }
}
