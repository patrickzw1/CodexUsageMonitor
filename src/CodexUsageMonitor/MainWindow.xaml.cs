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
    private WpfButton? _settingsAnchor;
    private bool _returnFocusToUpdateEntry;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this, ThemeManager.IsDarkMode);
        Deactivated += (_, _) => HandleDeactivated();
        Loaded += (_, _) => UpdateUpdateEntryExpansion();
    }

    private void HandleDeactivated()
    {
        SettingsPopup.IsOpen = false;
        UpdatePopup.IsOpen = false;
        Hide();
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

        var returnFocus = _returnFocusToUpdateEntry;
        _returnFocusToUpdateEntry = false;
        var focusScope = FocusManager.GetFocusScope(UpdateButton);
        var retainedFocus = UpdateButton.IsKeyboardFocusWithin
                            || ReferenceEquals(FocusManager.GetFocusedElement(focusScope), UpdateButton);
        UiState.SetSuppressFocusedExpansion(UpdateButton, !returnFocus && retainedFocus);
        if (returnFocus && UpdateButton.IsVisible && UpdateButton.IsEnabled)
        {
            UiState.SetIsExpanded(UpdateButton, true);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(UpdateButton), UpdateButton);
                Keyboard.Focus(UpdateButton);
                UpdateUpdateEntryExpansion();
            });
            return;
        }

        UpdateUpdateEntryExpansion();
    }

    private void UpdatePopup_Opened(object? sender, EventArgs e)
    {
        _returnFocusToUpdateEntry = false;
        UiState.SetSuppressFocusedExpansion(UpdateButton, false);
        UpdateUpdateEntryExpansion();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (UpdatePopup.IsOpen)
            {
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(UpdateActionButton), UpdateActionButton);
                UpdateActionButton.Focus();
            }
        });
    }

    private void UpdatePopup_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            _returnFocusToUpdateEntry = true;
            UpdatePopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void UpdatePopupActionButton_Click(object sender, RoutedEventArgs e)
        => _returnFocusToUpdateEntry = true;

    private void UpdateButton_ExpansionStateChanged(object sender, RoutedEventArgs e)
        => UpdateUpdateEntryExpansion();

    private void UpdateUpdateEntryExpansion()
    {
        var focusScope = FocusManager.GetFocusScope(UpdateButton);
        var hasFocus = UpdateButton.IsKeyboardFocusWithin
                       || ReferenceEquals(FocusManager.GetFocusedElement(focusScope), UpdateButton);
        var expand = UpdatePopup.IsOpen
                     || UpdateButton.IsMouseOver
                     || (hasFocus && !UiState.GetSuppressFocusedExpansion(UpdateButton));
        UiState.SetIsExpanded(UpdateButton, expand);
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
        if (e.Key == System.Windows.Input.Key.Tab && UiState.GetSuppressFocusedExpansion(UpdateButton))
        {
            UiState.SetSuppressFocusedExpansion(UpdateButton, false);
            UpdateUpdateEntryExpansion();
        }

        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (UpdatePopup.IsOpen)
            {
                _returnFocusToUpdateEntry = true;
                UpdatePopup.IsOpen = false;
            }
            else if (SettingsPopup.IsOpen)
            {
                SettingsPopup.IsOpen = false;
            }
            else if (HistoryPage.HasOpenInputPopup)
            {
                // Let the native input handle Escape before page navigation, including date cancellation.
                base.OnPreviewKeyDown(e);
                return;
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
