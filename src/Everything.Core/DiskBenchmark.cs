using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Everything.Core;

/// <summary>Tek bir testin sonucu; MB/s ondalık megabayttır (1.000.000 bayt), CrystalDiskMark gibi.</summary>
public sealed record BenchmarkResult(string Name, double ReadMBps, double WriteMBps, double ReadIops, double WriteIops);

/// <param name="Stage">Kullanıcıya gösterilecek aşama metni.</param>
/// <param name="Fraction">Tüm ölçümün 0..1 arası ilerlemesi.</param>
public readonly record struct BenchmarkProgress(string Stage, double Fraction);

/// <summary>
/// CrystalDiskMark benzeri disk hız testi: SEQ1M Q8T1, SEQ1M Q1T1, RND4K Q32T1, RND4K Q1T1.
/// Klasörde geçici bir test dosyası oluşturulur, sıralı olarak sıkıştırılamaz rastgele veriyle doldurulur,
/// ardından her test için okuma ve yazma ayrı ayrı ölçülür. Qn, aynı anda bekleyen n eşzamansız G/Ç demektir.
/// Windows'ta önbellek atlanır (FILE_FLAG_NO_BUFFERING + yazarak geçiş); diğer sistemlerde okuma
/// sayfa önbelleğinden gelebilir, bu yüzden sonuçlar yalnızca yaklaşık değerdir.
/// </summary>
public static class DiskBenchmark
{
    private const int Alignment = 4096;
    private const int SeqBlock = 1 << 20;
    private const int RndBlock = 4096;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private readonly record struct TestSpec(string Name, int BlockSize, int QueueDepth, bool Sequential);

    private static readonly TestSpec[] Tests =
    {
        new("SEQ1M Q8T1", SeqBlock, 8, true),
        new("SEQ1M Q1T1", SeqBlock, 1, true),
        new("RND4K Q32T1", RndBlock, 32, false),
        new("RND4K Q1T1", RndBlock, 1, false),
    };

    private readonly record struct Measurement(double MBps, double Iops);

    public static async Task<List<BenchmarkResult>> RunAsync(
        string directory,
        long testFileSize = 1L << 30,
        TimeSpan? durationPerTest = null,
        IProgress<BenchmarkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Klasör bulunamadı: {directory}");

        // Sıralı testler 1 MB bloklarla çalışır; dosya boyutu bunun katı olmalı.
        testFileSize = testFileSize / SeqBlock * SeqBlock;
        if (testFileSize < SeqBlock)
            throw new ArgumentOutOfRangeException(nameof(testFileSize), "Test dosyası en az 1 MB olmalı.");
        var duration = durationPerTest ?? TimeSpan.FromSeconds(5);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(durationPerTest));

        EnsureFreeSpace(directory, testFileSize);

        var path = Path.Combine(directory, $".everything-bench-{Guid.NewGuid():N}.tmp");
        // Hazırlık + her test için okuma ve yazma.
        var totalStages = 1 + Tests.Length * 2;
        SafeFileHandle? handle = null;
        try
        {
            var options = FileOptions.Asynchronous | FileOptions.WriteThrough;
            if (OperatingSystem.IsWindows())
                options |= NoBuffering;
            handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, options, testFileSize);

            await PrepareAsync(handle, testFileSize, totalStages, progress, cancellationToken).ConfigureAwait(false);

            var results = new List<BenchmarkResult>(Tests.Length);
            var stage = 1;
            foreach (var test in Tests)
            {
                var read = await MeasureAsync(handle, testFileSize, test, write: false, duration, $"{test.Name} okuma", stage++, totalStages, progress, cancellationToken).ConfigureAwait(false);
                var write = await MeasureAsync(handle, testFileSize, test, write: true, duration, $"{test.Name} yazma", stage++, totalStages, progress, cancellationToken).ConfigureAwait(false);
                results.Add(new BenchmarkResult(test.Name, read.MBps, write.MBps, read.Iops, write.Iops));
            }

