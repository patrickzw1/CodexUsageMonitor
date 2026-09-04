using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using CodexUsageMonitor.Core.Models;
using CodexUsageMonitor.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using FlowDirection = System.Windows.FlowDirection;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CodexUsageMonitor.Controls;

/// <summary>Applied official daily buckets only; no interpolation over missing dates.</summary>
public sealed class HistoryTrendChart : Control
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(HistoryTrendData), typeof(HistoryTrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));
    public static readonly DependencyProperty IsEnglishProperty = DependencyProperty.Register(
        nameof(IsEnglish), typeof(bool), typeof(HistoryTrendChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));
    public static readonly DependencyProperty LineBrushProperty = RegisterBrush(nameof(LineBrush));
    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(nameof(GridBrush));
    public static readonly DependencyProperty PointFocusBrushProperty = RegisterBrush(nameof(PointFocusBrush));
    public static readonly DependencyProperty TipSurfaceProperty = RegisterBrush(nameof(TipSurface));
    public static readonly DependencyProperty TipForegroundProperty = RegisterBrush(nameof(TipForeground));
    public static readonly DependencyProperty TipBorderProperty = RegisterBrush(nameof(TipBorder));

    private DailyUsagePoint[] _days = [];
    private Point[] _points = [];
    private int _selectedIndex = -1;
    private Rect _tipBounds = Rect.Empty;

    public HistoryTrendChart()
    {
        Focusable = true;
        IsVisibleChanged += (_, _) => { if (!IsVisible) SelectPoint(-1); };
    }

    public HistoryTrendData? Data { get => (HistoryTrendData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public bool IsEnglish { get => (bool)GetValue(IsEnglishProperty); set => SetValue(IsEnglishProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush PointFocusBrush { get => (Brush)GetValue(PointFocusBrushProperty); set => SetValue(PointFocusBrushProperty, value); }
    public Brush TipSurface { get => (Brush)GetValue(TipSurfaceProperty); set => SetValue(TipSurfaceProperty, value); }
    public Brush TipForeground { get => (Brush)GetValue(TipForegroundProperty); set => SetValue(TipForegroundProperty, value); }
    public Brush TipBorder { get => (Brush)GetValue(TipBorderProperty); set => SetValue(TipBorderProperty, value); }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(HistoryTrendChart),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnDataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var chart = (HistoryTrendChart)sender;
        chart._days = chart.Data?.Days.OrderBy(day => day.Date).ToArray() ?? [];
        chart._points = [];
        chart.SelectPoint(-1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(RenderSize));
        _tipBounds = Rect.Empty;
        if (Data is null || _days.Length == 0 || ActualWidth < 150 || ActualHeight < 100) return;

        var plot = new Rect(72, 28, ActualWidth - 84, ActualHeight - 78);
        var span = Data.End.DayNumber - Data.Start.DayNumber;
        var maximum = Math.Max(1d, _days.Max(day => (double)day.Tokens));
        _points = _days.Select(day => new Point(
            span == 0 ? plot.Left + plot.Width / 2 : plot.Left + (day.Date.DayNumber - Data.Start.DayNumber) / (double)span * plot.Width,
            plot.Bottom - day.Tokens / maximum * plot.Height)).ToArray();

        DrawText(dc, "Tokens", new Point(0, 0), Foreground);
        for (var tick = 0; tick <= 2; tick++)
        {
            var y = plot.Bottom - plot.Height * tick / 2;
            dc.DrawLine(new Pen(GridBrush, 1), new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(AxisValue(maximum * tick / 2), Foreground);
            dc.DrawText(label, new Point(plot.Left - label.Width - 10, y - label.Height / 2));
        }
        DrawDate(Data.Start, span == 0 ? plot.Left + plot.Width / 2 : plot.Left);
        if (span > 0) DrawDate(Data.End, plot.Right);
        if (span > 1 && plot.Width >= 280)
        {
            var middle = Data.Start.AddDays(span / 2);
            DrawDate(middle, plot.Left + (middle.DayNumber - Data.Start.DayNumber) / (double)span * plot.Width);
        }

        var linePen = new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var first = 0;
        for (var last = 0; last < _days.Length; last++)
        {
            if (last + 1 < _days.Length && _days[last + 1].Date.DayNumber == _days[last].Date.DayNumber + 1) continue;
            // Each run ends at a real calendar gap. Neither line nor fill crosses it.
            if (last > first)
            {
                var area = new StreamGeometry();
                using (var context = area.Open())
                {
                    context.BeginFigure(new Point(_points[first].X, plot.Bottom), true, true);
                    for (var index = first; index <= last; index++) context.LineTo(_points[index], true, false);
                    context.LineTo(new Point(_points[last].X, plot.Bottom), true, false);
                }
                dc.PushOpacity(0.10);
                dc.DrawGeometry(LineBrush, null, area);
                dc.Pop();
                var line = new StreamGeometry();
                using (var context = line.Open())
                {
                    context.BeginFigure(_points[first], false, false);
                    for (var index = first + 1; index <= last; index++) context.LineTo(_points[index], true, false);
                }
                dc.DrawGeometry(null, linePen, line);
            }
            first = last + 1;
        }
        foreach (var point in _points) dc.DrawEllipse(LineBrush, null, point, 3, 3);
        if (_selectedIndex >= 0 && _selectedIndex < _points.Length)
        {
            var point = _points[_selectedIndex];
            dc.DrawEllipse(null, new Pen(PointFocusBrush, 2), point, 6, 6);
            DrawTip(dc, point);
        }

        void DrawDate(DateOnly date, double x)
        {
            var format = Data.Start.Year == Data.End.Year ? IsEnglish ? "MMM d" : "M月d日" : "yyyy-MM-dd";
            var label = Text(date.ToString(format, Culture), Foreground);
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, Math.Max(0, ActualWidth - label.Width)), plot.Bottom + 10));
        }
    }

    private void DrawTip(DrawingContext dc, Point point)
    {
        var label = Text(PointLabel(_selectedIndex), TipForeground);
        label.MaxTextWidth = Math.Max(1, ActualWidth - 32);
        var width = Math.Min(ActualWidth - 8, label.Width + 20);
        var height = label.Height + 16;
        var x = Math.Clamp(point.X - width / 2, 4, Math.Max(4, ActualWidth - width - 4));
        var y = point.Y >= height + 12 ? point.Y - height - 10 : point.Y + 10;
        y = Math.Clamp(y, 4, Math.Max(4, ActualHeight - height - 4));
        _tipBounds = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(TipSurface, new Pen(TipBorder, 1), _tipBounds, 8, 8);
        dc.DrawText(label, new Point(x + 10, y + 8));
    }

    private CultureInfo Culture => CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "zh-CN");
    private string PointLabel(int index) => index < 0 ? string.Empty :
        $"{_days[index].Date.ToString(IsEnglish ? "MMM d, yyyy" : "yyyy年M月d日", Culture)}\n{_days[index].Tokens.ToString("N0", Culture)} Tokens";
    private FormattedText Text(string text, Brush brush) => new(text, Culture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private void DrawText(DrawingContext dc, string text, Point position, Brush brush) => dc.DrawText(Text(text, brush), position);
    private static string AxisValue(double value) => value switch
    {
        >= 1e12 => value.ToString("0.#E+0", CultureInfo.InvariantCulture),
        >= 1e9 => (value / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1e6 => (value / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1e3 => (value / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString("0.#", CultureInfo.InvariantCulture)
    };

    private int HitPoint(Point cursor)
    {
        var selected = -1;
        var distance = 14d * 14;
        for (var index = 0; index < _points.Length; index++)
        {
            var delta = _points[index] - cursor;
            if (delta.LengthSquared <= distance) { selected = index; distance = delta.LengthSquared; }
        }
        return selected;
    }

    private void SelectPoint(int index)
    {
        if (_selectedIndex == index) return;
        _selectedIndex = index;
        _tipBounds = Rect.Empty;
        AutomationProperties.SetItemStatus(this, PointLabel(index));
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SelectPoint(HitPoint(e.GetPosition(this))); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (!IsKeyboardFocusWithin) SelectPoint(-1); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); if (_days.Length > 0) SelectPoint(0); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); SelectPoint(-1); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_days.Length > 0)
        {
            var index = e.Key switch
            {
                Key.Left => Math.Max(0, _selectedIndex - 1),
                Key.Right => Math.Min(_days.Length - 1, _selectedIndex + 1),
                Key.Home => 0,
                Key.End => _days.Length - 1,
                _ => -1
            };
            if (index >= 0) { SelectPoint(index); e.Handled = true; }
        }
        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TrendAutomationPeer(this);
    private sealed class TrendAutomationPeer(HistoryTrendChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(HistoryTrendChart);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }
}
