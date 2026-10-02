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
    private readonly List<(Rect Bounds, DateOnly Day)> _bars = [];
    public event Action<DateOnly>? DayClicked;
    public Report? Report
    {
        get => _report;
        set { _report = value; Height = Math.Max(1, value?.Days.GroupBy(d => (d.Day.Year, d.Day.Month)).Count() ?? 1) * 245; InvalidateVisual(); }
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        _bars.Clear();
        if (_report == null || ActualWidth < 100) return;
        var groups = _report.Days.GroupBy(d => (d.Day.Year, d.Day.Month)).ToArray();
        var ink = new SolidColorBrush(Color.FromRgb(36, 56, 70));
        var muted = new SolidColorBrush(Color.FromRgb(112, 132, 145));
        var accent = new SolidColorBrush(Color.FromRgb(0, 159, 154));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(230, 237, 241)), 1);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(string text, double x, double y, double size, Brush color) => context.DrawText(new FormattedText(text,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, color, dpi), new Point(x, y));
        for (var m = 0; m < groups.Length; m++)
        {
            var days = groups[m].ToArray(); var top = m * 245d;
            Label(days[0].Day.ToString("MMMM yyyy") + " · " + TimeFormat.Duration(days.Sum(d => d.Seconds)), 56, top + 16, 14, ink);
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
            for (var i = 0; i < days.Length; i++)
            {
                var height = (double)days[i].Hours / maximum * plotHeight;
                var x = left + i * step + step * .13;
                var rect = new Rect(x, plotTop + plotHeight - Math.Max(height, 2), step * .74, Math.Max(height, 2));
                context.DrawRectangle(days[i].Seconds == 0 ? new SolidColorBrush(Color.FromRgb(204, 219, 227)) : accent, null, rect);
                _bars.Add((new Rect(x, plotTop, step * .74, plotHeight + 22), days[i].Day));
                if (step >= 19 || i % 3 == 0) Label(days[i].Day.Day.ToString(), x + 1, plotTop + plotHeight + 7, 11, muted);
            }
            Label("Day of month", left, top + 222, 11, muted);
        }
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var bar = _bars.FirstOrDefault(b => b.Bounds.Contains(e.GetPosition(this)));
        Cursor = bar.Bounds.IsEmpty || bar.Bounds.Width == 0 ? Cursors.Arrow : Cursors.Hand;
        var day = _report?.Days.FirstOrDefault(d => d.Day == bar.Day);
        ToolTip = day == null ? null : $"{day.Day:ddd, MMM d}: {TimeFormat.Duration(day.Seconds)} ({day.Hours:0.######} hours)";
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var bar = _bars.FirstOrDefault(b => b.Bounds.Contains(e.GetPosition(this)));
        if (bar.Bounds.Width > 0) DayClicked?.Invoke(bar.Day);
    }
    public static void SavePng(Report report, string path)
    {
        var root = new System.Windows.Controls.StackPanel { Width = 1400, Background = Brushes.White, Margin = new Thickness(0) };
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = report.Title, FontFamily = new FontFamily("Segoe UI"), FontSize = 26,
            FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, Margin = new Thickness(34, 26, 34, 8) });
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = $"{report.RangeLabel}  ·  Total {report.Duration}  ·  {report.Seconds / 3600m:0.######} hours",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 18, Foreground = Brushes.DimGray, Margin = new Thickness(34, 0, 34, 10) });
        root.Children.Add(new HoursChart { Report = report, Margin = new Thickness(26, 0, 26, 20) });
        root.Measure(new Size(1400, double.PositiveInfinity)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1400, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = File.Create(temporary)) encoder.Save(file); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
