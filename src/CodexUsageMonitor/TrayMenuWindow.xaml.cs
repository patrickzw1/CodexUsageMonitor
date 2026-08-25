using System.ComponentModel;
using System.Windows;
using CodexUsageMonitor.ViewModels;
using Forms = System.Windows.Forms;

namespace CodexUsageMonitor;

public partial class TrayMenuWindow : System.Windows.Window
{
    private readonly MainViewModel _viewModel;
    private readonly Action _showMainWindow;
    private readonly Action _shutdownApplication;

    public TrayMenuWindow(MainViewModel viewModel, Action showMainWindow, Action shutdownApplication)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _showMainWindow = showMainWindow;
        _shutdownApplication = shutdownApplication;
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this, ThemeManager.IsDarkMode);
        Deactivated += (_, _) => Hide();
    }

    public bool AllowClose { get; set; }

    public void ShowNearTray()
    {
        var cursor = Forms.Cursor.Position;
        NativeWindowPositioner.PlaceTrayMenu(this, Forms.Screen.FromPoint(cursor), cursor);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _showMainWindow();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshCommand.Execute(null);
        Hide();
    }

    private void DataButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenDataFolderCommand.Execute(null);
        Hide();
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e) => _shutdownApplication();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
