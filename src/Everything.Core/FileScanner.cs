using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Everything.Core;

public readonly record struct ScanProgress(string RootPath, int ItemsFound, string CurrentDirectory);

/// <summary>
/// Bir klasör ağacını paralel olarak tarar ve <see cref="FileIndex"/> oluşturur.
/// Bağlantı noktalarına (junction / symlink) girilmez; erişilemeyen klasörler atlanır.
/// </summary>
public static class FileScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0, // gizli ve sistem dosyaları da dahil
    };

    private readonly record struct RawEntry(string Name, bool IsDirectory, long Size, long ModifiedTicks, bool IsReparsePoint);

    public static FileIndex Scan(string rootPath, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var index = new FileIndex(rootPath, 64 * 1024);
        ScanInto(index, FileIndex.RootId, rootPath, progress, cancellationToken);
        index.ComputeAggregates();
        index.LastUpdatedUtc = DateTime.UtcNow;
        return index;
    }

    /// <summary>
    /// <paramref name="directoryPath"/> içeriğini <paramref name="directoryId"/> altına ekler.
    /// Toplamları hesaplamaz; çağıran <see cref="FileIndex.ComputeAggregates"/> ya da
    /// <see cref="FileIndex.RecomputeSubtree"/> çağırmalıdır.
    /// </summary>
    public static void ScanInto(FileIndex index, int directoryId, string directoryPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var frontier = new List<(int Id, string Path)> { (directoryId, directoryPath) };
        var gate = new object();
        var found = 0;
        var lastReport = Environment.TickCount64;

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = new ConcurrentBag<(int, string)>();

            Parallel.ForEach(
                frontier,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                dir =>
                {
                    var entries = ReadDirectory(dir.Path);
                    if (entries.Count == 0)
                        return;

                    lock (gate)
                    {
                        foreach (var e in entries)
                        {
                            var id = index.AddEntry(dir.Id, e.Name, e.IsDirectory, e.Size, e.ModifiedTicks, e.IsReparsePoint);
                            if (e.IsDirectory && !e.IsReparsePoint)
                                next.Add((id, Path.Join(dir.Path, e.Name)));
                        }
                        found += entries.Count;

                        var now = Environment.TickCount64;
                        if (progress is not null && now - lastReport > 200)
                        {
                            lastReport = now;
                            progress.Report(new ScanProgress(index.RootPath, found, dir.Path));
                        }
                    }
                });

            frontier = next.ToList();
        }

        progress?.Report(new ScanProgress(index.RootPath, found, string.Empty));
    }

    private static List<RawEntry> ReadDirectory(string path)
    {
        var list = new List<RawEntry>();
        try
        {
            var enumerable = new FileSystemEnumerable<RawEntry>(
                path,
                static (ref FileSystemEntry e) => new RawEntry(
                    e.FileName.ToString(),
                    e.IsDirectory,
                    e.IsDirectory ? 0 : e.Length,
                    e.LastWriteTimeUtc.UtcTicks,
                    (e.Attributes & FileAttributes.ReparsePoint) != 0),
                Options);
            foreach (var entry in enumerable)
                list.Add(entry);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Erişilemeyen ya da tarama sırasında silinen klasörleri atla.
        }
        return list;
    }

    /// <summary>Tek bir yol için güncel bilgileri okur (izleyici olayları için).</summary>
    internal static bool TryStat(string path, out bool isDirectory, out long size, out long modifiedTicks, out bool isReparsePoint)
    {
        isDirectory = false;
        size = 0;
        modifiedTicks = 0;
        isReparsePoint = false;
        try
        {
            FileSystemInfo info = new FileInfo(path);
            if (!info.Exists)
            {
                info = new DirectoryInfo(path);
                if (!info.Exists)
                    return false;
            }
            isDirectory = info is DirectoryInfo;
            size = info is FileInfo fi ? fi.Length : 0;
            modifiedTicks = info.LastWriteTimeUtc.Ticks;
            isReparsePoint = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
