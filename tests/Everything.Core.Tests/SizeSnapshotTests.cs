namespace Everything.Core.Tests;

public class SizeSnapshotTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly string _store;

    public SizeSnapshotTests() => _store = _tree.Dir("_snapshots");

    public void Dispose() => _tree.Dispose();

    private static string P(params string[] parts) => string.Join(Path.DirectorySeparatorChar, parts);

    private static SizeSnapshot Snap(string root, DateTime taken, params (string Path, long Size)[] sizes) => new()
    {
        RootPath = root,
        TakenUtc = taken,
        FolderSizes = sizes.ToDictionary(s => s.Path, s => s.Size, SizeSnapshot.KeyComparer),
    };

    [Fact]
    public void Capture_RecordsLargeFolders_AndTopLevel()
    {
        using var t = new TempTree();
        t.File("Buyuk/Alt/Derin/x.bin", 5000);
        t.File("Buyuk/kucukalt/y.bin", 10);
        t.File("Kucuk/Alt/z.bin", 10);
        var index = FileScanner.Scan(t.Root);

        var snap = SnapshotStore.Capture(index, minFolderSize: 1000, maxDepth: 2);

        Assert.Equal(t.Root, snap.RootPath);
        Assert.Equal(5020, snap.FolderSizes[""]);
        Assert.Equal(5010, snap.FolderSizes["Buyuk"]);
        Assert.Equal(5000, snap.FolderSizes[P("Buyuk", "Alt")]);
        Assert.Equal(10, snap.FolderSizes["Kucuk"]); // derinlik 1 her zaman kaydedilir
        Assert.False(snap.FolderSizes.ContainsKey(P("Buyuk", "kucukalt")));
        Assert.False(snap.FolderSizes.ContainsKey(P("Kucuk", "Alt")));
        Assert.False(snap.FolderSizes.ContainsKey(P("Buyuk", "Alt", "Derin"))); // maxDepth
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        using var t = new TempTree();
        t.File("A/B/x.bin", 3000);
        var snap = SnapshotStore.Capture(FileScanner.Scan(t.Root), minFolderSize: 1);

        SnapshotStore.Save(snap, _store);
        var (taken, file) = Assert.Single(SnapshotStore.List(_store, t.Root));
        Assert.Contains(snap.TakenUtc.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(file));
        Assert.Empty(Directory.GetFiles(_store, "*.tmp"));
        Assert.Equal(new DateTime(snap.TakenUtc.Ticks - snap.TakenUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc), taken);

        var loaded = SnapshotStore.TryLoad(file);
        Assert.NotNull(loaded);
        Assert.Equal(snap.RootPath, loaded.RootPath);
        Assert.Equal(snap.TakenUtc, loaded.TakenUtc);
        Assert.Equal(snap.FolderSizes.OrderBy(kv => kv.Key), loaded.FolderSizes.OrderBy(kv => kv.Key));
        Assert.Equal(3000, loaded.FolderSizes[P("A", "B")]);
    }

    [Fact]
    public void TryLoad_ReturnsNullForCorruptFiles()
    {
        var bad = Path.Combine(_store, "bozuk.evsnap");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5 });
        Assert.Null(SnapshotStore.TryLoad(bad));
        Assert.Null(SnapshotStore.TryLoad(Path.Combine(_store, "yok.evsnap")));

        // Geçerli ama kesilmiş dosya.
        SnapshotStore.Save(Snap("/kok", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ("", 10), ("a", 5)), _store);
        var file = SnapshotStore.List(_store, "/kok")[0].File;
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..(bytes.Length / 2)]);
        Assert.Null(SnapshotStore.TryLoad(file));
    }

    [Fact]
    public void List_FindClosest_Prune()
    {
        const string root = "/disk/veri";
        var day = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++)
        {
            SnapshotStore.Save(Snap(root, day.AddDays(i), ("", i)), _store);
            SnapshotStore.Save(Snap(root, day.AddDays(i).AddHours(-3), ("", 100 + i)), _store); // aynı gün daha eski
        }
        SnapshotStore.Save(Snap("/baska/kok", day, ("", 1)), _store);

        var list = SnapshotStore.List(_store, root);
        Assert.Equal(20, list.Count);
        Assert.Equal(day.AddDays(9), list[0].TakenUtc);
        Assert.True(list.Zip(list.Skip(1)).All(p => p.First.TakenUtc > p.Second.TakenUtc));

        // Hedeften önceki en yeni.
        var closest = SnapshotStore.FindClosest(_store, root, day.AddDays(3).AddHours(1));
        Assert.Equal(day.AddDays(3), closest!.TakenUtc);
        // Öncesinde yoksa sonraki en eski.
        closest = SnapshotStore.FindClosest(_store, root, day.AddDays(-5));
        Assert.Equal(day.AddHours(-3), closest!.TakenUtc);
        Assert.Null(SnapshotStore.FindClosest(_store, "/hic/yok", day));

        SnapshotStore.Prune(_store, root, keepDaily: 4);
        Assert.Equal(new[] { day.AddDays(9), day.AddDays(8), day.AddDays(7), day.AddDays(6) },
            SnapshotStore.List(_store, root).Select(s => s.TakenUtc));
        Assert.Single(SnapshotStore.List(_store, "/baska/kok"));
    }

    [Fact]
    public void Growth_ParentOmitted_WhenSingleChildExplainsIt()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = Snap("/k", t0, ("", 1000), ("Users", 600), (P("Users", "ali"), 500), (P("Users", "ali", "Downloads"), 100), ("Windows", 400));
        var cur = Snap("/k", t0.AddDays(7),
            ("", 1600), ("Users", 1190), (P("Users", "ali"), 1090), (P("Users", "ali", "Downloads"), 690), ("Windows", 410));

        var growth = GrowthAnalyzer.Compare(old, cur);

        // Kök, Users ve ali büyümeyi Downloads'a borçlu; yalnızca asıl sorumlu ve Windows kalır.
        Assert.Equal(new[] { P("Users", "ali", "Downloads"), "Windows" }, growth.Select(g => g.RelativePath));
        Assert.Equal(590, growth[0].Delta);
    }

    [Fact]
    public void Growth_ParentKept_WhenSpreadAcrossChildren()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = Snap("/k", t0, ("", 100), ("Proje", 100), (P("Proje", "a"), 50), (P("Proje", "b"), 50));
        var cur = Snap("/k", t0.AddDays(1), ("", 300), ("Proje", 300), (P("Proje", "a"), 150), (P("Proje", "b"), 150));

        var growth = GrowthAnalyzer.Compare(old, cur);

        // Kök tamamen Proje ile açıklanır; Proje'nin büyümesi iki çocuğa yayılmıştır.
        Assert.Equal(new[] { "Proje", P("Proje", "a"), P("Proje", "b") }, growth.Select(g => g.RelativePath));
        Assert.Equal(200, growth[0].Delta);
    }

    [Fact]
    public void Growth_NewRemoved_AndShrinkage()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = Snap("/k", t0, ("", 1000), ("Eski", 700), ("Sabit", 300));
        var cur = Snap("/k", t0.AddDays(1), ("", 900), ("Yeni", 600), ("Sabit", 300));

        var growth = Assert.Single(GrowthAnalyzer.Compare(old, cur));
        Assert.Equal(new GrowthEntry("Yeni", 0, 600), growth);

        var shrink = GrowthAnalyzer.Compare(old, cur, growth: false);
        // Kökün -100'ü, Eski'nin -700'ü ile aynı yönde ve ≥ %80 açıklanır.
        Assert.Equal(new[] { new GrowthEntry("Eski", 700, 0) }, shrink);
        Assert.Equal(-700, shrink[0].Delta);

        Assert.Empty(GrowthAnalyzer.Compare(old, cur, top: 0));
    }
}
