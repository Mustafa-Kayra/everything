using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Everything.Core;

/// <summary>
/// Büyük/küçük harf ve aksan duyarsız karşılaştırma için karakter katlama.
/// Her karakter önceden hesaplanmış 65536 elemanlı bir tabloyla küçük harfli, aksansız temel
/// karakterine indirgenir: "Şarkı" → "sarki", "İzmir" → "izmir", "Öğrenci" → "ogrenci".
/// </summary>
public static class TextFolding
{
    private static readonly char[] Table = BuildTable();

    /// <summary>Tek bir karakteri katlar.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char Fold(char c) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Table), c);

    /// <summary>Metnin katlanmış kopyası; zaten katlanmışsa aynı örneği döndürür.</summary>
    public static string Fold(string text)
    {
        var i = 0;
        while (i < text.Length && Fold(text[i]) == text[i])
            i++;
        if (i == text.Length)
            return text;

        return string.Create(text.Length, (text, i), static (span, state) =>
        {
            state.text.AsSpan(0, state.i).CopyTo(span);
            for (var j = state.i; j < span.Length; j++)
                span[j] = Fold(state.text[j]);
        });
    }

    /// <summary>
    /// <paramref name="foldedNeedle"/> (önceden <see cref="Fold(string)"/> ile katlanmış) metinde geçiyorsa true.
    /// Bellek ayırmaz. Saf ASCII adlarda (en yaygın durum) ASCII katlama küçük harfe çevirmekle aynı
    /// olduğundan vektörel <see cref="StringComparison.OrdinalIgnoreCase"/> aramasına düşer; diğerlerinde
    /// önce ilk karakteri tabloyla karşılaştırarak tarar.
    /// </summary>
    public static bool Contains(string text, string foldedNeedle)
    {
        var n = foldedNeedle.Length;
        if (n == 0)
            return true;
        var last = text.Length - n;
        if (last < 0)
            return false;

        if (Ascii.IsValid(text))
        {
            // ASCII karakterler ASCII'ye katlanır; ASCII olmayan bir iğne burada eşleşemez.
            return Ascii.IsValid(foldedNeedle) && text.AsSpan().IndexOf(foldedNeedle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Kısa adlarda vektörel aday taraması / ara arabellek denendi; düz döngüden hızlı değil.
        ref var t = ref MemoryMarshal.GetReference(text.AsSpan());
        ref var table = ref MemoryMarshal.GetArrayDataReference(Table);
        var first = foldedNeedle[0];
        for (var i = 0; i <= last; i++)
        {
            if (Unsafe.Add(ref table, Unsafe.Add(ref t, i)) == first && MatchesAt(ref t, i, foldedNeedle))
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesAt(ref char text, int start, string folded)
    {
        ref var table = ref MemoryMarshal.GetArrayDataReference(Table);
        for (var j = 0; j < folded.Length; j++)
        {
            if (Unsafe.Add(ref table, Unsafe.Add(ref text, start + j)) != folded[j])
                return false;
        }
        return true;
    }

    /// <summary>Metin katlanmış öneki ile başlıyorsa true.</summary>
    public static bool StartsWith(string text, string foldedPrefix) =>
        foldedPrefix.Length <= text.Length
        && MatchesAt(ref MemoryMarshal.GetReference(text.AsSpan()), 0, foldedPrefix);

    /// <summary>Metin katlanmış soneki ile bitiyorsa true.</summary>
    public static bool EndsWith(string text, string foldedSuffix) =>
        foldedSuffix.Length <= text.Length
        && MatchesAt(ref MemoryMarshal.GetReference(text.AsSpan()), text.Length - foldedSuffix.Length, foldedSuffix);

    private static char[] BuildTable()
    {
        var table = new char[char.MaxValue + 1];
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            if (i < 0x80)
            {
                table[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
                continue;
            }
            if (char.IsSurrogate(c))
            {
                table[i] = c;
                continue;
            }
            table[i] = FoldSlow(c);
        }

        // Ayrıştırılan temel harf de katlanabilir ("ǣ" → "æ" → "a"); her karakter sabit noktaya inmeli.
        for (var i = 0; i <= char.MaxValue; i++)
        {
            for (var step = 0; step < 4 && table[table[i]] != table[i]; step++)
                table[i] = table[table[i]];
        }
        return table;
    }

    private static char FoldSlow(char c)
    {
        switch (c)
        {
            case 'ı':
            case 'İ':
                return 'i';
            case 'ß':
            case 'ẞ':
                return 's';
            case 'æ':
            case 'Æ':
                return 'a';
            case 'ø':
            case 'Ø':
                return 'o';
            case 'đ':
            case 'Đ':
                return 'd';
            case 'ł':
            case 'Ł':
                return 'l';
        }

        var lower = char.ToLowerInvariant(c);
        string decomposed;
        try
        {
            decomposed = lower.ToString().Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return lower;
        }

        // Yalnızca temel karakter harf/rakamsa aksanı at (ör. "é" → "e"); diğerlerini olduğu gibi bırak.
        if (decomposed.Length > 0 && decomposed[0] != lower && char.IsLetterOrDigit(decomposed[0]))
            return char.ToLowerInvariant(decomposed[0]);
        return lower;
    }
}
