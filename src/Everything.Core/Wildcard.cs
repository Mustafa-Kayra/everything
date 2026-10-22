namespace Everything.Core;

/// <summary>
/// Büyük/küçük harf duyarsız joker karakter eşleştirici (<c>*</c> ve <c>?</c>), tüm ada uygulanır.
/// Regex'ten çok daha hızlıdır: "*.mp4" gibi yaygın kalıplar tek bir karşılaştırmaya iner.
/// <c>ignoreDiacritics</c> ile karakterler <see cref="TextFolding"/> üzerinden karşılaştırılır ("*sarki*" → "Şarkı.mp3").
/// </summary>
public sealed class Wildcard
{
    private readonly string _pattern;
    private readonly string? _suffix;
    private readonly string? _prefix;

    public Wildcard(string pattern, bool ignoreDiacritics = false)
    {
        Pattern = pattern;
        IgnoreDiacritics = ignoreDiacritics;
        // Katlamada kalıp bir kez katlanır; '*' ve '?' kendilerine katlanır.
        _pattern = ignoreDiacritics ? TextFolding.Fold(pattern) : pattern;
        if (_pattern.IndexOf('?') < 0)
        {
            var star = _pattern.IndexOf('*');
            if (star == 0 && _pattern.IndexOf('*', 1) < 0)
                _suffix = _pattern[1..];
            else if (star == _pattern.Length - 1)
                _prefix = _pattern[..^1];
        }
    }

    public string Pattern { get; }

    /// <summary>Aksan ve Türkçe karakter duyarsız eşleştirme ("ş" = "s", "İ" = "i").</summary>
    public bool IgnoreDiacritics { get; }

    public bool IsMatch(string text)
    {
        if (_suffix is not null)
            return IgnoreDiacritics ? TextFolding.EndsWith(text, _suffix) : text.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase);
        if (_prefix is not null && _prefix.IndexOf('*') < 0)
            return IgnoreDiacritics ? TextFolding.StartsWith(text, _prefix) : text.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);
        return IgnoreDiacritics ? Match<FoldedComparer>(text, _pattern) : Match<IgnoreCaseComparer>(text, _pattern);
    }

    /// <summary>Doğrusal zamanlı geri izlemeli glob eşleştirme.</summary>
    private static bool Match<TComparer>(string text, string pattern)
        where TComparer : struct, ICharComparer
    {
        int t = 0, p = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || TComparer.CharsEqual(pattern[p], text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private interface ICharComparer
    {
        static abstract bool CharsEqual(char pattern, char text);
    }

    private readonly struct IgnoreCaseComparer : ICharComparer
    {
        public static bool CharsEqual(char pattern, char text) =>
            pattern == text || char.ToUpperInvariant(pattern) == char.ToUpperInvariant(text);
    }

    /// <summary>Kalıp önceden katlanmıştır; yalnızca metin karakteri katlanır.</summary>
    private readonly struct FoldedComparer : ICharComparer
    {
        public static bool CharsEqual(char pattern, char text) => pattern == TextFolding.Fold(text);
    }
}
