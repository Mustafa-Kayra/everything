using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Everything.App.Native;
using Everything.Core;

namespace Everything.App.Services;

/// <summary>Temizlenebilecek bir konum grubu (ör. tarayıcı önbellekleri).</summary>
public sealed class CleanupTarget : INotifyPropertyChanged
{
    private bool _isChecked;
    private long? _size;
    private string _status = string.Empty;

    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Glyph { get; init; } = "";

    /// <summary>İçeriği silinecek klasör kalıpları; "*" bir klasör adının yerini tutar.</summary>
    public IReadOnlyList<string> Folders { get; init; } = Array.Empty<string>();

    /// <summary>Doğrudan silinecek dosyalar.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    public bool RequiresAdmin { get; init; }
    public bool IsRecycleBin { get; init; }

    /// <summary>Silinmez; yalnızca "İncele" ile aramada gösterilir.</summary>
    public string? ReviewQuery { get; init; }

    public bool CanClean => ReviewQuery is null && (!RequiresAdmin || AppServices.IsElevated);
    public bool IsReviewOnly => ReviewQuery is not null;

    public string Note => RequiresAdmin && !AppServices.IsElevated ? "Yönetici gerekir" : string.Empty;

    public bool IsChecked
    {
        get => _isChecked && CanClean;
        set
        {
            _isChecked = value;
            Changed(nameof(IsChecked));
        }
    }

    public long? Size
    {
        get => _size;
        set
        {
            _size = value;
            Changed(nameof(Size));
            Changed(nameof(SizeText));
        }
    }

    public string SizeText => _size is { } s ? ByteSize.Format(s) : "—";

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            Changed(nameof(Status));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>Temizlik hedeflerinin tanımı, boyut hesabı ve silme işlemi.</summary>
internal static class CleanupTargets
{
    public static List<CleanupTarget> Create()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var downloads = Path.Combine(profile, "Downloads");

        string[] Chromium(string userData) => new[]
        {
            Path.Combine(userData, "*", "Cache"),
            Path.Combine(userData, "*", "Code Cache"),
            Path.Combine(userData, "*", "GPUCache"),
            Path.Combine(userData, "*", "Service Worker", "CacheStorage"),
        };

