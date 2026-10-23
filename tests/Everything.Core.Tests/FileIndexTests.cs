namespace Everything.Core.Tests;

public class FileIndexTests
{
    private static TempTree Sample()
    {
        var t = new TempTree();
        t.File("Belgeler/rapor-2024.pdf", 1000);
        t.File("Belgeler/rapor-2023.pdf", 500);
        t.File("Belgeler/notlar.txt", 20);
        t.File("Videolar/tatil.mp4", 50_000);
        t.File("Videolar/Arşiv/eski tatil.mkv", 30_000);
        t.File("kok.bin", 7);
        t.Dir("Boş");
        return t;
    }

    [Fact]
    public void Scan_IndexesAllEntries_AndComputesFolderSizes()
    {
        using var t = Sample();
        var index = FileScanner.Scan(t.Root);

        // 6 dosya + 4 klasör (Belgeler, Videolar, Arşiv, Boş)
        Assert.Equal(10, index.Count);
        Assert.Equal(81_527, index.GetSize(FileIndex.RootId));
        Assert.Equal(6, index.GetFileCount(FileIndex.RootId));

        var videos = index.FindPath(t.PathOf("Videolar"));
        Assert.True(index.IsDirectory(videos));
        Assert.Equal(80_000, index.GetSize(videos));
        Assert.Equal(2, index.GetFileCount(videos));

        var mkv = index.FindPath(t.PathOf("Videolar/Arşiv/eski tatil.mkv"));
        Assert.NotEqual(FileIndex.None, mkv);
        Assert.Equal(t.PathOf("Videolar/Arşiv/eski tatil.mkv"), index.GetFullPath(mkv));
        Assert.Equal(t.PathOf("Videolar/Arşiv"), index.GetDirectoryPath(mkv));
    }

    [Fact]
    public void FindPath_ReturnsNoneForUnknownOrOutsidePaths()
    {
        using var t = Sample();
        var index = FileScanner.Scan(t.Root);
        Assert.Equal(FileIndex.None, index.FindPath(t.PathOf("yok.txt")));
        Assert.Equal(FileIndex.None, index.FindPath(t.Root + "Baska/x"));
        Assert.Equal(FileIndex.RootId, index.FindPath(t.Root));
    }

    [Fact]
    public void Remove_And_Move_KeepAggregatesConsistent()
    {
        using var t = Sample();
        var index = FileScanner.Scan(t.Root);
        var docs = index.FindPath(t.PathOf("Belgeler"));
        var videos = index.FindPath(t.PathOf("Videolar"));
        var archive = index.FindPath(t.PathOf("Videolar/Arşiv"));

        index.Move(archive, docs, "Taşındı");
        Assert.Equal(1520 + 30_000, index.GetSize(docs));
        Assert.Equal(50_000, index.GetSize(videos));
        Assert.Equal(81_527, index.GetSize(FileIndex.RootId));
        Assert.Equal(t.PathOf("Belgeler/Taşındı/eski tatil.mkv"), index.GetFullPath(index.FindPath(t.PathOf("Belgeler/Taşındı/eski tatil.mkv"))));

        index.Remove(docs);
        Assert.Equal(50_007, index.GetSize(FileIndex.RootId));
        Assert.Equal(2, index.GetFileCount(FileIndex.RootId));
        Assert.Equal(FileIndex.None, index.FindPath(t.PathOf("Belgeler/notlar.txt")));
        Assert.Equal(4, index.Count); // Videolar, tatil.mp4, kok.bin, Boş
    }

    [Fact]
    public void Serializer_RoundTripsIndex()
    {
        using var t = Sample();
        var index = FileScanner.Scan(t.Root);
        index.Move(index.FindPath(t.PathOf("Videolar/Arşiv")), index.FindPath(t.PathOf("Belgeler")), "Arşiv");
        index.Remove(index.FindPath(t.PathOf("kok.bin")));
        index.SetFileReference(index.FindPath(t.PathOf("Videolar/tatil.mp4")), 0x0001_0000_0000_002A);
        index.UsnJournalId = 77;
        index.NextUsn = 12345;

        var file = Path.Combine(t.Root, "..", Path.GetFileName(t.Root) + ".evix");
        try
        {
            IndexSerializer.Save(index, file);
            var loaded = IndexSerializer.TryLoad(file);
            Assert.NotNull(loaded);
            Assert.Equal(index.Count, loaded!.Count);
            Assert.Equal(index.GetSize(FileIndex.RootId), loaded.GetSize(FileIndex.RootId));
            var mkv = loaded.FindPath(t.PathOf("Belgeler/Arşiv/eski tatil.mkv"));
            Assert.NotEqual(FileIndex.None, mkv);
            Assert.Equal(30_000, loaded.GetSize(mkv));
            Assert.Equal(FileIndex.None, loaded.FindPath(t.PathOf("kok.bin")));
            Assert.Equal(0x0001_0000_0000_002A, loaded.GetFileReference(loaded.FindPath(t.PathOf("Videolar/tatil.mp4"))));
            Assert.Equal(77UL, loaded.UsnJournalId);
            Assert.Equal(12345, loaded.NextUsn);
            Assert.Equal(loaded.FindPath(t.PathOf("Videolar/tatil.mp4")), loaded.BuildFileReferenceMap()[0x0001_0000_0000_002A]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Serializer_ReturnsNullForCorruptFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            Assert.Null(IndexSerializer.TryLoad(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(-5)]
    [InlineData(0)]
    public void Serializer_ReturnsNullForCorruptCount(int count)
    {
        var file = Path.GetTempFileName();
        try
        {
            using (var writer = new BinaryWriter(File.Create(file)))
            {
                writer.Write(0x58495645);
                writer.Write(2);
                writer.Write(@"C:\");
                writer.Write(0L);
                writer.Write(0UL);
                writer.Write(0L);
                writer.Write(0L);
                writer.Write(count);
                writer.Write(new byte[64]);
            }
            Assert.Null(IndexSerializer.TryLoad(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SpaceAnalyzer_ReportsChildrenLargestFilesAndTypes()
    {
        using var t = Sample();
        var index = FileScanner.Scan(t.Root);

        var children = SpaceAnalyzer.GetChildren(index, FileIndex.RootId);
        Assert.Equal(new[] { "Videolar", "Belgeler", "kok.bin", "Boş" }, children.Select(c => c.Name));

        var largest = SpaceAnalyzer.GetLargestFiles(index, FileIndex.RootId, 3);
        Assert.Equal(new[] { "tatil.mp4", "eski tatil.mkv", "rapor-2024.pdf" }, largest.Select(index.GetName));

        var types = SpaceAnalyzer.GetUsageByExtension(index, FileIndex.RootId);
        Assert.Equal(".mp4", types[0].Key);
        Assert.Equal("Video", types[0].Category);
        var pdf = types.Single(x => x.Key == ".pdf");
        Assert.Equal(1500, pdf.Size);
        Assert.Equal(2, pdf.FileCount);
        Assert.Contains(types, x => x.Key == FileCategories.NoExtension || x.Key == ".bin");

        var categories = SpaceAnalyzer.GroupByCategory(types);
        Assert.Equal("Video", categories[0].Key);
        Assert.Equal(80_000, categories[0].Size);
    }
}
