using System.Globalization;
using System.Text;

namespace Everything.Core;

public enum EntryKind
{
    Any,
    File,
    Folder,
}

/// <summary>
/// Arama sorgusu. Söz dizimi:
/// <list type="bullet">
/// <item><c>rapor 2024</c> — adında her iki kelime de geçenler</item>
/// <item><c>"yıllık rapor"</c> — tam ifade</item>
/// <item><c>*.mp4</c>, <c>IMG_????.jpg</c> — joker karakterler (tüm ada uyar)</item>
/// <item><c>!temp</c> — hariç tut</item>
/// <item><c>ext:pdf;docx</c> — uzantı</item>
/// <item><c>size:&gt;1gb</c>, <c>size:&lt;10kb</c>, <c>size:10mb..100mb</c> — boyut</item>
/// <item><c>file:</c> / <c>folder:</c> (veya <c>dosya:</c> / <c>klasör:</c>) — tür</item>
/// <item><c>ProjeX\src</c> — ters/eğik çizgi içeren terimler tam yolda aranır</item>
/// <item><c>path:Belgeler</c> — terimi tam yolda ara</item>
/// </list>
/// </summary>
public sealed class SearchQuery
{
    private SearchQuery()
    {
    }

    public string Text { get; private init; } = string.Empty;
    public IReadOnlyList<string> NameTerms { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> PathTerms { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedTerms { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<Wildcard> Wildcards { get; private init; } = Array.Empty<Wildcard>();
    public IReadOnlySet<string>? Extensions { get; private init; }
    public long? MinSize { get; private init; }
    public long? MaxSize { get; private init; }
    public EntryKind Kind { get; private init; }

    /// <summary>Sorgu hiçbir kısıt içermiyorsa true.</summary>
    public bool IsEmpty =>
        NameTerms.Count == 0 && PathTerms.Count == 0 && ExcludedTerms.Count == 0 && Wildcards.Count == 0
        && Extensions is null && MinSize is null && MaxSize is null && Kind == EntryKind.Any;

    public bool NeedsFullPath => PathTerms.Count > 0;

    public static SearchQuery Parse(string? text)
    {
        text ??= string.Empty;
        var names = new List<string>();
        var paths = new List<string>();
        var excluded = new List<string>();
        var wildcards = new List<Wildcard>();
        HashSet<string>? extensions = null;
        long? min = null, max = null;
        var kind = EntryKind.Any;

        foreach (var token in Tokenize(text))
        {
            var (prefix, value) = SplitPrefix(token);
            switch (prefix)
            {
                case "ext":
                case "uzantı":
                case "uzanti":
                    extensions ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in value.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        extensions.Add(e.TrimStart('*').TrimStart('.'));
                    continue;
                case "size":
                case "boyut":
                    ParseSize(value, ref min, ref max);
                    continue;
                case "file":
                case "files":
                case "dosya":
                    kind = EntryKind.File;
                    if (value.Length > 0)
                        AddTerm(value, false, names, paths, excluded, wildcards);
                    continue;
                case "folder":
                case "folders":
                case "klasör":
                case "klasor":
                    kind = EntryKind.Folder;
                    if (value.Length > 0)
                        AddTerm(value, false, names, paths, excluded, wildcards);
                    continue;
                case "path":
                case "yol":
                    if (value.Length > 0)
                        paths.Add(value);
                    continue;
            }

            AddTerm(token.Value, token.Quoted, names, paths, excluded, wildcards);
        }

        return new SearchQuery
        {
            Text = text,
            NameTerms = names,
            PathTerms = paths,
            ExcludedTerms = excluded,
            Wildcards = wildcards,
            Extensions = extensions,
            MinSize = min,
            MaxSize = max,
            Kind = kind,
        };
    }

    private static void AddTerm(string term, bool quoted, List<string> names, List<string> paths, List<string> excluded, List<Wildcard> wildcards)
    {
        if (term.Length == 0)
            return;
        if (!quoted && term.Length > 1 && term[0] == '!')
        {
            excluded.Add(term[1..]);
            return;
        }
        if (!quoted && (term.Contains('*') || term.Contains('?')))
        {
            wildcards.Add(new Wildcard(term));
            return;
        }
        if (term.Contains('\\') || term.Contains('/'))
        {
            paths.Add(term.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            return;
        }
        names.Add(term);
    }

    private static (string Prefix, string Value) SplitPrefix(Token t)
    {
        var token = t.Value;
        var colon = token.IndexOf(':');
        // "C:\..." gibi sürücü harflerini önek sanma; tırnak içindeki iki nokta önek değildir.
        if (colon <= 1 || (t.Quoted && colon >= t.QuoteStart))
            return (string.Empty, token);
        var prefix = token[..colon].ToLower(CultureInfo.InvariantCulture);
        return prefix switch
        {
            "ext" or "uzantı" or "uzanti" or "size" or "boyut" or "file" or "files" or "dosya"
                or "folder" or "folders" or "klasör" or "klasor" or "path" or "yol" => (prefix, token[(colon + 1)..].Trim('"')),
            _ => (string.Empty, token),
        };
    }

    /// <param name="QuoteStart">İlk tırnağın değer içindeki konumu (ör. path:"..." için 5).</param>
    private readonly record struct Token(string Value, bool Quoted, int QuoteStart);

    private static IEnumerable<Token> Tokenize(string text)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        var quoteStart = -1;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                if (quoteStart < 0)
                    quoteStart = sb.Length;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (sb.Length > 0)
                    yield return new Token(sb.ToString(), quoteStart >= 0, quoteStart);
                sb.Clear();
                quoteStart = -1;
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0)
            yield return new Token(sb.ToString(), quoteStart >= 0, quoteStart);
    }

    private static void ParseSize(string value, ref long? min, ref long? max)
    {
        value = value.Trim();
        var range = value.Split("..", 2);
        if (range.Length == 2)
        {
            if (ByteSize.TryParse(range[0], out var lo))
                min = lo;
            if (ByteSize.TryParse(range[1], out var hi))
                max = hi;
            return;
        }

        if (value.StartsWith(">=", StringComparison.Ordinal) && ByteSize.TryParse(value[2..], out var v1))
            min = v1;
        else if (value.StartsWith("<=", StringComparison.Ordinal) && ByteSize.TryParse(value[2..], out var v2))
            max = v2;
        else if (value.StartsWith('>') && ByteSize.TryParse(value[1..], out var v3))
            min = v3 + 1;
        else if (value.StartsWith('<') && ByteSize.TryParse(value[1..], out var v4))
            max = v4 - 1;
        else if (ByteSize.TryParse(value, out var v5))
            min = v5;
    }
}
