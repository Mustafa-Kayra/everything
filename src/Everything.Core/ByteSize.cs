using System.Globalization;

namespace Everything.Core;

public static class ByteSize
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>1536 → "1,5 KB" (geçerli kültüre göre).</summary>
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0)
            return "-" + Format(-bytes, culture);
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, culture) + " " + Units[unit];
    }

    /// <summary>"10mb", "1.5 GB", "2048", "500k" gibi metinleri bayta çevirir.</summary>
    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        if (text.Length == 0)
            return false;

        var split = 0;
        while (split < text.Length && (char.IsDigit(text[split]) || text[split] is '.' or ','))
            split++;
        if (split == 0)
            return false;

        var numberText = text[..split].Replace(',', '.');
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return false;

        var multiplier = text[split..] switch
        {
            "" or "b" or "byte" or "bytes" => 1L,
            "k" or "kb" or "kib" => 1L << 10,
            "m" or "mb" or "mib" => 1L << 20,
            "g" or "gb" or "gib" => 1L << 30,
            "t" or "tb" or "tib" => 1L << 40,
            _ => -1L,
        };
        if (multiplier < 0)
            return false;
        bytes = (long)(number * multiplier);
        return true;
    }
}
