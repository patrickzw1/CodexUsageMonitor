using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;

namespace CodexUsageMonitor.Controls;

public sealed class WeeklyPaceBar : FrameworkElement
{
    public static readonly DependencyProperty UsedPercentProperty = DependencyProperty.Register(
        nameof(UsedPercent),
        typeof(double),
        typeof(WeeklyPaceBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ElapsedPercentProperty = DependencyProperty.Register(
        nameof(ElapsedPercent),
        typeof(double),
        typeof(WeeklyPaceBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WillExhaustProperty = DependencyProperty.Register(
        nameof(WillExhaust),
        typeof(bool),
        typeof(WeeklyPaceBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = RegisterBrush(nameof(Track), Color.FromRgb(226, 230, 240));
    public static readonly DependencyProperty RemainingProperty = RegisterBrush(nameof(Remaining), Color.FromRgb(73, 126, 232));
    public static readonly DependencyProperty WarningProperty = RegisterBrush(nameof(Warning), Color.FromRgb(217, 119, 6));
    public static readonly DependencyProperty ConsumedProperty = RegisterBrush(nameof(Consumed), Color.FromRgb(151, 160, 175));
    public static readonly DependencyProperty DividerProperty = RegisterBrush(nameof(Divider), Colors.White);
    public static readonly DependencyProperty MarkerProperty = RegisterBrush(nameof(Marker), Color.FromRgb(31, 41, 55));

    public double UsedPercent
    {
        get => (double)GetValue(UsedPercentProperty);
        set => SetValue(UsedPercentProperty, value);
    }

    public double ElapsedPercent
    {
        get => (double)GetValue(ElapsedPercentProperty);
        set => SetValue(ElapsedPercentProperty, value);
    }

    public bool WillExhaust
    {
        get => (bool)GetValue(WillExhaustProperty);
        set => SetValue(WillExhaustProperty, value);
    }

    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Remaining { get => (Brush)GetValue(RemainingProperty); set => SetValue(RemainingProperty, value); }
    public Brush Warning { get => (Brush)GetValue(WarningProperty); set => SetValue(WarningProperty, value); }
    public Brush Consumed { get => (Brush)GetValue(ConsumedProperty); set => SetValue(ConsumedProperty, value); }
    public Brush Divider { get => (Brush)GetValue(DividerProperty); set => SetValue(DividerProperty, value); }
    public Brush Marker { get => (Brush)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var barRect = new Rect(0, 12, ActualWidth, Math.Max(10, ActualHeight - 24));
        drawingContext.DrawRoundedRectangle(Track, null, barRect, 5, 5);

        if (ElapsedPercent > 0)
        {
            drawingContext.PushClip(new RectangleGeometry(barRect, 5, 5));
            drawingContext.DrawRectangle(WillExhaust ? Warning : Remaining, null, barRect);

            var usedWidth = ActualWidth * Math.Clamp(UsedPercent, 0, 100) / 100;
            if (usedWidth > 0)
            {
                drawingContext.DrawRectangle(Consumed, null, new Rect(0, barRect.Top, usedWidth, barRect.Height));
            }

            drawingContext.Pop();
        }

        var dividerPen = new Pen(Divider, 2);
        for (var day = 1; day < 7; day++)
        {
            var x = ActualWidth * day / 7;
            drawingContext.DrawLine(dividerPen, new Point(x, barRect.Top), new Point(x, barRect.Bottom));
        }

        if (ElapsedPercent <= 0)
        {
            return;
        }

        var markerX = Math.Clamp(ActualWidth * Math.Clamp(ElapsedPercent, 0, 100) / 100, 2, Math.Max(2, ActualWidth - 2));
        var markerPen = new Pen(Marker, 1.25)
        {
            DashStyle = DashStyles.Dash
        };
        drawingContext.DrawLine(markerPen, new Point(markerX, 2), new Point(markerX, ActualHeight - 2));
    }

    private static DependencyProperty RegisterBrush(string name, Color fallback)
        => DependencyProperty.Register(
            name,
            typeof(Brush),
            typeof(WeeklyPaceBar),
            new FrameworkPropertyMetadata(new SolidColorBrush(fallback), FrameworkPropertyMetadataOptions.AffectsRender));
}
