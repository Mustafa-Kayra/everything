using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Everything.Core;

/// <summary>
/// Belirli bir anda klasör boyutlarının özeti ("Ne büyüdü?" karşılaştırması için).
/// Anahtarlar köke göre göreli yollardır ("" = kök), ayırıcı <see cref="Path.DirectorySeparatorChar"/>.
/// </summary>
public sealed class SizeSnapshot
{
    public required string RootPath { get; init; }

    public required DateTime TakenUtc { get; init; }

    public required Dictionary<string, long> FolderSizes { get; init; }

    /// <summary>Platforma uygun anahtar karşılaştırıcısı (Windows'ta büyük/küçük harf duyarsız).</summary>
    public static StringComparer KeyComparer { get; } =
        FileIndex.NameComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>Anlık görüntüleri çekip sıkıştırılmış ikili dosyalar olarak saklar.</summary>
public static class SnapshotStore
{
    private const int Magic = 0x50414E53; // "SNAP"
    private const int Version = 1;
    private const string Extension = ".evsnap";
    private const string TimeFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// İndeksin klasör boyutlarını kaydeder: derinliği ≤ <paramref name="maxDepth"/> olan ve boyutu
    /// ≥ <paramref name="minFolderSize"/> olan (ya da derinliği ≤ 1 olan) klasörler. Kök derinliği 0'dır.
    /// </summary>
    public static SizeSnapshot Capture(FileIndex index, long minFolderSize = 50L * 1024 * 1024, int maxDepth = 8)
    {
        var sizes = new Dictionary<string, long>(SizeSnapshot.KeyComparer);
        index.Lock.EnterReadLock();
        try
        {
            var stack = new Stack<(int Id, string Path, int Depth)>();
            stack.Push((FileIndex.RootId, string.Empty, 0));
            while (stack.Count > 0)
            {
                var (id, path, depth) = stack.Pop();
                var size = index.GetSize(id);
                sizes[path] = size;

                // Küçük bir klasörün alt klasörleri de küçüktür; derinlik 1'e kadar yine de inilir.
                if (depth >= maxDepth || (size < minFolderSize && depth >= 1))
                    continue;
                for (var c = index.GetFirstChild(id); c != FileIndex.None; c = index.GetNextSibling(c))
                {
                    if (index.IsDeleted(c) || !index.IsDirectory(c))
                        continue;
                    if (depth + 1 <= 1 || index.GetSize(c) >= minFolderSize)
                    {
                        var name = index.GetName(c);
                        stack.Push((c, path.Length == 0 ? name : path + Path.DirectorySeparatorChar + name, depth + 1));
                    }
                }
            }
        }
        finally
        {
            index.Lock.ExitReadLock();
        }

        return new SizeSnapshot { RootPath = index.RootPath, TakenUtc = DateTime.UtcNow, FolderSizes = sizes };
    }

    /// <summary>Görüntüyü gzip'li ikili dosyaya yazar (önce geçici dosyaya, sonra yerine taşıyarak).</summary>
    public static void Save(SizeSnapshot snapshot, string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, FilePrefix(snapshot.RootPath) + snapshot.TakenUtc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture) + Extension);
        var temp = file + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
        using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(snapshot.RootPath);
            writer.Write(snapshot.TakenUtc.ToUniversalTime().Ticks);
            writer.Write(snapshot.FolderSizes.Count);
            foreach (var (path, size) in snapshot.FolderSizes)
            {
                writer.Write(path);
                writer.Write(size);
            }
        }

        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Kök için kayıtlı görüntüler, yeniden eskiye.</summary>
    public static List<(DateTime TakenUtc, string File)> List(string directory, string rootPath)
    {
        var result = new List<(DateTime TakenUtc, string File)>();
        if (!Directory.Exists(directory))
            return result;

        var prefix = FilePrefix(rootPath);
        foreach (var file in Directory.EnumerateFiles(directory, prefix + "*" + Extension))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length != prefix.Length + TimeFormat.Length)
                continue;
            if (DateTime.TryParseExact(name[prefix.Length..], TimeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var taken))
                result.Add((taken, file));
        }
        result.Sort((a, b) => b.TakenUtc.CompareTo(a.TakenUtc));
        return result;
    }

    /// <summary>Görüntüyü yükler; dosya yoksa ya da bozuksa null.</summary>
    public static SizeSnapshot? TryLoad(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                return null;

            var root = reader.ReadString();
            var ticks = reader.ReadInt64();
            var count = reader.ReadInt32();
            if (count < 0 || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return null;
            var sizes = new Dictionary<string, long>(Math.Min(count, 1 << 16), SizeSnapshot.KeyComparer);
            for (var i = 0; i < count; i++)
            {
                var path = reader.ReadString();
                sizes[path] = reader.ReadInt64();
            }
            return new SizeSnapshot { RootPath = root, TakenUtc = new DateTime(ticks, DateTimeKind.Utc), FolderSizes = sizes };
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hedef zamana en yakın görüntü: hedeften önceki (veya tam o andaki) en yeni görüntü; hiç yoksa
    /// hedeften sonraki en eski görüntü. Okunamayan dosyalar atlanır.
    /// </summary>
    public static SizeSnapshot? FindClosest(string directory, string rootPath, DateTime targetUtc)
    {
        targetUtc = targetUtc.ToUniversalTime();
        var all = List(directory, rootPath);
        var before = all.Where(s => s.TakenUtc <= targetUtc);            // yeniden eskiye
        var after = all.Where(s => s.TakenUtc > targetUtc).Reverse();    // eskiden yeniye
        foreach (var (_, file) in before.Concat(after))
        {
            var snapshot = TryLoad(file);
            if (snapshot is not null && SamePath(snapshot.RootPath, rootPath))
                return snapshot;
        }
        return null;
    }

    /// <summary>Her günden (UTC) yalnızca en yeni görüntüyü, en yeni <paramref name="keepDaily"/> gün için tutar.</summary>
    public static void Prune(string directory, string rootPath, int keepDaily = 45)
    {
        var days = new HashSet<DateTime>();
        foreach (var (taken, file) in List(directory, rootPath))
        {
            // Liste yeniden eskiye sıralı: bir günün ilk görülen görüntüsü o günün en yenisidir.
            if (days.Count < keepDaily && days.Add(taken.Date))
                continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Silinemeyen dosya bir sonraki budamada yeniden denenir.
            }
        }
    }

    /// <summary>Dosya adı öneki: kök yolunun kararlı özeti (aynı kökün görüntüleri aynı öneki taşır).</summary>
    private static string FilePrefix(string rootPath)
    {
        var normalized = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (FileIndex.NameComparison == StringComparison.OrdinalIgnoreCase)
            normalized = normalized.ToUpperInvariant();
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(normalized));
        return hash.ToString("x16", CultureInfo.InvariantCulture) + "-";
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            FileIndex.NameComparison);
}

