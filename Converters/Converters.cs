using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ScrcpyDeck.Converters;

/// <summary>true -> green (ready), false -> amber (needs attention).</summary>
public class BoolToStateColor : IValueConverter
{
    private static readonly Brush Ready = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));

    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value is bool b && b) ? Ready : Warn;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>true -> Visible, false -> Collapsed. Pass "invert" to flip.</summary>
public class BoolToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var b = value is bool v && v;
        if (p is string s && s == "invert") b = !b;
        return b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}
