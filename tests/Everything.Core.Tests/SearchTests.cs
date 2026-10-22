namespace Everything.Core.Tests;

public class SearchTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FileIndex _index;

    public SearchTests()
    {
        _tree.File("Belgeler/Rapor-2024.pdf", 1000);
        _tree.File("Belgeler/rapor-2023.docx", 500);
        _tree.File("Belgeler/notlar.txt", 20);
        _tree.File("Videolar/tatil.mp4", 5 * 1024 * 1024);
        _tree.File("Videolar/IMG_0001.jpg", 2048);
        _tree.File("Videolar/IMG_12.jpg", 2048);
        _tree.File("Proje/src/rapor.cs", 10);
        _tree.Dir("Raporlar");
        _index = FileScanner.Scan(_tree.Root);
    }

    public void Dispose() => _tree.Dispose();

    private string[] Find(string query) =>
        SearchEngine.Search(new[] { _index }, SearchQuery.Parse(query)).Hits
            .Select(h => h.Index.GetName(h.Id))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void EmptyQuery_ReturnsNothing() => Assert.Empty(Find("   "));

    [Fact]
    public void Substring_IsCaseInsensitive() =>
        Assert.Equal(new[] { "Rapor-2024.pdf", "Raporlar", "rapor-2023.docx", "rapor.cs" }, Find("RAPOR"));

    [Fact]
    public void MultipleTerms_AreAnded() => Assert.Equal(new[] { "Rapor-2024.pdf" }, Find("rapor 2024"));

    [Fact]
    public void Exclusion() => Assert.Equal(new[] { "Rapor-2024.pdf", "rapor-2023.docx", "rapor.cs" }, Find("rapor !lar"));

    [Fact]
    public void Wildcards_MatchWholeName()
    {
        Assert.Equal(new[] { "IMG_0001.jpg" }, Find("IMG_????.jpg"));
        Assert.Equal(new[] { "IMG_0001.jpg", "IMG_12.jpg" }, Find("*.JPG"));
    }

    [Theory]
    [InlineData("*.mp4", "Tatil.MP4", true)]
    [InlineData("*.mp4", "tatil.mp4.part", false)]
    [InlineData("IMG*", "img_001.jpg", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("??.txt", "ab.txt", true)]
    [InlineData("??.txt", "abc.txt", false)]
    [InlineData("*", "", true)]
    public void Wildcard_Matches(string pattern, string text, bool expected) =>
        Assert.Equal(expected, new Wildcard(pattern).IsMatch(text));

    [Fact]
    public void ExtensionFilter() => Assert.Equal(new[] { "Rapor-2024.pdf", "rapor-2023.docx" }, Find("ext:pdf;docx"));

    [Fact]
    public void SizeFilter()
    {
        Assert.Equal(new[] { "tatil.mp4" }, Find("size:>1mb"));
        Assert.Equal(new[] { "IMG_0001.jpg", "IMG_12.jpg" }, Find("file: size:2kb..2kb"));
        Assert.Equal(new[] { "notlar.txt", "rapor.cs" }, Find("file: boyut:<100"));
    }

    [Fact]
    public void KindFilter()
    {
        Assert.Equal(new[] { "Raporlar" }, Find("folder:rapor"));
        Assert.Equal(new[] { "Rapor-2024.pdf", "rapor-2023.docx", "rapor.cs" }, Find("dosya: rapor"));
    }

    [Fact]
    public void PathTerms_MatchFullPath()
    {
        Assert.Equal(new[] { "rapor.cs" }, Find("proje/src rapor"));
        Assert.Equal(new[] { "IMG_0001.jpg", "IMG_12.jpg", "tatil.mp4" }, Find("path:videolar file:"));
    }

    [Fact]
    public void QuotedPhrase_IsSingleTerm()
    {
        _tree.File("Belgeler/yıllık rapor.pdf", 1);
        var index = FileScanner.Scan(_tree.Root);
        var hits = SearchEngine.Search(new[] { index }, SearchQuery.Parse("\"k rap\"")).Hits;
        Assert.Equal("yıllık rapor.pdf", index.GetName(Assert.Single(hits).Id));
    }

    [Fact]
    public void QuotedPrefixValue_IsSupported()
    {
        _tree.File("Eski Belgeler/rapor.pdf", 1);
        var index = FileScanner.Scan(_tree.Root);
        var query = SearchQuery.Parse($"path:\"{_tree.PathOf("Eski Belgeler")}\" ext:pdf");
        var hits = SearchEngine.Search(new[] { index }, query).Hits;
        Assert.Equal("rapor.pdf", index.GetName(Assert.Single(hits).Id));
    }

    [Fact]
    public void MaxResults_TruncatesButCountsAll()
    {
        var result = SearchEngine.Search(new[] { _index }, SearchQuery.Parse("a"), maxResults: 2);
        Assert.Equal(2, result.Hits.Count);
        Assert.True(result.TotalMatches > 2);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("10mb", 10L * 1024 * 1024)]
    [InlineData("1,5 GB", 1610612736L)]
    [InlineData("500k", 512_000L)]
    [InlineData("2048", 2048L)]
    public void ByteSize_Parses(string text, long expected)
    {
        Assert.True(ByteSize.TryParse(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("sarki", "Şarkı.mp3")]
    [InlineData("SARKI", "Şarkı.mp3")]
    [InlineData("ogrenci", "ogrenci-notlari.txt|Öğrenci listesi.xlsx")]
    [InlineData("izmir", "İzmir.jpg")]
    [InlineData("IZMIR", "İzmir.jpg")]
    [InlineData("cikis", "çıkış.txt")]
    [InlineData("ÇIKIŞ", "çıkış.txt")]
    [InlineData("istanbul", "ISTANBUL.png")]
    public void Diacritics_AreIgnoredByDefault(string query, string expected)
    {
        AddTurkishNames();
        Assert.Equal(expected.Split('|'), Find(query, FileScanner.Scan(_tree.Root)));
    }

    [Fact]
    public void Diacritics_WildcardsExclusionsAndPaths()
    {
        AddTurkishNames();
        var index = FileScanner.Scan(_tree.Root);
        Assert.Equal(new[] { "Şarkı.mp3" }, Find("sark?.mp3", index));
        Assert.Equal(new[] { "Şarkı.mp3" }, Find("*sarki*", index));
        Assert.Equal(new[] { "Şarkı.mp3" }, Find("SARKI*", index));
        Assert.Equal(new[] { "ogrenci-notlari.txt" }, Find("ogrenci !liste", index));
        Assert.Equal(new[] { "Şarkı.mp3" }, Find("path:muzik file:", index));
        Assert.Equal(new[] { "Şarkı.mp3" }, Find("muzik/sarki", index));
    }

    [Fact]
    public void Diacritics_StrictWhenDisabled()
    {
        AddTurkishNames();
        var index = FileScanner.Scan(_tree.Root);
        Assert.Empty(Find("sarki", index, ignoreDiacritics: false));
        Assert.Empty(Find("*sarki*", index, ignoreDiacritics: false));
        Assert.Equal(new[] { "Şarkı.mp3" }, Find("şarkı", index, ignoreDiacritics: false));
        Assert.Equal(new[] { "Şarkı.mp3" }, Find("ŞARK?.MP3", index, ignoreDiacritics: false));
        Assert.False(SearchQuery.Parse("sarki", ignoreDiacritics: false).IgnoreDiacritics);
        Assert.True(SearchQuery.Parse("sarki").IgnoreDiacritics);
        Assert.Equal(new[] { "sarki" }, SearchQuery.Parse("ŞARKI").NameTerms);
    }

    [Theory]
    [InlineData("*sarki*", "Şarkı.mp3", true)]
    [InlineData("sark?.mp3", "ŞARKI.MP3", true)]
    [InlineData("izmir*", "İzmir.jpg", true)]
    [InlineData("*.MP3", "şarkı.mp3", true)]
    [InlineData("o*c?", "Öğrenci", true)]
    [InlineData("*sarki*", "Sanki.mp3", false)]
    [InlineData("??.txt", "çı.txt", true)]
    public void Wildcard_MatchesWithFolding(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, new Wildcard(pattern, ignoreDiacritics: true).IsMatch(text));
        Assert.Equal(pattern, new Wildcard(pattern, ignoreDiacritics: true).Pattern);
    }

    [Fact]
    public void Wildcard_StrictByDefault() => Assert.False(new Wildcard("*sarki*").IsMatch("Şarkı.mp3"));

    private void AddTurkishNames()
    {
        _tree.File("Müzik/Şarkı.mp3", 1);
        _tree.File("Okul/Öğrenci listesi.xlsx", 1);
        _tree.File("Okul/ogrenci-notlari.txt", 1);
        _tree.File("Resimler/İzmir.jpg", 1);
        _tree.File("Resimler/ISTANBUL.png", 1);
        _tree.File("çıkış.txt", 1);
    }

    private static string[] Find(string query, FileIndex index, bool ignoreDiacritics = true) =>
        SearchEngine.Search(new[] { index }, SearchQuery.Parse(query, ignoreDiacritics)).Hits
            .Select(h => h.Index.GetName(h.Id))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void ByteSize_Formats() =>
        Assert.Equal("1.5 GB", ByteSize.Format(1610612736L, System.Globalization.CultureInfo.InvariantCulture));
}
