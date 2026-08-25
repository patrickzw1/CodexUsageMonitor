using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CodexUsageMonitor.ViewModels;
using Forms = System.Windows.Forms;

namespace CodexUsageMonitor;

public partial class MainWindow : System.Windows.Window
{
    private readonly bool _keepOpen;

    public MainWindow(MainViewModel viewModel, bool keepOpen = false)
    {
        InitializeComponent();
        DataContext = viewModel;
        _keepOpen = keepOpen;
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this, ThemeManager.IsDarkMode);
        Deactivated += (_, _) =>
        {
            if (!_keepOpen)
            {
                Hide();
            }
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !IsInsideButton(e.OriginalSource as DependencyObject))
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => Hide();

    public bool AllowClose { get; set; }

    public void ShowNearTray()
    {
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        NativeWindowPositioner.PlaceMainWindow(this, screen);
    }

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

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (DataContext is MainViewModel viewModel && !viewModel.IsOverviewVisible)
            {
                viewModel.ShowOverviewCommand.Execute(null);
            }
            else
            {
                Hide();
            }
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
