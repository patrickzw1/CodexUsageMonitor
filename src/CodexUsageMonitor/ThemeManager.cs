using System.Windows;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfApplication = System.Windows.Application;

namespace CodexUsageMonitor;

internal static class ThemeManager
{
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("InkBrush", "#151823", "#F4F7FC"),
        ("MutedBrush", "#515A6C", "#B5BECF"),
        ("IndigoBrush", "#315FC7", "#85A8FF"),
        ("BlueBrush", "#3978E7", "#66A2FF"),
        ("VioletBrush", "#7C63E7", "#AA98FF"),
        ("AmberBrush", "#C98016", "#F1B963"),
        ("GreenBrush", "#168465", "#54CAA5"),
        ("LineBrush", "#4DD0D7E2", "#52606C80"),
        ("SurfaceBrush", "#E8F5F7FB", "#F0181D27"),
        ("CardBrush", "#BFFDFEFF", "#E9232A36"),
        ("SettingsPanelBrush", "#FFF9FBFE", "#FF232A36"),
        ("SubtleBrush", "#160F172A", "#28FFFFFF"),
        ("FrameBorderBrush", "#78FFFFFF", "#4DFFFFFF"),
        ("CardBorderBrush", "#9FFFFFFF", "#24FFFFFF"),
        ("CardHoverBrush", "#DEFFFFFF", "#F02D3543"),
        ("CardHoverBorderBrush", "#B8FFFFFF", "#48FFFFFF"),
        ("ChromeBrush", "#8FFFFFFF", "#E0181E29"),
        ("TabHostBrush", "#34FFFFFF", "#80222835"),
        ("TabSelectedBrush", "#E6FFFFFF", "#F0363E4E"),
        ("RangeHostBrush", "#42FFFFFF", "#80222835"),
        ("InsetBrush", "#24FFFFFF", "#5C2A313D"),
        ("InnerCardBrush", "#34FFFFFF", "#A02B3340"),
        ("InnerCardBorderBrush", "#63FFFFFF", "#2EFFFFFF"),
        ("HoverBrush", "#140F172A", "#20FFFFFF"),
        ("PressedBrush", "#220F172A", "#32FFFFFF"),
        ("PillBrush", "#D9DEE7", "#3A4352"),
        ("PillHoverBrush", "#C9D0DB", "#465264"),
        ("PillPressedBrush", "#B8C2D0", "#556278"),
        ("PillBorderBrush", "#6B9AA4B4", "#52FFFFFF"),
        ("ToggleTrackBrush", "#C8CDD6", "#5A6575"),
        ("ProgressTrackBrush", "#260F172A", "#665A6270"),
        ("ScrollThumbBrush", "#806B7280", "#808995A8"),
        ("ScrollThumbHoverBrush", "#A65D6678", "#B0A8B3C4"),
        ("ScrollThumbActiveBrush", "#C8315FC7", "#D085A8FF"),
        ("WarningPanelBrush", "#D9FFF4DC", "#E3392D1C"),
        ("WarningBorderBrush", "#85E4AA57", "#A6785125"),
        ("WarningTextBrush", "#87520A", "#FFD18A"),
        ("IndigoSubtleBrush", "#26315FC7", "#3885A8FF"),
        ("IndigoTrackBrush", "#2A315FC7", "#3D85A8FF"),
        ("AmberSubtleBrush", "#27E6A63B", "#3AF1B963"),
        ("AmberTrackBrush", "#35E6A63B", "#45F1B963"),
        ("VioletSubtleBrush", "#228667E8", "#3AAA98FF"),
        ("PaceConsumedBrush", "#97A0AF", "#778399"),
        ("PaceDividerBrush", "#F8FFFFFF", "#CC1B202A"),
        ("PaceMarkerBrush", "#1F2937", "#E8F0FF")
    ];

    public static bool IsDarkMode { get; private set; }

    public static void Apply(bool isDarkMode)
    {
        IsDarkMode = isDarkMode;
        var resources = WpfApplication.Current.Resources;
        foreach (var (key, light, dark) in Palette)
        {
            var brush = new SolidColorBrush(Parse(isDarkMode ? dark : light));
            brush.Freeze();
            resources[key] = brush;
        }

        foreach (Window window in WpfApplication.Current.Windows)
        {
            WindowBackdrop.Apply(window, isDarkMode);
            InvalidateVisualTree(window);
        }
    }

    private static MediaColor Parse(string value)
        => (MediaColor)MediaColorConverter.ConvertFromString(value);

    private static void InvalidateVisualTree(DependencyObject root)
    {
        if (root is UIElement element)
        {
            element.InvalidateVisual();
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            InvalidateVisualTree(VisualTreeHelper.GetChild(root, index));
        }
    }
}
