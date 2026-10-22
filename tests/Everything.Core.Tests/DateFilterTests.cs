namespace Everything.Core.Tests;

public class DateFilterTests
{
    /// <summary>Çarşamba, 21 Ekim 2026 15:00 (yerel).</summary>
    private static readonly DateTime Now = new(2026, 10, 21, 15, 0, 0, DateTimeKind.Local);

    private static long Utc(int year, int month, int day, int hour = 0) =>
        new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks;

    private static (long? Min, long? Max) Range(string text)
    {
        var q = SearchQuery.Parse(text, true, Now);
        return (q.MinModifiedTicks, q.MaxModifiedTicks);
    }

    /// <summary>[başlangıç, bitiş) yerel dönemi → (min, max) dahil UTC tik.</summary>
    private static (long? Min, long? Max) Period(long startUtc, long endUtc) => (startUtc, endUtc - 1);

    [Theory]
    [InlineData("dm:today")]
    [InlineData("dm:bugün")]
    [InlineData("dm:bugun")]
    [InlineData("tarih:BUGÜN")]
    [InlineData("date:today")]
    public void Today(string text) => Assert.Equal(Period(Utc(2026, 10, 21), Utc(2026, 10, 22)), Range(text));

    [Theory]
    [InlineData("dm:yesterday")]
    [InlineData("dm:dün")]
    [InlineData("dm:dun")]
    public void Yesterday(string text) => Assert.Equal(Period(Utc(2026, 10, 20), Utc(2026, 10, 21)), Range(text));

    [Theory]
    [InlineData("dm:thisweek")]
    [InlineData("dm:buhafta")]
    public void ThisWeek_StartsMonday(string text) => Assert.Equal(Period(Utc(2026, 10, 19), Utc(2026, 10, 26)), Range(text));

    [Theory]
    [InlineData("dm:lastweek")]
    [InlineData("dm:geçenhafta")]
    [InlineData("dm:gecenhafta")]
    public void LastWeek(string text) => Assert.Equal(Period(Utc(2026, 10, 12), Utc(2026, 10, 19)), Range(text));

    [Fact]
    public void WeekStartsMonday_EvenOnSunday()
    {
        var sunday = new DateTime(2026, 10, 25, 23, 0, 0, DateTimeKind.Local);
        var q = SearchQuery.Parse("dm:buhafta", true, sunday);
        Assert.Equal(Period(Utc(2026, 10, 19), Utc(2026, 10, 26)), (q.MinModifiedTicks, q.MaxModifiedTicks));
    }

    [Theory]
    [InlineData("dm:thismonth", 2026, 10, 2026, 11)]
    [InlineData("dm:buay", 2026, 10, 2026, 11)]
    [InlineData("dm:lastmonth", 2026, 9, 2026, 10)]
    [InlineData("dm:geçenay", 2026, 9, 2026, 10)]
    [InlineData("dm:gecenay", 2026, 9, 2026, 10)]
    public void Months(string text, int y1, int m1, int y2, int m2) =>
        Assert.Equal(Period(Utc(y1, m1, 1), Utc(y2, m2, 1)), Range(text));

    [Theory]
    [InlineData("dm:thisyear", 2026)]
    [InlineData("dm:buyıl", 2026)]
    [InlineData("dm:buyil", 2026)]
    [InlineData("dm:lastyear", 2025)]
    [InlineData("dm:geçenyıl", 2025)]
    [InlineData("dm:gecenyil", 2025)]
    public void Years(string text, int year) => Assert.Equal(Period(Utc(year, 1, 1), Utc(year + 1, 1, 1)), Range(text));

    [Fact]
    public void LastMonth_InJanuary_IsDecember()
    {
        var q = SearchQuery.Parse("dm:geçenay", true, new DateTime(2027, 1, 5, 9, 0, 0, DateTimeKind.Local));
        Assert.Equal(Period(Utc(2026, 12, 1), Utc(2027, 1, 1)), (q.MinModifiedTicks, q.MaxModifiedTicks));
    }