            progress?.Report(new BenchmarkProgress("Tamamlandı", 1));
            return results;
        }
        finally
        {
            handle?.Dispose();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Klasörün bulunduğu birimde test dosyası için yer yoksa <see cref="IOException"/> atar.</summary>
    private static void EnsureFreeSpace(string directory, long testFileSize)
    {
        DriveInfo? drive = null;
        try
        {
            // Klasörü içeren en uzun bağlama noktası (Linux'ta /tmp ayrı bir birim olabilir).
            var comparison = FileIndex.NameComparison;
            drive = DriveInfo.GetDrives()
                .Where(d => IsUnder(directory, d.RootDirectory.FullName, comparison))
                .MaxBy(d => d.RootDirectory.FullName.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        long free;
        try
        {
            if (drive is null || !drive.IsReady)
                return;
            free = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Dosya sistemi meta verisi için küçük bir pay bırak.
        var needed = testFileSize + 16L * 1024 * 1024;
        if (free < needed)
        {
            throw new IOException(
                $"Disk testi için yeterli boş alan yok ({directory}): {ByteSize.Format(needed)} gerekli, {ByteSize.Format(free)} boş. " +
                "Daha küçük bir test dosyası seçin veya yer açın.");
        }
    }

    private static bool IsUnder(string path, string root, StringComparison comparison)
    {
        if (!path.StartsWith(root, comparison))
            return false;
        return path.Length == root.Length
            || Path.EndsInDirectorySeparator(root)
            || path[root.Length] == Path.DirectorySeparatorChar
            || path[root.Length] == Path.AltDirectorySeparatorChar;
    }

    /// <summary>Dosyayı 1 MB'lık bloklarla, Q8 sıralı yazarak sıkıştırılamaz veriyle doldurur.</summary>
    private static async Task PrepareAsync(SafeFileHandle handle, long fileSize, int totalStages, IProgress<BenchmarkProgress>? progress, CancellationToken cancellationToken)
    {
        const string stage = "Test dosyası hazırlanıyor";
        progress?.Report(new BenchmarkProgress(stage, 0));
        var blocks = fileSize / SeqBlock;
        long next = -1, done = 0;

        async Task Worker()
        {
            var buffer = AlignedBuffer(SeqBlock);
            Random.Shared.NextBytes(buffer.Span);
            long block;
            while ((block = Interlocked.Increment(ref next)) < blocks)
            {
                await RandomAccess.WriteAsync(handle, buffer, block * SeqBlock, cancellationToken).ConfigureAwait(false);
                var completed = Interlocked.Increment(ref done);
                if (progress is not null && completed % 16 == 0)
                    progress.Report(new BenchmarkProgress(stage, (double)completed / blocks / totalStages));
            }
        }

        var workers = new Task[(int)Math.Min(8, blocks)];
        for (var i = 0; i < workers.Length; i++)
            workers[i] = Task.Run(Worker, cancellationToken);
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private static async Task<Measurement> MeasureAsync(
        SafeFileHandle handle, long fileSize, TestSpec test, bool write, TimeSpan duration,
        string stage, int stageIndex, int totalStages, IProgress<BenchmarkProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new BenchmarkProgress(stage, (double)stageIndex / totalStages));

        var blocks = fileSize / test.BlockSize;
        long nextSeq = -1, ops = 0;
        var sw = Stopwatch.StartNew();

        async Task Worker(int seed)
        {
            var buffer = AlignedBuffer(test.BlockSize);
            if (write)
                Random.Shared.NextBytes(buffer.Span);
            var random = new Random(seed);
            while (sw.Elapsed < duration)
            {
                // Sıralı testte bloklar sırayla paylaşılır; rastgele testte 4 KB hizalı rastgele konum seçilir.
                var block = test.Sequential ? Interlocked.Increment(ref nextSeq) % blocks : random.NextInt64(blocks);
                var offset = block * test.BlockSize;
                if (write)
                {
                    await RandomAccess.WriteAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var read = await RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false);
                    if (read != test.BlockSize)
                        throw new IOException($"Test dosyasından beklenenden az veri okundu ({read} / {test.BlockSize} bayt).");
                }
                Interlocked.Increment(ref ops);
            }
        }

        var workers = new Task[test.QueueDepth];
        for (var i = 0; i < workers.Length; i++)
        {
            var seed = Random.Shared.Next();
            workers[i] = Task.Run(() => Worker(seed), cancellationToken);
        }

        var all = Task.WhenAll(workers);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
            var within = Math.Min(1, sw.Elapsed / duration);
            progress?.Report(new BenchmarkProgress(stage, (stageIndex + within) / totalStages));
        }
        await all.ConfigureAwait(false);

        var seconds = sw.Elapsed.TotalSeconds;
        var count = Interlocked.Read(ref ops);
        return new Measurement(count * (double)test.BlockSize / 1_000_000 / seconds, count / seconds);
    }

    /// <summary>
    /// Sayfa (ve dolayısıyla sektör) hizalı tampon: NO_BUFFERING G/Ç'nin şartıdır. Sabitlenmiş yığındaki
    /// dizi hiç taşınmadığından adresi hizalanarak dilimlenebilir; güvensiz kod gerekmez.
    /// </summary>
    private static Memory<byte> AlignedBuffer(int size)
    {
        var array = GC.AllocateUninitializedArray<byte>(size + Alignment, pinned: true);
        var address = (long)Marshal.UnsafeAddrOfPinnedArrayElement(array, 0);
        var offset = (int)((Alignment - address % Alignment) % Alignment);
        return array.AsMemory(offset, size);
    }
}
