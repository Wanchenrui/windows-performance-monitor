using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PerfMonitor.Desktop;

public sealed record TrendHover(TrendPoint? Point, string Text);

/// <summary>Native retained drawing. Only real adjacent observations are joined.</summary>
public sealed class TrendChart : FrameworkElement
{
    public IReadOnlyList<TrendPoint> Primary { get; private set; } = [];
    public IReadOnlyList<TrendPoint> Secondary { get; private set; } = [];
    public Color Accent { get; set; } = Color.FromRgb(197, 219, 116);
    public Color SecondaryAccent { get; set; } = Color.FromRgb(106, 170, 155);
    public bool ShowAxes { get; set; } = true;
    public bool Percent { get; set; } = true;
    public string PrimaryName { get; set; } = "使用率";
    public string SecondaryName { get; set; } = "";
    public double WindowSeconds { get; set; } = 60;
    public double EndElapsed { get; private set; }
    private Point? _pointer;

    public TrendChart()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        MouseMove += (_, e) => { _pointer = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _pointer = null; ToolTip = null; InvalidateVisual(); };
    }

    public void Update(IReadOnlyList<TrendPoint> primary, IReadOnlyList<TrendPoint> secondary, double endElapsed)
    {
        Primary = primary; Secondary = secondary; EndElapsed = endElapsed; InvalidateVisual();
    }

    public static IReadOnlyList<IReadOnlyList<TrendPoint>> Segments(IReadOnlyList<TrendPoint> points, double start, double end)
    {
        var result = new List<IReadOnlyList<TrendPoint>>();
        var segment = new List<TrendPoint>();
        foreach (var point in points.Where(point => point.ElapsedSeconds >= start && point.ElapsedSeconds <= end))
        {
            if (point.Value is null || point.BreakBefore)
            {
                if (segment.Count > 0) result.Add(segment.ToArray());
                segment.Clear();
            }
            if (point.Value is not null) segment.Add(point);
        }
        if (segment.Count > 0) result.Add(segment.ToArray());
        return result;
    }

    public static TrendHover HoverAt(IReadOnlyList<TrendPoint> primary, IReadOnlyList<TrendPoint> secondary,
        double elapsed, string firstName, string secondName, bool percent, double start = 0)
    {
        var point = primary.Where(point => point.ElapsedSeconds >= start)
            .MinBy(point => Math.Abs(point.ElapsedSeconds - elapsed));
        if (point is null || Math.Abs(point.ElapsedSeconds - elapsed) > 1.75)
            return new(null, "此时段没有实际观测");
        var text = $"{point.ObservedAtUtc.ToLocalTime():HH:mm:ss}\n{firstName}：{FormatValue(point.Value, percent)}\n{point.Quality}";
        var second = secondary.FirstOrDefault(item => item.ObservationSequence == point.ObservationSequence);
        if (second is not null) text += $"\n{secondName}：{FormatValue(second.Value, false)}";
        return new(point, text);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 10 || ActualHeight < 10) return;
        var left = ShowAxes ? 52d : 2d;
        var top = ShowAxes ? 12d : 4d;
        var rect = new Rect(left, top, Math.Max(1, ActualWidth - left - 12), Math.Max(1, ActualHeight - top - (ShowAxes ? 32 : 8)));
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var start = Math.Max(0, EndElapsed - WindowSeconds);
        var span = Math.Max(1, EndElapsed - start);
        var visible = Primary.Concat(Secondary).Where(point => point.ElapsedSeconds >= start && point.Value is not null).ToArray();
        var max = Percent ? 100 : Math.Max(1024, visible.Select(point => point.Value!.Value).DefaultIfEmpty(0).Max() * 1.15);
        if (ShowAxes)
        {
            for (var row = 0; row <= 4; row++)
            {
                var y = rect.Top + row * rect.Height / 4;
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(43, 48, 53)), 1), new(rect.Left, y), new(rect.Right, y));
                Label(dc, Percent ? $"{100 - row * 25}%" : FormatRate(max * (4 - row) / 4), new(0, y - 7), 10);
            }
            Label(dc, EndElapsed < WindowSeconds ? "本次会话" : WindowSeconds == 60 ? "60 秒前" : "5 分钟前", new(rect.Left, rect.Bottom + 10), 10);
            Label(dc, "最新观测", new(rect.Right - 48, rect.Bottom + 10), 10);
            if (Secondary.Count > 0)
            {
                Label(dc, "● " + PrimaryName, new(rect.Right - 142, 0), 10, new SolidColorBrush(Accent));
                Label(dc, "● " + SecondaryName, new(rect.Right - 72, 0), 10, new SolidColorBrush(SecondaryAccent));
            }
        }
        Point Position(TrendPoint point) => new(rect.Left + (point.ElapsedSeconds - start) / span * rect.Width,
            rect.Bottom - Math.Clamp(point.Value!.Value / max, 0, 1) * rect.Height);
        DrawSeries(dc, Primary, Accent, start, rect, Position);
        DrawSeries(dc, Secondary, SecondaryAccent, start, rect, Position);
        if (visible.Length == 0 && ShowAxes)
            Label(dc, "等待实际观测  ·  不预填历史曲线", new(rect.Left + 22, rect.Top + rect.Height * .45), 13);
        if (_pointer is { } pointer && rect.Contains(pointer))
        {
            var time = start + (pointer.X - rect.Left) / rect.Width * span;
            var hover = HoverAt(Primary, Secondary, time, PrimaryName, SecondaryName, Percent, start);
            if (hover.Point is { } nearest)
            {
                var x = rect.Left + (nearest.ElapsedSeconds - start) / span * rect.Width;
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(98, 109, 119)), 1), new(x, rect.Top), new(x, rect.Bottom));
                if (nearest.Value is not null) dc.DrawEllipse(new SolidColorBrush(Accent), new Pen(Brushes.White, 1), Position(nearest), 4, 4);
            }
            ToolTip = hover.Text;
        }
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<TrendPoint> points, Color color, double start,
        Rect rect, Func<TrendPoint, Point> position)
    {
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, ShowAxes ? 2.4 : 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var segment in Segments(points, start, EndElapsed))
        {
            var first = position(segment[0]);
            if (segment.Count > 1)
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(first, false, false);
                    context.PolyLineTo(segment.Skip(1).Select(position).ToArray(), true, false);
                }
                if (ShowAxes)
                {
                    var area = new StreamGeometry();
                    using (var context = area.Open())
                    {
                        context.BeginFigure(new(first.X, rect.Bottom), true, true);
                        context.PolyLineTo(segment.Select(position).ToArray(), false, false);
                        context.LineTo(new(position(segment[^1]).X, rect.Bottom), false, false);
                    }
                    var fill = new LinearGradientBrush(Color.FromArgb(48, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
                    dc.DrawGeometry(fill, null, area);
                }
                dc.DrawGeometry(null, pen, geometry);
            }
            else dc.DrawEllipse(brush, null, first, 2, 2);
        }
        var last = points.LastOrDefault(point => point.ElapsedSeconds >= start);
        if (last?.Value is not null)
        {
            var at = position(last);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B)), null, at, 7, 7);
            dc.DrawEllipse(brush, null, at, 2.8, 2.8);
        }
    }

    private void Label(DrawingContext dc, string text, Point point, double size, Brush? brush = null) =>
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush ?? new SolidColorBrush(Color.FromRgb(134, 145, 157)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
    public static string FormatValue(double? value, bool percent) => value is null ? "缺测" : percent ? $"{value:0.0}%" : FormatRate(value.Value);
    public static string FormatRate(double value)
    {
        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s"];
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[index]}");
    }
}

public sealed class LoadRing : FrameworkElement
{
    private double? _value;
    public Color Accent { get; set; } = Color.FromRgb(197, 219, 116);
    public bool Historical { get; set; }
    public void Update(double? value, bool historical) { _value = value; Historical = historical; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 10) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 6;
        var track = new Pen(new SolidColorBrush(Color.FromRgb(49, 55, 61)), 4);
        dc.DrawEllipse(null, track, center, radius, radius);
        var accent = Historical ? Color.FromRgb(133, 144, 155) : Accent;
        var pen = new Pen(new SolidColorBrush(accent), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (_value >= 100) dc.DrawEllipse(null, pen, center, radius, radius);
        else if (_value > 0)
        {
            var angle = Math.Clamp(_value.Value / 100, 0, 1) * Math.PI * 2 - Math.PI / 2;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new(center.X, center.Y - radius), false, false);
                context.ArcTo(new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)),
                    new Size(radius, radius), 0, _value > 50, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, pen, geometry);
        }
        var text = new FormattedText(_value is null ? "—" : $"{_value:0}%", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size > 100 ? 30 : 17,
            new SolidColorBrush(Color.FromRgb(230, 235, 238)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