        var browsers = new List<string>();
        browsers.AddRange(Chromium(Path.Combine(local, "Google", "Chrome", "User Data")));
        browsers.AddRange(Chromium(Path.Combine(local, "Microsoft", "Edge", "User Data")));
        browsers.AddRange(Chromium(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")));
        browsers.AddRange(Chromium(Path.Combine(local, "Vivaldi", "User Data")));
        browsers.Add(Path.Combine(local, "Opera Software", "Opera Stable", "Cache"));
        browsers.Add(Path.Combine(local, "Opera Software", "Opera GX Stable", "Cache"));
        browsers.Add(Path.Combine(local, "Mozilla", "Firefox", "Profiles", "*", "cache2"));

        return new List<CleanupTarget>
        {
            new()
            {
                Name = "Geçici dosyalar",
                Description = "Programların bıraktığı geçici dosyalar (%TEMP%). Kullanımdaki dosyalar atlanır.",
                Glyph = "",
                Folders = new[] { Path.GetTempPath() },
                IsChecked = true,
            },
            new()
            {
                Name = "Windows geçici dosyaları",
                Description = Path.Combine(windows, "Temp"),
                Glyph = "",
                Folders = new[] { Path.Combine(windows, "Temp") },
                RequiresAdmin = true,
                IsChecked = true,
            },
            new()
            {
                Name = "Windows Update önbelleği",
                Description = "Kurulmuş güncellemelerin indirme dosyaları; gerekirse yeniden indirilir.",
                Glyph = "",
                Folders = new[]
                {
                    Path.Combine(windows, "SoftwareDistribution", "Download"),
                    Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
                },
                RequiresAdmin = true,
                IsChecked = true,
            },
            new()
            {
                Name = "Tarayıcı önbellekleri",
                Description = "Chrome, Edge, Brave, Opera, Vivaldi ve Firefox önbellekleri. Oturumlar ve şifreler silinmez.",
                Glyph = "",
                Folders = browsers,
                IsChecked = true,
            },
            new()
            {
                Name = "Hata dökümleri ve raporları",
                Description = "Çöken programların döküm dosyaları ve Windows hata raporları.",
                Glyph = "",
                Folders = new[]
                {
                    Path.Combine(local, "CrashDumps"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
                    Path.Combine(windows, "Minidump"),
                },
                Files = new[] { Path.Combine(windows, "MEMORY.DMP") },
                IsChecked = true,
            },
            new()
            {
                Name = "Geliştirici önbellekleri",
                Description = "npm, Yarn, pip, NuGet ve Gradle paket önbellekleri. Projeler derlenirken yeniden indirilir.",
                Glyph = "",
                Folders = new[]
                {
                    Path.Combine(local, "npm-cache"),
                    Path.Combine(local, "Yarn", "Cache"),
                    Path.Combine(local, "pip", "Cache"),
                    Path.Combine(profile, ".nuget", "packages"),
                    Path.Combine(profile, ".gradle", "caches"),
                },
            },
            new()
            {
                Name = "Geri Dönüşüm Kutusu",
                Description = "Tüm sürücülerdeki Geri Dönüşüm Kutusu boşaltılır.",
                Glyph = "",
                IsRecycleBin = true,
                IsChecked = true,
            },
            new()
            {
                Name = "İndirilenler'deki eski dosyalar",
                Description = "90 günden eski ve 50 MB'tan büyük dosyalar. Otomatik silinmez; incelemek için aramada açılır.",
                Glyph = "",
                Folders = new[] { downloads },
                ReviewQuery = $"path:\"{downloads}\\\" file: dm:<son90g size:>50mb",
            },
        };
    }

    /// <summary>Hedefin şu anki toplam boyutunu hesaplar.</summary>
    public static long Measure(CleanupTarget target, CancellationToken cancellationToken)
    {
        if (target.IsRecycleBin)
        {
            var info = new Win32.RecycleBinInfo { Size = Marshal.SizeOf<Win32.RecycleBinInfo>() };
            return Win32.SHQueryRecycleBin(null, ref info) == 0 ? info.SizeBytes : 0;
        }

        long total = 0;
        var cutoff = DateTime.UtcNow.AddDays(-90);
        foreach (var file in EnumerateFiles(target, cancellationToken))
        {
            // İnceleme hedefinde yalnızca ölçüte uyan (eski ve büyük) dosyalar sayılır.
            if (target.ReviewQuery is not null && (file.Length <= 50L * 1024 * 1024 || file.LastWriteTimeUtc >= cutoff))
                continue;
            total += file.Length;
        }
        return total;
    }

    /// <summary>Hedefi temizler; boşaltılan bayt ve silinemeyen dosya sayısını döndürür.</summary>
    public static (long Freed, int Skipped) Clean(CleanupTarget target, CancellationToken cancellationToken)
    {
        if (target.IsRecycleBin)
        {
            var before = Measure(target, cancellationToken);
            Win32.SHEmptyRecycleBin(IntPtr.Zero, null, Win32.SherbNoConfirmation | Win32.SherbNoProgressUi | Win32.SherbNoSound);
            return (before - Measure(target, cancellationToken), 0);
        }

        long freed = 0;
        var skipped = 0;
        foreach (var file in EnumerateFiles(target, cancellationToken).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var length = file.Length;
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
                freed += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        // Boşalan alt klasörleri kaldır (kök klasörün kendisi kalır).
        foreach (var folder in ExpandFolders(target.Folders))
            RemoveEmptySubdirectories(folder);
        return (freed, skipped);
    }

    private static IEnumerable<FileInfo> EnumerateFiles(CleanupTarget target, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in ExpandFolders(target.Folders))
        {
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(folder).EnumerateFiles("*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }
        }
        foreach (var path in target.Files)
        {
            var file = new FileInfo(path);
            bool exists;
            try
            {
                exists = file.Exists;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                exists = false;
            }
            if (exists)
                yield return file;
        }
    }

    /// <summary>"…\User Data\*\Cache" gibi kalıpları var olan klasörlere açar.</summary>
    private static IEnumerable<string> ExpandFolders(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var parts = pattern.Split(Path.DirectorySeparatorChar);
            var current = new List<string> { parts[0] + Path.DirectorySeparatorChar };
            foreach (var part in parts.Skip(1).Where(p => p.Length > 0))
            {
                var next = new List<string>();
                foreach (var dir in current)
                {
                    if (part == "*")
                    {
                        try
                        {
                            next.AddRange(Directory.EnumerateDirectories(dir));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                    else
                    {
                        var candidate = Path.Combine(dir, part);
                        if (Directory.Exists(candidate))
                            next.Add(candidate);
                    }
                }
                current = next;
            }
            foreach (var dir in current)
                yield return dir;
        }
    }

    private static void RemoveEmptySubdirectories(string folder)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                RemoveEmptySubdirectories(dir);
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
