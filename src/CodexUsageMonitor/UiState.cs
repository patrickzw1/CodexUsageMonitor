using System.Windows;

namespace CodexUsageMonitor;

public static class UiState
{
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.RegisterAttached(
        "IsLoading",
        typeof(bool),
        typeof(UiState),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen",
        typeof(bool),
        typeof(UiState),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded",
        typeof(bool),
        typeof(UiState),
        new FrameworkPropertyMetadata(false, OnIsExpandedChanged));

    public static readonly DependencyProperty SuppressFocusedExpansionProperty = DependencyProperty.RegisterAttached(
        "SuppressFocusedExpansion",
        typeof(bool),
        typeof(UiState),
        new FrameworkPropertyMetadata(false));

    public static bool GetIsLoading(DependencyObject element)
        => (bool)element.GetValue(IsLoadingProperty);

    public static void SetIsLoading(DependencyObject element, bool value)
        => element.SetValue(IsLoadingProperty, value);

    public static bool GetIsOpen(DependencyObject element)
        => (bool)element.GetValue(IsOpenProperty);

    public static void SetIsOpen(DependencyObject element, bool value)
        => element.SetValue(IsOpenProperty, value);

    public static bool GetIsExpanded(DependencyObject element)
        => (bool)element.GetValue(IsExpandedProperty);

    public static void SetIsExpanded(DependencyObject element, bool value)
        => element.SetValue(IsExpandedProperty, value);

    public static bool GetSuppressFocusedExpansion(DependencyObject element)
        => (bool)element.GetValue(SuppressFocusedExpansionProperty);

    public static void SetSuppressFocusedExpansion(DependencyObject element, bool value)
        => element.SetValue(SuppressFocusedExpansionProperty, value);

    private static void OnIsExpandedChanged(DependencyObject element, DependencyPropertyChangedEventArgs _)
    {
        if (element is not System.Windows.Controls.Control control)
        {
            return;
        }

        if (control.IsLoaded)
        {
            ApplyExpansionState(control);
            return;
        }

        control.Loaded -= ExpansionControl_Loaded;
        control.Loaded += ExpansionControl_Loaded;
    }

    private static void ExpansionControl_Loaded(object sender, RoutedEventArgs e)
    {
        var control = (System.Windows.Controls.Control)sender;
        control.Loaded -= ExpansionControl_Loaded;
        ApplyExpansionState(control);
    }

    private static void ApplyExpansionState(System.Windows.Controls.Control control)
        => VisualStateManager.GoToState(
            control,
            GetIsExpanded(control) ? "Expanded" : "Compact",
            SystemParameters.ClientAreaAnimation);
}
