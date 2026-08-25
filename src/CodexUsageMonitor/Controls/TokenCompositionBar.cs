using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace CodexUsageMonitor.Controls;

public sealed class TokenCompositionBar : FrameworkElement
{
    public static readonly DependencyProperty UncachedInputProperty = Register(nameof(UncachedInput));
    public static readonly DependencyProperty CachedInputProperty = Register(nameof(CachedInput));
    public static readonly DependencyProperty OutputProperty = Register(nameof(Output));
    public static readonly DependencyProperty ReasoningProperty = Register(nameof(Reasoning));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(Brush),
        typeof(TokenCompositionBar),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(226, 230, 240)), FrameworkPropertyMetadataOptions.AffectsRender));

    public double UncachedInput { get => (double)GetValue(UncachedInputProperty); set => SetValue(UncachedInputProperty, value); }
    public double CachedInput { get => (double)GetValue(CachedInputProperty); set => SetValue(CachedInputProperty, value); }
    public double Output { get => (double)GetValue(OutputProperty); set => SetValue(OutputProperty, value); }
    public double Reasoning { get => (double)GetValue(ReasoningProperty); set => SetValue(ReasoningProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var values = new[] { UncachedInput, CachedInput, Output, Reasoning };
        var brushes = new Brush[]
        {
            new SolidColorBrush(Color.FromRgb(15, 169, 203)),
            new SolidColorBrush(Color.FromRgb(139, 92, 246)),
            new SolidColorBrush(Color.FromRgb(47, 104, 222)),
            new SolidColorBrush(Color.FromRgb(245, 158, 11))
        };
        var total = values.Sum(value => Math.Max(value, 0));
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(Track, null, rect, 6, 6);
        if (total <= 0)
        {
            return;
        }

        drawingContext.PushClip(new RectangleGeometry(rect, 6, 6));
        var x = 0d;
        for (var index = 0; index < values.Length; index++)
        {
            var width = index == values.Length - 1
                ? ActualWidth - x
                : ActualWidth * Math.Max(values[index], 0) / total;
            drawingContext.DrawRectangle(brushes[index], null, new Rect(x, 0, Math.Max(width - 1, 0), ActualHeight));
            x += width;
        }
        drawingContext.Pop();
    }

    private static DependencyProperty Register(string name)
        => DependencyProperty.Register(
            name,
            typeof(double),
            typeof(TokenCompositionBar),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
}
