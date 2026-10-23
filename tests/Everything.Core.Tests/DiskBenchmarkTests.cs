namespace Everything.Core.Tests;

public class DiskBenchmarkTests
{
    [Fact]
    public async Task SmokeTest_ReturnsAllResults_AndDeletesTempFile()
    {
        using var tree = new TempTree();
        var reports = new List<BenchmarkProgress>();
        var progress = new SyncProgress(reports.Add);

        var results = await DiskBenchmark.RunAsync(tree.Root, 16L << 20, TimeSpan.FromMilliseconds(200), progress);

        Assert.Equal(new[] { "SEQ1M Q8T1", "SEQ1M Q1T1", "RND4K Q32T1", "RND4K Q1T1" }, results.Select(r => r.Name));
        Assert.All(results, r =>
        {
            Assert.True(r.ReadMBps > 0);
            Assert.True(r.WriteMBps > 0);
            Assert.True(r.ReadIops > 0);
            Assert.True(r.WriteIops > 0);
        });
        Assert.Empty(Directory.GetFileSystemEntries(tree.Root));
        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.All(reports, p => Assert.InRange(p.Fraction, 0, 1));
            Assert.Equal(1, reports[^1].Fraction);
        }
    }

    [Fact]
    public async Task Cancellation_DeletesTempFile()
    {
        using var tree = new TempTree();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Stage.Contains("okuma"))
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiskBenchmark.RunAsync(tree.Root, 16L << 20, TimeSpan.FromSeconds(30), progress, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(tree.Root));
    }

    [Fact]
    public async Task NotEnoughFreeSpace_Throws()
    {
        using var tree = new TempTree();
        var ex = await Assert.ThrowsAsync<IOException>(() => DiskBenchmark.RunAsync(tree.Root, 1L << 55));
        Assert.Contains("boş alan", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(tree.Root));
    }

    /// <summary><see cref="Progress{T}"/> SynchronizationContext'e gönderir; testte anında çağrılsın.</summary>
    private sealed class SyncProgress(Action<BenchmarkProgress> report) : IProgress<BenchmarkProgress>
    {
        public void Report(BenchmarkProgress value)
        {
            lock (this)
                report(value);
        }
    }
}