public sealed record GrowthEntry(string RelativePath, long OldSize, long NewSize)
{
    public long Delta => NewSize - OldSize;
}

/// <summary>İki görüntü arasında en çok büyüyen (veya küçülen) klasörleri bulur.</summary>
public static class GrowthAnalyzer
{
    /// <summary>Bir ebeveynin değişiminin bu oranı tek bir alt klasörce açıklanıyorsa ebeveyn listelenmez.</summary>
    private const double AttributionThreshold = 0.8;

    /// <summary>
    /// Boyutu en çok değişen klasörler. <paramref name="growth"/> true ise büyüyenler (Delta büyükten küçüğe),
    /// false ise küçülenler (en çok küçülen önce). Değişiminin ≥ %80'i aynı yöndeki tek bir alt klasörün
    /// değişimiyle açıklanan ebeveynler atlanır; böylece liste asıl sorumlulara işaret eder. Yeni klasörler
    /// (OldSize 0) ve kaldırılanlar (NewSize 0) dahildir.
    /// Not: Görüntüler küçük klasörleri kaydetmediğinden, eşiği yeni aşan bir klasör yeni görünür.
    /// </summary>
    public static List<GrowthEntry> Compare(SizeSnapshot old, SizeSnapshot current, int top = 100, bool growth = true)
    {
        var entries = new Dictionary<string, GrowthEntry>(SizeSnapshot.KeyComparer);
        foreach (var (path, size) in current.FolderSizes)
            entries[path] = new GrowthEntry(path, old.FolderSizes.GetValueOrDefault(path), size);
        foreach (var (path, size) in old.FolderSizes)
        {
            if (!entries.ContainsKey(path))
                entries[path] = new GrowthEntry(path, size, 0);
        }

        // Her klasör için doğrudan alt klasörleri arasında aynı yöndeki en büyük değişim.
        var largestChildDelta = new Dictionary<string, long>(SizeSnapshot.KeyComparer);
        foreach (var entry in entries.Values)
        {
            if (entry.RelativePath.Length == 0 || entry.Delta == 0)
                continue;
            var parent = ParentOf(entry.RelativePath);
            var magnitude = growth ? entry.Delta : -entry.Delta;
            if (magnitude > 0 && magnitude > largestChildDelta.GetValueOrDefault(parent))
                largestChildDelta[parent] = magnitude;
        }

        return entries.Values
            .Where(e => growth ? e.Delta > 0 : e.Delta < 0)
            .Where(e => largestChildDelta.GetValueOrDefault(e.RelativePath) < AttributionThreshold * Math.Abs((double)e.Delta))
            .OrderByDescending(e => growth ? e.Delta : -e.Delta)
            .ThenBy(e => e.RelativePath, SizeSnapshot.KeyComparer)
            .Take(Math.Max(0, top))
            .ToList();
    }

    private static string ParentOf(string relativePath)
    {
        var sep = relativePath.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return sep < 0 ? string.Empty : relativePath[..sep];
    }
}
