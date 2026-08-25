using System.Windows;
using System.Windows.Media;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;

namespace CodexUsageMonitor.Controls;

public sealed class CircularProgress : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(CircularProgress),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(System.Windows.Media.Brush),
        typeof(CircularProgress),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(System.Windows.Media.Brush),
        typeof(CircularProgress),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.RoyalBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public System.Windows.Media.Brush Track
    {
        get => (System.Windows.Media.Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public System.Windows.Media.Brush Fill
    {
        get => (System.Windows.Media.Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var thickness = Math.Max(8, Math.Min(ActualWidth, ActualHeight) * 0.075);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - thickness);
        var center = new System.Windows.Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(null, new Pen(Track, thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0, 100);
        if (value <= 0)
        {
            return;
        }

        if (value >= 99.999)
        {
            drawingContext.DrawEllipse(null, new Pen(Fill, thickness), center, radius, radius);
            return;
        }

        var start = PointOnCircle(center, radius, -90);
        var end = PointOnCircle(center, radius, -90 + value * 3.6);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(Fill, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }

    private static System.Windows.Point PointOnCircle(System.Windows.Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new System.Windows.Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }
}
