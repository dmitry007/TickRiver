using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TickRiver.Core;

namespace TickRiver;

public sealed class HoursChart : FrameworkElement
{
    private Report? _report;
    private bool _combineMonths;
    private readonly List<(Rect Bounds, DateOnly Day)> _bars = [];
    public event Action<DateOnly>? DayClicked;
    public Report? Report
    {
        get => _report;
        set { _report = value; UpdateSize(); }
    }
    public bool CombineMonths
    {
        get => _combineMonths;
        set { _combineMonths = value; UpdateSize(); }
    }
    private void UpdateSize()
    {
        Height = (_combineMonths ? 1 : Math.Max(1, _report?.Days.GroupBy(d => (d.Day.Year, d.Day.Month)).Count() ?? 1)) * 245;
        MinWidth = _combineMonths ? (_report?.Days.Count ?? 0) * 3 + 80 : 0;
        _bars.Clear(); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        _bars.Clear();
        if (_report == null || _report.Days.Count == 0 || ActualWidth < 100) return;
        var groups = _combineMonths ? new[] { _report.Days.ToArray() }
            : _report.Days.GroupBy(d => (d.Day.Year, d.Day.Month)).Select(g => g.ToArray()).ToArray();
        var ink = new SolidColorBrush(Color.FromRgb(36, 56, 70));
        var muted = new SolidColorBrush(Color.FromRgb(112, 132, 145));
        var accent = new SolidColorBrush(Color.FromRgb(0, 159, 154));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(230, 237, 241)), 1);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(string text, double x, double y, double size, Brush color) => context.DrawText(new FormattedText(text,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, color, dpi), new Point(x, y));
        for (var m = 0; m < groups.Length; m++)
        {
            var days = groups[m]; var top = m * 245d;
            var heading = _combineMonths ? _report.RangeLabel : days[0].Day.ToString("MMMM yyyy");
            Label(heading + " · " + TimeFormat.Duration(days.Sum(d => d.Seconds)), 56, top + 16, 14, ink);
            const double left = 56, plotHeight = 145;
            var plotTop = top + 48; var width = ActualWidth - left - 24;
            var maximum = Math.Max(1, Math.Ceiling((double)days.Max(d => d.Hours)));
            for (var i = 0; i <= 4; i++)
            {
                var y = plotTop + plotHeight - plotHeight * i / 4;
                context.DrawLine(grid, new Point(left, y), new Point(left + width, y));
                Label($"{maximum * i / 4:0.#}h", 8, y - 9, 11, muted);
            }
            var step = width / days.Length;
            var lastLabelRight = double.NegativeInfinity;
            for (var i = 0; i < days.Length; i++)
            {
                var height = (double)days[i].Hours / maximum * plotHeight;
                var x = left + i * step + step * .13;
                var rect = new Rect(x, plotTop + plotHeight - Math.Max(height, 2), step * .74, Math.Max(height, 2));
                context.DrawRectangle(days[i].Seconds == 0 ? new SolidColorBrush(Color.FromRgb(204, 219, 227)) : accent, null, rect);
                _bars.Add((new Rect(left + i * step, plotTop, step, plotHeight + 22), days[i].Day));
                if (!_combineMonths)
                {
                    if (step >= 19 || i % 3 == 0) Label(days[i].Day.Day.ToString(), x + 1, plotTop + plotHeight + 7, 11, muted);
                }
                else if (days.Length <= 62 || i == 0 || days[i].Day.Day == 1)
                {
                    var format = days.Length > 62 ? "MMM yyyy" : days[0].Day.Year != days[^1].Day.Year ? "MMM d, yyyy" : "MMM d";
                    var label = new FormattedText(days[i].Day.ToString(format), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, muted, dpi);
                    var labelX = Math.Clamp(left + (i + .5) * step - label.Width / 2, left, Math.Max(left, left + width - label.Width));
                    if (labelX >= lastLabelRight + 8)
                    {
                        context.DrawText(label, new Point(labelX, plotTop + plotHeight + 7)); lastLabelRight = labelX + label.Width;
                    }
                }
            }
            Label(_combineMonths ? "Date" : "Day of month", left, top + 222, 11, muted);
        }
    }
    internal DateOnly? DayAt(Point position)
    {
        foreach (var (bounds, day) in _bars) { if (bounds.Contains(position) && position.X < bounds.Right) return day; }
        return null;
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var selectedDay = DayAt(e.GetPosition(this));
        Cursor = selectedDay.HasValue ? Cursors.Hand : Cursors.Arrow;
        var day = _report?.Days.FirstOrDefault(d => d.Day == selectedDay);
        ToolTip = day == null ? null : $"{day.Day:ddd, MMM d, yyyy}: {TimeFormat.Duration(day.Seconds)} ({day.Hours:0.######} hours)";
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (DayAt(e.GetPosition(this)) is DateOnly day) DayClicked?.Invoke(day);
    }
    public static void SavePng(Report report, string path, bool combineMonths = false)
    {
        var width = Math.Max(1400, combineMonths ? report.Days.Count * 3 + 132 : 0);
        var root = new System.Windows.Controls.StackPanel { Width = width, Background = Brushes.White, Margin = new Thickness(0) };
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = report.Title, FontFamily = new FontFamily("Segoe UI"), FontSize = 26,
            FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, Margin = new Thickness(34, 26, 34, 8) });
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = $"{report.RangeLabel}  ·  Total {report.Duration}  ·  {report.Seconds / 3600m:0.######} hours",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 18, Foreground = Brushes.DimGray, Margin = new Thickness(34, 0, 34, 10) });
        root.Children.Add(new HoursChart { CombineMonths = combineMonths, Report = report, Margin = new Thickness(26, 0, 26, 20) });
        root.Measure(new Size(width, double.PositiveInfinity)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = File.Create(temporary)) encoder.Save(file); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
