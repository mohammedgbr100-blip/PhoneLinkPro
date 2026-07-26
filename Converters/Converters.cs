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

/// <summary>
/// Masks a device serial for row display (Batch 4A repair — the device-list rows bound the
/// raw Serial directly, defeating the hero's own masking rule). Delegates to
/// <see cref="ScrcpyDeck.Services.ShellLayout.MaskSerial"/> so every masked serial in the app,
/// hero or list, follows the exact same rule.
/// </summary>
public class MaskSerialConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => ScrcpyDeck.Services.ShellLayout.MaskSerial(value as string);

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>
/// The device-list row title, and its "تحديد وبثّ" accessible name (Batch 4A repair): binds
/// the whole row's AdbDevice, because <see cref="ScrcpyDeck.Models.AdbDevice.DisplayName"/>
/// falls back to the RAW serial whenever Model is not yet known (the normal state for an
/// unauthorized device, or the whole window before enrichment) — rendering it directly would
/// defeat the masking rule for exactly the same reason the hero's own name line once did.
/// Delegates to <see cref="ScrcpyDeck.Services.ShellLayout.DeviceNameOrFallback"/> rather than
/// SafeDeviceLabel: a masked serial under this row's ordinary RTL body style is not just a
/// truthfulness repeat of the row's own dedicated LTR mono serial line below it, it can also
/// come out visually reordered (the Unicode bidi algorithm reorders a masked serial's mixed
/// digit/punctuation/bullet run without an explicit LeftToRight override).
/// </summary>
public class SafeDeviceLabelConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is ScrcpyDeck.Models.AdbDevice d
            ? ScrcpyDeck.Services.ShellLayout.DeviceNameOrFallback(d.Model, d.DisplayName, d.State)
            : "";

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}
