namespace Everything.Core.Tests;

public class DuplicateFinderTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string Write(string relativePath, byte[] content)
    {
        var full = _tree.PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private List<DuplicateGroup> Find(DuplicateOptions? options = null, CancellationToken cancellationToken = default)
    {
        var index = FileScanner.Scan(_tree.Root);
        return DuplicateFinder.Find(new[] { (index, FileIndex.RootId) }, options ?? new DuplicateOptions { MinSize = 1 }, null, cancellationToken);
    }

    [Fact]
    public void IdenticalFiles_InDifferentFolders_AreGrouped()
    {
        var content = Random(10_000, 1);
        Write("A/kopya.bin", content);
        Write("B/C/kopya2.bin", content);
        Write("B/tek.bin", Random(10_000, 2)); // aynı boyut, farklı içerik

        var groups = Find();

        var group = Assert.Single(groups);
        Assert.Equal(10_000, group.Size);
        Assert.Equal(10_000, group.WastedBytes);
        Assert.Equal(new[] { _tree.PathOf("A/kopya.bin"), _tree.PathOf("B/C/kopya2.bin") }, group.Files.Select(f => f.Path));
        Assert.All(group.Files, f => Assert.Equal(f.Path, f.Index.GetFullPath(f.Id)));
    }

    [Fact]
    public void SameSize_DifferentContent_IsNotGrouped()
    {
        Write("a.bin", Random(5000, 1));
        Write("b.bin", Random(5000, 2));
        Assert.Empty(Find());
    }

    [Fact]
    public void LargeFiles_ComparedByFullContent()
    {
        const int size = 300 * 1024;
        var same = Random(size, 7);
        Write("x/bir.bin", same);
        Write("y/iki.bin", same);
        Write("z/uc.bin", same);

        // Baştaki ve sondaki 64 KB aynı, ortası farklı.
        var different = (byte[])same.Clone();
        different[size / 2] ^= 0xFF;
        Write("w/farkli.bin", different);

        var group = Assert.Single(Find());
        Assert.Equal(3, group.Files.Count);
        Assert.Equal(2L * size, group.WastedBytes);
        Assert.DoesNotContain(group.Files, f => f.Path.EndsWith("farkli.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void SameHeadAndTail_DifferentMiddle_IsNotGrouped()
    {
        const int size = 200 * 1024;
        var a = Random(size, 3);
        var b = (byte[])a.Clone();
        b[100 * 1024] ^= 0x01;
        Write("a.bin", a);
        Write("b.bin", b);
        Assert.Empty(Find());
    }

    [Fact]
    public void Groups_SortedByWastedBytes()
    {
        var small = Random(2000, 1);
        var big = Random(9000, 2);
        Write("s1.bin", small);
        Write("s2.bin", small);
        Write("s3.bin", small);
        Write("b1.bin", big);
        Write("b2.bin", big);

        var groups = Find();
        Assert.Equal(new long[] { 9000, 4000 }, groups.Select(g => g.WastedBytes));
    }

    [Fact]
    public void MinSize_FiltersSmallFiles()
    {
        var small = Random(1000, 1);
        var big = Random(4000, 2);
        Write("a.bin", small);
        Write("b.bin", small);
        Write("c.bin", big);
        Write("d.bin", big);

        var group = Assert.Single(Find(new DuplicateOptions { MinSize = 2000 }));
        Assert.Equal(4000, group.Size);
    }

    [Fact]
    public void ExcludedPaths_AreSkipped()
    {
        var content = Random(3000, 1);
        Write("Tut/a.bin", content);
        Write("Haric/b.bin", content);
        Write("HaricDegil/c.bin", content);

        var excluded = OperatingSystem.IsWindows() ? _tree.PathOf("HARIC") : _tree.PathOf("Haric");
        var group = Assert.Single(Find(new DuplicateOptions { MinSize = 1, ExcludedPaths = new[] { excluded + Path.DirectorySeparatorChar } }));
        Assert.Equal(new[] { _tree.PathOf("HaricDegil/c.bin"), _tree.PathOf("Tut/a.bin") }, group.Files.Select(f => f.Path));
    }

    [Fact]
    public void HardLinks_AreNotReportedAsDuplicates()
    {
        var content = Random(3000, 1);
        var a = Write("a.bin", content);
        var b = Write("b.bin", content);

        // a ve b aynı fiziksel dosya sayılır.
        string? Identity(string path) => path == a || path == b ? "disk1:42" : path;
        Assert.Empty(Find(new DuplicateOptions { MinSize = 1, FileIdentity = Identity }));

        // Gerçek bir kopya eklenince, sabit bağlantılardan yalnızca biri listelenir.
        var c = Write("c.bin", content);
        var group = Assert.Single(Find(new DuplicateOptions { MinSize = 1, FileIdentity = Identity }));
        Assert.Equal(new[] { a, c }, group.Files.Select(f => f.Path));
    }

    [Fact]
    public void OverlappingScopes_DoNotDuplicateFiles()
    {
        var content = Random(3000, 1);
        Write("A/x.bin", content);
        Write("A/y.bin", content);
        var index = FileScanner.Scan(_tree.Root);
        var sub = index.FindPath(_tree.PathOf("A"));

        var groups = DuplicateFinder.Find(new[] { (index, FileIndex.RootId), (index, sub) }, new DuplicateOptions { MinSize = 1 });
        Assert.Equal(2, Assert.Single(groups).Files.Count);
    }

    [Fact]
    public void Cancellation_Throws()
    {
        var content = Random(3000, 1);
        Write("a.bin", content);
        Write("b.bin", content);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Find(cancellationToken: cts.Token));
    }

    [Fact]
    public void Progress_IsReported()
    {
        var content = Random(300 * 1024, 1);
        Write("a.bin", content);
        Write("b.bin", content);
        var index = FileScanner.Scan(_tree.Root);
        var stages = new List<DuplicateProgress>();
        var progress = new SyncProgress(stages.Add);

        DuplicateFinder.Find(new[] { (index, FileIndex.RootId) }, new DuplicateOptions { MinSize = 1 }, progress);

        Assert.Contains(stages, p => p.Stage == DuplicateFinder.StagePartial && p.Done == 2 && p.Total == 2);
        Assert.Contains(stages, p => p.Stage == DuplicateFinder.StageFull && p.Done == 2 * content.Length && p.Total == 2 * content.Length);
    }

    private sealed class SyncProgress(Action<DuplicateProgress> handler) : IProgress<DuplicateProgress>
    {
        public void Report(DuplicateProgress value)
        {
            lock (this)
                handler(value);
        }
    }
}
