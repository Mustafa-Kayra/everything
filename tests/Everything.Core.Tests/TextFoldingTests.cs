namespace Everything.Core.Tests;

public class TextFoldingTests
{
    [Theory]
    [InlineData("Şarkı", "sarki")]
    [InlineData("Öğrenci", "ogrenci")]
    [InlineData("İzmir", "izmir")]
    [InlineData("IZMIR", "izmir")]
    [InlineData("çıkış", "cikis")]
    [InlineData("ÇIKIŞ", "cikis")]
    [InlineData("Ñandú café", "nandu cafe")]
    [InlineData("Straße", "strase")]
    [InlineData("Æsir Øresund", "asir oresund")]
    [InlineData("Đakovo Łódź", "dakovo lodz")]
    [InlineData("rapor-2024.pdf", "rapor-2024.pdf")]
    [InlineData("Привет", "привет")]
    [InlineData("", "")]
    public void Fold_LowercasesAndStripsDiacritics(string text, string expected) =>
        Assert.Equal(expected, TextFolding.Fold(text));

    [Fact]
    public void Fold_ReturnsSameInstanceWhenAlreadyFolded()
    {
        var text = "zaten-katli.txt";
        Assert.Same(text, TextFolding.Fold(text));
    }

    [Fact]
    public void Fold_IsIdempotentForAllChars()
    {
        for (var c = 0; c <= char.MaxValue; c++)
        {
            var once = TextFolding.Fold((char)c);
            Assert.Equal(once, TextFolding.Fold(once));
        }
    }

    [Theory]
    [InlineData("Şarkı.mp3", "", true)]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    [InlineData("ab", "abc", false)]
    [InlineData("Şarkı.mp3", "sarki", true)]
    [InlineData("Şarkı.mp3", "mp3", true)]
    [InlineData("Şarkı.mp3", "arki.", true)]
    [InlineData("Şarkı.mp3", "sarkim", false)]
    [InlineData("Yeni Şarkı", "sarki", true)]
    [InlineData("şşşarkı", "sarki", true)]
    [InlineData("RAPOR.PDF", "pdf", true)]
    [InlineData("RAPOR.PDF", "pdf2", false)]
    [InlineData("RAPOR.PDF", "rapor.pdf", true)]
    [InlineData("rapor.pdf", "ş", false)]
    [InlineData("Привет мир", "привет", true)]
    [InlineData("hello", "привет", false)]
    [InlineData("İ", "i", true)]
    public void Contains_Cases(string text, string needle, bool expected) =>
        Assert.Equal(expected, TextFolding.Contains(text, TextFolding.Fold(needle)));

    [Fact]
    public void StartsWithAndEndsWith()
    {
        Assert.True(TextFolding.StartsWith("Şarkı.mp3", "sar"));
        Assert.False(TextFolding.StartsWith("Şa", "sar"));
        Assert.True(TextFolding.EndsWith("Şarkı.MP3", ".mp3"));
        Assert.True(TextFolding.EndsWith("Şarkı", "ki"));
        Assert.False(TextFolding.EndsWith("mp3", ".mp3"));
    }
}
