using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Everything.Core;

namespace Everything.App;

/// <summary>long bayt → "1,5 GB".</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? ByteSize.Format(bytes) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>0–100 yüzdeyi parametrede verilen maksimum genişliğe ölçekler.</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var max = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 100;
        var percent = value is double d ? d : 0;
        return Math.Max(0, Math.Min(max, max * percent / 100));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Doluluk yüzdesine göre renk: mavi, turuncu (≥75), kırmızı (≥90).</summary>
public sealed class UsageBrushConverter : IValueConverter
{
    private static readonly Brush Normal = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush Danger = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0;
        return percent >= 90 ? Danger : percent >= 75 ? Warn : Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
