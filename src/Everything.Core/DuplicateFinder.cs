using System.Buffers;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace Everything.Core;

public sealed record DuplicateFile(FileIndex Index, int Id, string Path, long Size, DateTime ModifiedUtc);

public sealed record DuplicateGroup(long Size, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>Her kopyadan yalnızca biri tutulursa kazanılacak alan.</summary>
    public long WastedBytes => Size * (Files.Count - 1);
}

/// <summary>
/// İlerleme bilgisi. "Dosyaların başı okunuyor" aşamasında Done/Total dosya sayısı,
/// "Tam içerik karşılaştırılıyor" aşamasında okunan bayt sayısıdır.
/// </summary>
public readonly record struct DuplicateProgress(string Stage, long Done, long Total);

public sealed class DuplicateOptions
{
    /// <summary>Bundan küçük dosyalar hiç incelenmez.</summary>
    public long MinSize { get; init; } = 1024 * 1024;

    /// <summary>Bu yolların (ve altlarının) dosyaları atlanır; Windows'ta büyük/küçük harf duyarsız.</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Fiziksel dosya kimliği (ör. birim seri no + dosya indeksi). Aynı kimliği döndüren yollar
    /// aynı dosyanın sabit bağlantılarıdır (hard link); birbirlerinin kopyası sayılmazlar.
    /// </summary>
    public Func<string, string?>? FileIdentity { get; init; }

    public int MaxDegreeOfParallelism { get; init; } = 4;
}

/// <summary>
/// İçeriği aynı olan dosyaları bulur: önce boyuta göre gruplar, sonra dosyaların başını ve sonunu,
/// en son da tam içeriğini XxHash128 ile karşılaştırır. Çağıranın okuma kilidi almasına gerek yoktur;
/// kilit yalnızca indeks gezilirken tutulur, disk okuması sırasında bırakılır.
/// </summary>
public static class DuplicateFinder
{
    public const string StageSizes = "Boyutlar karşılaştırılıyor";
    public const string StagePartial = "Dosyaların başı okunuyor";
    public const string StageFull = "Tam içerik karşılaştırılıyor";

    private const int PartialChunk = 64 * 1024;
    private const int FullChunk = 1024 * 1024;
    private const long ReportIntervalMs = 100;

    private sealed class Candidate(DuplicateFile file)
    {
        public DuplicateFile File { get; } = file;
        public string? Identity { get; set; }
        public UInt128 Hash { get; set; }
        public bool Ok { get; set; }
    }

    public static List<DuplicateGroup> Find(
        IEnumerable<(FileIndex Index, int ScopeId)> scopes,
        DuplicateOptions options,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);

