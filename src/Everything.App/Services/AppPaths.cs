using System.IO;

namespace Everything.App.Services;

/// <summary>
/// Uygulamanın dosya konumları. Kurulu sürüm (yanında unins000.exe olan) verilerini
/// %LOCALAPPDATA%\EverythingArama altında, portable sürüm exe'nin yanındaki
/// "Everything-Data" klasöründe tutar (klasör yazılabilir değilse LOCALAPPDATA'ya düşer).
/// </summary>
internal static class AppPaths
{
    public const string ProductName = "Everything Arama";
    public const string RepositoryOwner = "Mustafa-Kayra";
    public const string RepositoryName = "everything";

    static AppPaths()
    {
        ExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EverythingArama.exe");
        ExeDirectory = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;
        IsInstalled = File.Exists(Path.Combine(ExeDirectory, "unins000.exe"));

        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EverythingArama");
        var portableData = Path.Combine(ExeDirectory, "Everything-Data");
        DataDirectory = !IsInstalled && IsWritable(ExeDirectory) ? portableData : localData;
        Directory.CreateDirectory(DataDirectory);
    }

    public static string ExePath { get; }
    public static string ExeDirectory { get; }
    public static bool IsInstalled { get; }
    public static bool IsPortable => !IsInstalled;
    public static string DataDirectory { get; }

    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public static string SnapshotDirectory => Path.Combine(DataDirectory, "snapshots");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LogFile => Path.Combine(DataDirectory, "everything.log");
    public static string UpdateDirectory => Path.Combine(DataDirectory, "updates");

    public static Version CurrentVersion =>
        typeof(AppPaths).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    /// <summary>Basit günlük; hata ayıklama için (en fazla ~1 MB tutulur).</summary>
    public static void Log(string message)
    {
        try
        {
            var info = new FileInfo(LogFile);
            if (info.Exists && info.Length > 1024 * 1024)
                info.Delete();
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