    [Theory]
    [InlineData("dm:son7g")]
    [InlineData("dm:last7d")]
    [InlineData("dm:SON7G")]
    public void Relative_Days(string text) =>
        Assert.Equal(((long?)Now.AddDays(-7).ToUniversalTime().Ticks, (long?)null), Range(text));

    [Theory]
    [InlineData("dm:son24s")]
    [InlineData("dm:last24h")]
    public void Relative_Hours(string text) =>
        Assert.Equal(((long?)Now.AddHours(-24).ToUniversalTime().Ticks, (long?)null), Range(text));

    [Theory]
    [InlineData("dm:son3ay")]
    [InlineData("dm:last3m")]
    public void Relative_Months(string text) =>
        Assert.Equal(((long?)Now.AddMonths(-3).ToUniversalTime().Ticks, (long?)null), Range(text));

    [Theory]
    [InlineData("dm:son1y")]
    [InlineData("dm:last1y")]
    public void Relative_Years(string text) =>
        Assert.Equal(((long?)Now.AddYears(-1).ToUniversalTime().Ticks, (long?)null), Range(text));

    [Fact]
    public void Relative_LessThan_MeansOlder() =>
        Assert.Equal(((long?)null, (long?)(Now.AddDays(-30).ToUniversalTime().Ticks - 1)), Range("dm:<son30g"));

    [Theory]
    [InlineData("dm:2024", 2024, 1, 1, 2025, 1, 1)]
    [InlineData("dm:2024-05", 2024, 5, 1, 2024, 6, 1)]
    [InlineData("dm:05.2024", 2024, 5, 1, 2024, 6, 1)]
    [InlineData("dm:2024-05-17", 2024, 5, 17, 2024, 5, 18)]
    [InlineData("dm:17.05.2024", 2024, 5, 17, 2024, 5, 18)]
    [InlineData("dm:2024-12-31", 2024, 12, 31, 2025, 1, 1)]
    [InlineData("dm:2024-01-01..2024-03-31", 2024, 1, 1, 2024, 4, 1)]
    [InlineData("dm:2024..2025", 2024, 1, 1, 2026, 1, 1)]
    [InlineData("dm:01.2024..03.2024", 2024, 1, 1, 2024, 4, 1)]
    public void Absolute(string text, int y1, int m1, int d1, int y2, int m2, int d2) =>
        Assert.Equal(Period(Utc(y1, m1, d1), Utc(y2, m2, d2)), Range(text));

    [Fact]
    public void Comparisons()
    {
        Assert.Equal(((long?)Utc(2024, 1, 2), (long?)null), Range("dm:>2024-01-01"));
        Assert.Equal(((long?)Utc(2024, 1, 1), (long?)null), Range("dm:>=2024-01-01"));
        Assert.Equal(((long?)null, (long?)(Utc(2024, 1, 1) - 1)), Range("dm:<2024-01-01"));
        Assert.Equal(((long?)null, (long?)(Utc(2024, 1, 2) - 1)), Range("dm:<=2024-01-01"));
        Assert.Equal(((long?)Utc(2025, 1, 1), (long?)null), Range("dm:>2024"));
        Assert.Equal(((long?)Utc(2024, 1, 1), (long?)null), Range("dm:2024.."));
        Assert.Equal(((long?)null, (long?)(Utc(2025, 1, 1) - 1)), Range("dm:..2024"));
    }

    [Fact]
    public void MultipleFilters_Intersect() =>
        Assert.Equal(Period(Utc(2024, 3, 1), Utc(2024, 4, 1)), Range("dm:2024 tarih:2024-03"));

