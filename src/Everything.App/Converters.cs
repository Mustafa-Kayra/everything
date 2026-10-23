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

/// <summary>Doluluk yüzdesine göre temanın renkleri: vurgu, uyarı (≥75), kritik (≥90).</summary>
public sealed class UsageBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0;
        var key = percent >= 90 ? "SystemFillColorCriticalBrush"
            : percent >= 75 ? "SystemFillColorCautionBrush"
            : "AccentFillColorDefaultBrush";
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.SteelBlue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