        // 1) Dosyaları topla ve boyuta göre grupla.
        var files = Collect(scopes, options, progress, cancellationToken);
        var bySize = files
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(f => new Candidate(f)).ToList())
            .ToList();
        progress?.Report(new DuplicateProgress(StageSizes, files.Count, files.Count));

        // 2) Baştan ve sondan 64 KB'lık parçaların özeti; ayrıca sabit bağlantı kimliği.
        var partialList = bySize.SelectMany(g => g).ToList();
        var reporter = new Reporter(progress, StagePartial, partialList.Count);
        RunParallel(partialList, options, cancellationToken, c =>
        {
            if (TryGetIdentity(options, c.File.Path, out var identity) && TryHashPartial(c.File, out var hash))
            {
                c.Identity = identity;
                c.Hash = hash;
                c.Ok = true;
            }
            reporter.Add(1);
        });
        reporter.Finish();

        var afterPartial = new List<List<Candidate>>();
        foreach (var group in bySize)
        {
            foreach (var sub in group.Where(c => c.Ok).GroupBy(c => c.Hash))
            {
                var distinct = CollapseHardLinks(sub);
                if (distinct.Count > 1)
                    afterPartial.Add(distinct);
            }
        }

        // 3) Tam içerik. 128 KB'tan küçük dosyalar 2. aşamada zaten tamamen okundu.
        var needFull = afterPartial.Where(g => g[0].File.Size > 2L * PartialChunk).SelectMany(g => g).ToList();
        reporter = new Reporter(progress, StageFull, needFull.Sum(c => c.File.Size));
        RunParallel(needFull, options, cancellationToken, c =>
        {
            c.Ok = TryHashFull(c.File, reporter, cancellationToken, out var hash);
            c.Hash = hash;
        });
        reporter.Finish();

        var pathComparer = PathComparer();
        var result = new List<DuplicateGroup>();
        foreach (var group in afterPartial)
        {
            foreach (var sub in group.Where(c => c.Ok).GroupBy(c => c.Hash))
            {
                var list = sub.Select(c => c.File).OrderBy(f => f.Path, pathComparer).ToList();
                if (list.Count > 1)
                    result.Add(new DuplicateGroup(list[0].Size, list));
            }
        }

        result.Sort((a, b) =>
        {
            var c = b.WastedBytes.CompareTo(a.WastedBytes);
            return c != 0 ? c : pathComparer.Compare(a.Files[0].Path, b.Files[0].Path);
        });
        return result;
    }

    private static StringComparer PathComparer() =>
        FileIndex.NameComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Kapsamlardaki uygun dosyaları okuma kilidi altında toplar; çakışan kapsamlar tekrar saymaz.</summary>
    private static List<DuplicateFile> Collect(
        IEnumerable<(FileIndex Index, int ScopeId)> scopes,
        DuplicateOptions options,
        IProgress<DuplicateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var excluded = options.ExcludedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToArray();
        var seen = new HashSet<string>(PathComparer());
        var result = new List<DuplicateFile>();
        var lastReport = Environment.TickCount64;
        var visited = 0;

        foreach (var (index, scopeId) in scopes)
        {
            index.Lock.EnterReadLock();
            try
            {
                if (scopeId < 0 || scopeId >= index.Capacity || index.IsDeleted(scopeId))
                    continue;

                var stack = new Stack<(int Id, string Path)>();
                stack.Push((scopeId, index.GetFullPath(scopeId)));
                while (stack.Count > 0)
                {
                    var (id, path) = stack.Pop();
                    if ((++visited & 4095) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var now = Environment.TickCount64;
                        if (progress is not null && now - lastReport > ReportIntervalMs)
                        {
                            lastReport = now;
                            progress.Report(new DuplicateProgress(StageSizes, result.Count, 0));
                        }
                    }

                    if (IsExcluded(path, excluded))
                        continue;

                    if (index.IsDirectory(id))
                    {
                        for (var c = index.GetFirstChild(id); c != FileIndex.None; c = index.GetNextSibling(c))
                        {
                            if (!index.IsDeleted(c))
                                stack.Push((c, Path.Join(path, index.GetName(c))));
                        }
                        continue;
                    }

                    // Sembolik bağlantılar (ve bulut yer tutucuları) okunmaz.
                    var size = index.GetSize(id);
                    if (size < options.MinSize || size <= 0 || index.IsReparsePoint(id) || !seen.Add(path))
                        continue;
                    result.Add(new DuplicateFile(index, id, path, size, index.GetModifiedUtc(id)));
                }
            }
            finally
            {
                index.Lock.ExitReadLock();
            }
        }
        return result;
    }

    private static bool IsExcluded(string path, string[] excluded)
    {
        foreach (var prefix in excluded)
        {
            if (!path.StartsWith(prefix, FileIndex.NameComparison))
                continue;
            // "C:\Foo" hariç tutulunca "C:\FooBar" etkilenmesin.
            if (path.Length == prefix.Length || prefix.Length == 0
                || path[prefix.Length] == Path.DirectorySeparatorChar || path[prefix.Length] == Path.AltDirectorySeparatorChar)
                return true;
        }
        return false;
    }

    /// <summary>Aynı fiziksel dosyaya giden yollardan yalnızca birini (yola göre ilkini) bırakır.</summary>
    private static List<Candidate> CollapseHardLinks(IEnumerable<Candidate> group)
    {
        var comparer = PathComparer();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<Candidate>();
        foreach (var c in group.OrderBy(c => c.File.Path, comparer))
        {
            if (c.Identity is null || identities.Add(c.Identity))
                list.Add(c);
        }
        return list;
    }

    private static bool TryGetIdentity(DuplicateOptions options, string path, out string? identity)
    {
        identity = null;
        if (options.FileIdentity is null)
            return true;
        try
        {
            identity = options.FileIdentity(path);
            return true;
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return false;
        }
    }

    private static void RunParallel(List<Candidate> items, DuplicateOptions options, CancellationToken cancellationToken, Action<Candidate> body)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0)
            return;
        try
        {
            Parallel.ForEach(
                items,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism) },
                body);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static bool TryHashPartial(DuplicateFile file, out UInt128 hash)
    {
        hash = default;
        var buffer = ArrayPool<byte>.Shared.Rent(2 * PartialChunk);
        try
        {
            using var handle = OpenRead(file.Path);
            var length = RandomAccess.GetLength(handle);
            if (length != file.Size)
                return false; // İndeks eskimiş; dosya değişmiş.

            // Küçük dosyalar tamamen, büyükler baştan ve sondan birer parça okunur.
            int read;
            if (length <= 2L * PartialChunk)
            {
                read = (int)length;
                if (!ReadExactly(handle, buffer.AsSpan(0, read), 0))
                    return false;
            }
            else
            {
                read = 2 * PartialChunk;
                if (!ReadExactly(handle, buffer.AsSpan(0, PartialChunk), 0)
                    || !ReadExactly(handle, buffer.AsSpan(PartialChunk, PartialChunk), length - PartialChunk))
                    return false;
            }
            hash = XxHash128.HashToUInt128(buffer.AsSpan(0, read));
            return true;
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryHashFull(DuplicateFile file, Reporter reporter, CancellationToken cancellationToken, out UInt128 hash)
    {
        hash = default;
        var buffer = ArrayPool<byte>.Shared.Rent(FullChunk);
        try
        {
            using var handle = OpenRead(file.Path);
            if (RandomAccess.GetLength(handle) != file.Size)
                return false;

            var hasher = new XxHash128();
            long offset = 0;
            while (offset < file.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = (int)Math.Min(FullChunk, file.Size - offset);
                if (!ReadExactly(handle, buffer.AsSpan(0, chunk), offset))
                    return false;
                hasher.Append(buffer.AsSpan(0, chunk));
                offset += chunk;
                reporter.Add(chunk);
            }
            hash = hasher.GetCurrentHashAsUInt128();
            return true;
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static SafeFileHandle OpenRead(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);

    /// <summary>Tamponu doldurur; dosya beklenenden kısaysa false.</summary>
    private static bool ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (buffer.Length > 0)
        {
            var n = RandomAccess.Read(handle, buffer, offset);
            if (n <= 0)
                return false;
            buffer = buffer[n..];
            offset += n;
        }
        return true;
    }

    private static bool IsIoError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    /// <summary>Paralel işçilerden gelen ilerlemeyi toplayıp seyrek raporlar.</summary>
    private sealed class Reporter(IProgress<DuplicateProgress>? progress, string stage, long total)
    {
        private long _done;
        private long _lastReport = Environment.TickCount64;

        public void Add(long amount)
        {
            var done = Interlocked.Add(ref _done, amount);
            if (progress is null)
                return;
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastReport);
            if (now - last > ReportIntervalMs && Interlocked.CompareExchange(ref _lastReport, now, last) == last)
                progress.Report(new DuplicateProgress(stage, done, total));
        }

        public void Finish() => progress?.Report(new DuplicateProgress(stage, Interlocked.Read(ref _done), total));
    }
}
