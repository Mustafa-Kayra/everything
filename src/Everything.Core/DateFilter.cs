using System.Globalization;

namespace Everything.Core;

/// <summary>
/// <c>dm:</c> değerlerini (bugün, son7g, 2024-05, &gt;=17.05.2024, 2024-01..2024-03 …) yerel saate göre
/// yorumlayıp değişiklik zamanı için kapsayıcı bir UTC tik aralığına çevirir.
/// </summary>
internal static class DateFilter
{
    private static readonly string[] YearFormats = { "yyyy" };
    private static readonly string[] MonthFormats = { "yyyy-MM", "yyyy-M", "MM.yyyy", "M.yyyy" };
    private static readonly string[] DayFormats = { "yyyy-MM-dd", "yyyy-M-d", "dd.MM.yyyy", "d.M.yyyy" };

    /// <summary>Yerel saatte [Start, End) dönemi; End null ise açık uçludur (ör. "son7g").</summary>
    private readonly record struct Period(DateTime Start, DateTime? End);

    /// <summary>Değer çözülemezse false döner ve hiçbir sınır üretmez.</summary>
    public static bool TryParse(string value, DateTime nowLocal, out long? minTicks, out long? maxTicks)
    {
        minTicks = maxTicks = null;
        value = value.Trim();
        if (value.Length == 0)
            return false;

        try
        {
            var range = value.Split("..", 2);
            if (range.Length == 2)
            {
                // "a..b": a'nın başından b'nin sonuna (dahil); taraflardan biri boş bırakılabilir.
                var hasLo = range[0].Length > 0;
                var hasHi = range[1].Length > 0;
                Period lo = default, hi = default;
                if ((!hasLo && !hasHi)
                    || (hasLo && !TryParsePeriod(range[0], nowLocal, out lo))
                    || (hasHi && !TryParsePeriod(range[1], nowLocal, out hi)))
                    return false;
                long? min = hasLo ? ToUtcTicks(lo.Start) : null;
                long? max = hasHi && hi.End is { } hiEnd ? ToUtcTicks(hiEnd) - 1 : null;
                if (min is null && max is null)
                    return false;
                (minTicks, maxTicks) = (min, max);
                return true;
            }

            var op = value.StartsWith(">=", StringComparison.Ordinal) ? ">="
                : value.StartsWith("<=", StringComparison.Ordinal) ? "<="
                : value[0] is '>' or '<' or '=' ? value[..1]
                : string.Empty;
            if (!TryParsePeriod(value[op.Length..], nowLocal, out var p))
                return false;

            switch (op)
            {
                case ">":
                    // Dönemden sonra: "dm:>2024" → 2025 ve sonrası. Açık uçlu dönemin "sonrası" yoktur.
                    if (p.End is not { } after)
                        return false;
                    minTicks = ToUtcTicks(after);
                    return true;
                case ">=":
                    minTicks = ToUtcTicks(p.Start);
                    return true;
                case "<":
                    maxTicks = ToUtcTicks(p.Start) - 1;
                    return true;
                case "<=":
                    if (p.End is not { } upTo)
                        return false;
                    maxTicks = ToUtcTicks(upTo) - 1;
                    return true;
                default:
                    minTicks = ToUtcTicks(p.Start);
                    maxTicks = p.End is { } end ? ToUtcTicks(end) - 1 : null;
                    return true;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Takvim sınırlarının dışına taşan değerler (ör. "son99999y") filtre sayılmaz.
            minTicks = maxTicks = null;
            return false;
        }
    }

    private static long ToUtcTicks(DateTime local) =>
        DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().Ticks;

    private static bool TryParsePeriod(string text, DateTime now, out Period period)
    {
        period = default;
        // Katlama "bugün"/"BUGUN", "geçenyıl"/"gecenyil" gibi yazımları tek biçime indirir.
        var value = TextFolding.Fold(text.Trim());
        if (value.Length == 0)
            return false;

        var today = now.Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        var year = new DateTime(today.Year, 1, 1);
        Period? keyword = value switch
        {
            "today" or "bugun" => new Period(today, today.AddDays(1)),
            "yesterday" or "dun" => new Period(today.AddDays(-1), today),
            "thisweek" or "buhafta" => new Period(monday, monday.AddDays(7)),
            "lastweek" or "gecenhafta" => new Period(monday.AddDays(-7), monday),
            "thismonth" or "buay" => new Period(month, month.AddMonths(1)),
            "lastmonth" or "gecenay" => new Period(month.AddMonths(-1), month),
            "thisyear" or "buyil" => new Period(year, year.AddYears(1)),
            "lastyear" or "gecenyil" => new Period(year.AddYears(-1), year),
            _ => null,
        };
        if (keyword is { } k)
        {
            period = k;
            return true;
        }

        if (TryParseRelative(value, now, out var start))
        {
            period = new Period(start, null);
            return true;
        }

        var c = CultureInfo.InvariantCulture;
        const DateTimeStyles s = DateTimeStyles.None;
        if (DateTime.TryParseExact(value, DayFormats, c, s, out var d))
            period = new Period(d, d.AddDays(1));
        else if (DateTime.TryParseExact(value, MonthFormats, c, s, out var m))
            period = new Period(m, m.AddMonths(1));
        else if (DateTime.TryParseExact(value, YearFormats, c, s, out var y))
            period = new Period(y, y.AddYears(1));
        else
            return false;
        return true;
    }

    /// <summary>"son7g", "son24s", "son3ay", "son1y", "last7d", "last24h", "last3m", "last1y" → başlangıç anı.</summary>
    private static bool TryParseRelative(string value, DateTime now, out DateTime start)
    {
        start = default;
        var rest = value.StartsWith("son", StringComparison.Ordinal) ? value[3..]
            : value.StartsWith("last", StringComparison.Ordinal) ? value[4..]
            : null;
        if (rest is null)
            return false;

        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            digits++;
        if (digits == 0 || !int.TryParse(rest.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return false;

        switch (rest[digits..])
        {
            case "g" or "gun" or "d" or "day" or "days":
                start = now.AddDays(-n);
                return true;
            case "s" or "saat" or "h" or "hour" or "hours":
                start = now.AddHours(-n);
                return true;
            case "hf" or "hafta" or "w" or "week" or "weeks":
                start = now.AddDays(-7.0 * n);
                return true;
            case "ay" or "m" or "month" or "months":
                start = now.AddMonths(-n);
                return true;
            case "y" or "yil" or "year" or "years":
                start = now.AddYears(-n);
                return true;
            default:
                return false;
        }
    }
}
