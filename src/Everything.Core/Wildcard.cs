namespace Everything.Core;

/// <summary>
/// Büyük/küçük harf duyarsız joker karakter eşleştirici (<c>*</c> ve <c>?</c>), tüm ada uygulanır.
/// Regex'ten çok daha hızlıdır: "*.mp4" gibi yaygın kalıplar tek bir karşılaştırmaya iner.
/// </summary>
public sealed class Wildcard
{
    private readonly string _pattern;
    private readonly string? _suffix;
    private readonly string? _prefix;

    public Wildcard(string pattern)
    {
        _pattern = pattern;
        Pattern = pattern;
        if (pattern.IndexOf('?') < 0)
        {
            var star = pattern.IndexOf('*');
            if (star == 0 && pattern.IndexOf('*', 1) < 0)
                _suffix = pattern[1..];
            else if (star == pattern.Length - 1)
                _prefix = pattern[..^1];
        }
    }

    public string Pattern { get; }

    public bool IsMatch(string text)
    {
        if (_suffix is not null)
            return text.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase);
        if (_prefix is not null && _prefix.IndexOf('*') < 0)
            return text.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);
        return Match(text, _pattern);
    }

    /// <summary>Doğrusal zamanlı geri izlemeli glob eşleştirme.</summary>
    private static bool Match(string text, string pattern)
    {
        int t = 0, p = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], text[t])))
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

    private static bool CharEquals(char a, char b) =>
        a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
