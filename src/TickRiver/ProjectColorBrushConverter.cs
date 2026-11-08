using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TickRiver.Core;

namespace TickRiver;

public sealed class ProjectColorBrushConverter : IValueConverter
{
    private readonly Dictionary<long, SolidColorBrush> _brushes = new();
    internal ProjectColors? Colors { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long id) return Brushes.Transparent;
        if (!_brushes.TryGetValue(id, out var brush)) _brushes[id] = brush = new SolidColorBrush(GetColor(id));
        return brush;
    }
    internal void Refresh() { foreach (var (id, brush) in _brushes) brush.Color = GetColor(id); }
    private Color GetColor(long id) => (Color)ColorConverter.ConvertFromString((Colors?.Get(id) ?? ProjectColors.Default(id)).Hex);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
