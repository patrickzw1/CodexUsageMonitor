using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexUsageMonitor.ViewModels;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;

namespace CodexUsageMonitor;

public partial class MainWindow : System.Windows.Window
{
    private readonly bool _keepOpen;
    private WpfButton? _settingsAnchor;

    public MainWindow(MainViewModel viewModel, bool keepOpen = false)
    {
        InitializeComponent();
        DataContext = viewModel;
        _keepOpen = keepOpen;
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this, ThemeManager.IsDarkMode);
        Deactivated += (_, _) =>
        {
            SettingsPopup.IsOpen = false;
            UpdatePopup.IsOpen = false;
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

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        UpdatePopup.IsOpen = false;
        Hide();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => ToggleSettingsPopup((WpfButton)sender);

    public void ToggleSettingsPopup(WpfButton anchor)
    {
        if (SettingsPopup.IsOpen && ReferenceEquals(_settingsAnchor, anchor))
        {
            SettingsPopup.IsOpen = false;
            return;
        }

        _settingsAnchor?.SetResourceReference(ToolTipProperty, "LocSettings");
        _settingsAnchor = anchor;
        _settingsAnchor.ToolTip = null;
        SettingsPopup.DataContext = DataContext;
        SettingsPopup.PlacementTarget = anchor;
        SettingsPopup.IsOpen = true;
    }

    private void SettingsPopup_Closed(object? sender, EventArgs e)
    {
        _settingsAnchor?.SetResourceReference(ToolTipProperty, "LocSettings");
        _settingsAnchor = null;
    }

    private void UpdatePopup_Closed(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsUpdateFlyoutOpen = false;
        }
    }

    private void UpdatePopup_Opened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (UpdatePopup.IsOpen)
            {
                ViewUpdateButton.Focus();
            }
        });
    }

    private void UpdatePopup_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            UpdatePopup.IsOpen = false;
            e.Handled = true;
        }
    }

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
            SettingsPopup.IsOpen = false;
            UpdatePopup.IsOpen = false;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (UpdatePopup.IsOpen)
            {
                UpdatePopup.IsOpen = false;
            }
            else if (SettingsPopup.IsOpen)
            {
                SettingsPopup.IsOpen = false;
            }
            else if (DataContext is MainViewModel viewModel && !viewModel.IsOverviewVisible)
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
