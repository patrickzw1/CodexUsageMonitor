using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CodexUsageMonitor.ViewModels;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexUsageMonitor.Controls;

public sealed class ModelDonutChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(ModelDonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public static readonly DependencyProperty TotalTextProperty = DependencyProperty.Register(
        nameof(TotalText),
        typeof(string),
        typeof(ModelDonutChart),
        new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(Brush),
        typeof(ModelDonutChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(28, 15, 23, 42)), FrameworkPropertyMetadataOptions.AffectsRender));

    private INotifyCollectionChanged? _observableSource;

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string TotalText
    {
        get => (string)GetValue(TotalTextProperty);
        set => SetValue(TotalTextProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var rows = ItemsSource?.Cast<object>().OfType<ModelUsageRowViewModel>().ToArray()
                   ?? Array.Empty<ModelUsageRowViewModel>();
        var size = Math.Min(ActualWidth, ActualHeight);
        var thickness = Math.Max(20, size * 0.13);
        var radius = Math.Max(1, size / 2 - thickness / 2 - 2);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(
            null,
            new Pen(Track, thickness),
            center,
            radius,
            radius);

        var startAngle = -90d;
        foreach (var row in rows.Where(item => item.SharePercent > 0))
        {
            var sweep = Math.Min(row.SharePercent * 3.6, 360 - startAngle - 90);
            var gap = sweep > 5 ? 1.5 : 0;
            DrawArc(drawingContext, center, radius, startAngle + gap / 2, Math.Max(0, sweep - gap), row.Color, thickness);
            startAngle += sweep;
        }

        var typeface = new Typeface(
            (FontFamily)Application.Current.Resources["UiFont"],
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        var total = new FormattedText(
            TotalText,
            CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(19, size * 0.13),
            (Brush)Application.Current.Resources["InkBrush"],
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(total, new Point(center.X - total.Width / 2, center.Y - total.Height / 2 - 4));

        var caption = new FormattedText(
            "TOKENS",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(8, size * 0.045),
            (Brush)Application.Current.Resources["MutedBrush"],
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y + total.Height / 2 - 1));
    }

    private static void DrawArc(
        DrawingContext drawingContext,
        Point center,
        double radius,
        double startAngle,
        double sweepAngle,
        string color,
        double thickness)
    {
        if (sweepAngle <= 0)
        {
            return;
        }

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (sweepAngle >= 359.9)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweepAngle);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, sweepAngle > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    private static void OnItemsSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var chart = (ModelDonutChart)sender;
        if (chart._observableSource is not null)
        {
            chart._observableSource.CollectionChanged -= chart.OnCollectionChanged;
        }

        chart._observableSource = args.NewValue as INotifyCollectionChanged;
        if (chart._observableSource is not null)
        {
            chart._observableSource.CollectionChanged += chart.OnCollectionChanged;
        }
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => InvalidateVisual();
}
