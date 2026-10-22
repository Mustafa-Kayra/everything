using Everything.Core.Ntfs;

namespace Everything.Core.Tests;

public class VolumeIndexerTests
{
    private static async Task WaitUntil(Func<bool> condition, VolumeIndexer volume)
    {
        for (var i = 0; i < 100; i++)
        {
            await volume.FlushAsync();
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail("Beklenen indeks durumu oluşmadı.");
    }

    [Fact]
    public async Task LiveChanges_AreAppliedToIndex()
    {
        using var t = new TempTree();
        t.File("a/bir.txt", 100);
        using var volume = new VolumeIndexer(t.Root);
        await volume.StartAsync();
        Assert.Equal(IndexState.Ready, volume.State);
        var index = volume.Index!;
        Assert.Equal(100, index.GetSize(FileIndex.RootId));

        // Yeni dosya
        t.File("a/iki.txt", 50);
        await WaitUntil(() => index.FindPath(t.PathOf("a/iki.txt")) != FileIndex.None, volume);
        await WaitUntil(() => index.GetSize(FileIndex.RootId) == 150, volume);

        // İçeriğiyle birlikte dışarıdan taşınan klasör
        var outside = Path.Combine(Path.GetTempPath(), "everything-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outside, "alt"));
        File.WriteAllBytes(Path.Combine(outside, "alt", "buyuk.bin"), new byte[1000]);
        Directory.Move(outside, t.PathOf("tasinan"));
        await WaitUntil(() => index.FindPath(t.PathOf("tasinan/alt/buyuk.bin")) != FileIndex.None, volume);
        await WaitUntil(() => index.GetSize(FileIndex.RootId) == 1150, volume);

        // Klasörü yeniden adlandırma
        Directory.Move(t.PathOf("tasinan"), t.PathOf("yeni-ad"));
        await WaitUntil(() => index.FindPath(t.PathOf("yeni-ad/alt/buyuk.bin")) != FileIndex.None, volume);
        Assert.Equal(FileIndex.None, index.FindPath(t.PathOf("tasinan")));
        Assert.Equal(1150, index.GetSize(FileIndex.RootId));

        // Boyut değişimi
        File.WriteAllBytes(t.PathOf("a/bir.txt"), new byte[400]);
        await WaitUntil(() => index.GetSize(FileIndex.RootId) == 1450, volume);

        // Silme
        Directory.Delete(t.PathOf("yeni-ad"), recursive: true);
        await WaitUntil(() => index.FindPath(t.PathOf("yeni-ad")) == FileIndex.None, volume);
        await WaitUntil(() => index.GetSize(FileIndex.RootId) == 450, volume);
        Assert.Equal(2, index.GetFileCount(FileIndex.RootId));

        var hits = SearchEngine.Search(new[] { index }, SearchQuery.Parse("buyuk")).Hits;
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Start_UsesFreshCacheWithoutRescanning()
    {
        using var t = new TempTree();
        var cacheDir = NewCacheDir();
        try
        {
            t.File("x.txt", 10);
            using (var first = new VolumeIndexer(t.Root, cacheDir))
                await first.StartAsync();
            Assert.Single(Directory.GetFiles(cacheDir, "*.evix"));

            // Kapalıyken eklenen dosya: günlük yok ve önbellek taze → tarama yapılmaz.
            t.File("y.txt", 20);
            using var second = new VolumeIndexer(t.Root, new VolumeIndexerOptions { CacheDirectory = cacheDir, RefreshIfOlderThan = TimeSpan.FromDays(3) });
            await second.StartAsync();
            Assert.Equal(IndexState.Ready, second.State);
            Assert.Equal(10, second.Index!.GetSize(FileIndex.RootId));
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task Start_RescansWhenCacheIsTooOld()
    {
        using var t = new TempTree();
        var cacheDir = NewCacheDir();
        try
        {
            t.File("x.txt", 10);
            using (var first = new VolumeIndexer(t.Root, cacheDir))
                await first.StartAsync();

            t.File("y.txt", 20);
            using var second = new VolumeIndexer(t.Root, new VolumeIndexerOptions { CacheDirectory = cacheDir, RefreshIfOlderThan = TimeSpan.Zero });
            await second.StartAsync();
            Assert.Equal(30, second.Index!.GetSize(FileIndex.RootId));
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task Start_CatchesUpFromJournalInsteadOfRescanning()
    {
        using var t = new TempTree();
        var cacheDir = NewCacheDir();
        var journal = new FakeUsnJournal(t.Root);
        try
        {
            t.File("docs/a.txt", 10);
            var options = new VolumeIndexerOptions { CacheDirectory = cacheDir, JournalFactory = _ => journal.Share(), JournalPollInterval = TimeSpan.FromMilliseconds(50) };
            using (var first = new VolumeIndexer(t.Root, options))
            {
                await first.StartAsync();
                Assert.True(first.UsesJournal);
            }

            // Kapalıyken: yeni dosya, silinen dosya, yeniden adlandırılan klasör.
            t.File("docs/b.txt", 20);
            journal.Add(UsnReason.FileCreate, t.PathOf("docs/b.txt"));
            File.Delete(t.PathOf("docs/a.txt"));
            journal.Add(UsnReason.FileDelete, t.PathOf("docs/a.txt"));
            journal.Add(UsnReason.RenameOldName, t.PathOf("docs"), isDirectory: true);
            Directory.Move(t.PathOf("docs"), t.PathOf("belgeler"));
            journal.Add(UsnReason.RenameNewName, t.PathOf("belgeler"), isDirectory: true);

            // Günlüğe yazılmamış bir değişiklik: tam tarama yapılsaydı görünürdü.
            t.File("gizli.txt", 1000);

            using var second = new VolumeIndexer(t.Root, options);
            await second.StartAsync();
            var index = second.Index!;
            Assert.Equal(3, second.CatchUpChanges);
            Assert.NotEqual(FileIndex.None, index.FindPath(t.PathOf("belgeler/b.txt")));
            Assert.Equal(FileIndex.None, index.FindPath(t.PathOf("docs")));
            Assert.Equal(FileIndex.None, index.FindPath(t.PathOf("gizli.txt")));
            Assert.Equal(20, index.GetSize(FileIndex.RootId));

            // Canlı güncellemeler de günlükten gelir.
            t.File("belgeler/c.txt", 5);
            journal.Add(UsnReason.FileCreate | UsnReason.Close, t.PathOf("belgeler/c.txt"));
            for (var i = 0; i < 100 && index.FindPath(t.PathOf("belgeler/c.txt")) == FileIndex.None; i++)
                await Task.Delay(50);
            Assert.Equal(25, index.GetSize(FileIndex.RootId));
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task Start_RescansWhenJournalWasReset()
    {
        using var t = new TempTree();
        var cacheDir = NewCacheDir();
        var journal = new FakeUsnJournal(t.Root);
        try
        {
            t.File("a.txt", 10);
            var options = new VolumeIndexerOptions { CacheDirectory = cacheDir, JournalFactory = _ => journal.Share() };
            using (var first = new VolumeIndexer(t.Root, options))
                await first.StartAsync();

            t.File("b.txt", 20);
            journal.Reset();
            using var second = new VolumeIndexer(t.Root, options);
            await second.StartAsync();
            Assert.Equal(30, second.Index!.GetSize(FileIndex.RootId));
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static string NewCacheDir() => Path.Combine(Path.GetTempPath(), "everything-cache-" + Guid.NewGuid().ToString("N"));
}