    [Theory]
    [InlineData("dm:")]
    [InlineData("dm:yarın")]
    [InlineData("dm:2024-13")]
    [InlineData("dm:32.01.2024")]
    [InlineData("dm:son7x")]
    [InlineData("dm:son99999999y")]
    [InlineData("dm:..")]
    [InlineData("dm:>son7g")]
    public void Unparseable_IsIgnored(string text)
    {
        var q = SearchQuery.Parse(text, true, Now);
        Assert.Null(q.MinModifiedTicks);
        Assert.Null(q.MaxModifiedTicks);
        Assert.True(q.IsEmpty);
    }

    [Fact]
    public void DateFilter_MakesQueryNonEmpty_AndCombinesWithTerms()
    {
        var q = SearchQuery.Parse("rapor dm:bugün", true, Now);
        Assert.False(q.IsEmpty);
        Assert.Equal(new[] { "rapor" }, q.NameTerms);
    }

    [Fact]
    public void Matches_ExcludesUnknownAndOutOfRangeDates()
    {
        var index = new FileIndex("/kok");
        var inside = index.AddEntry(FileIndex.RootId, "eski.txt", false, 1, Utc(2024, 5, 17, 12), false);
        var before = index.AddEntry(FileIndex.RootId, "dahaeski.txt", false, 1, Utc(2024, 5, 16, 12), false);
        var after = index.AddEntry(FileIndex.RootId, "yeni.txt", false, 1, Utc(2024, 5, 18, 12), false);
        var unknown = index.AddEntry(FileIndex.RootId, "bilinmiyor.txt", false, 1, 0, false);
        var folder = index.AddEntry(FileIndex.RootId, "klasor", true, 0, Utc(2024, 5, 17, 8), false);

        var q = SearchQuery.Parse("dm:17.05.2024", true, Now);
        Assert.True(SearchEngine.Matches(index, inside, q));
        Assert.True(SearchEngine.Matches(index, folder, q));
        Assert.False(SearchEngine.Matches(index, before, q));
        Assert.False(SearchEngine.Matches(index, after, q));
        Assert.False(SearchEngine.Matches(index, unknown, q));

        // Tarih filtresi yoksa bilinmeyen zaman sorun değildir.
        Assert.True(SearchEngine.Matches(index, unknown, SearchQuery.Parse("bilinmiyor", true, Now)));
    }

    [Fact]
    public void EndToEnd_OnRealFiles()
    {
        using var tree = new TempTree();
        File.SetLastWriteTime(tree.File("Belgeler/eski-rapor.pdf", 10), new DateTime(2023, 6, 15, 10, 0, 0, DateTimeKind.Local));
        File.SetLastWriteTime(tree.File("Belgeler/yeni-rapor.pdf", 10), new DateTime(2024, 5, 17, 23, 59, 0, DateTimeKind.Local));
        File.SetLastWriteTime(tree.File("Belgeler/mart.txt", 10), new DateTime(2024, 3, 31, 12, 0, 0, DateTimeKind.Local));
        tree.File("bugun.txt", 10); // Az önce yazıldı.
        var index = FileScanner.Scan(tree.Root);

        string[] Find(string query) =>
            SearchEngine.Search(new[] { index }, SearchQuery.Parse(query)).Hits
                .Where(h => !h.Index.IsDirectory(h.Id))
                .Select(h => h.Index.GetName(h.Id))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(new[] { "yeni-rapor.pdf" }, Find("dm:17.05.2024"));
        Assert.Equal(new[] { "eski-rapor.pdf" }, Find("rapor dm:2023"));
        Assert.Equal(new[] { "mart.txt", "yeni-rapor.pdf" }, Find("dm:2024"));
        Assert.Equal(new[] { "mart.txt" }, Find("dm:2024-01-01..2024-03-31"));
        Assert.Equal(new[] { "eski-rapor.pdf", "mart.txt" }, Find("dm:<2024-05"));
        Assert.Equal(new[] { "bugun.txt" }, Find("dm:son1g"));
        Assert.Equal(new[] { "bugun.txt" }, Find("file: dm:>2024"));
    }
}
