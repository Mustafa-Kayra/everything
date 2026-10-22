using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Everything.App.Services;
using Everything.App.Views;
using Everything.Core;
using Everything.Core.Ntfs;

namespace Everything.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private TrayIcon? _tray;
    private HotkeyService? _hotkey;
    private QuickSearchWindow? _quickSearch;
    private DispatcherTimer? _maintenance;

    internal static App Instance => (App)Current;

    /// <summary>Uygulama gerçekten kapanıyor (tepsiye küçültme değil).</summary>
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
        var args = e.Args;

        // Kurulum/kaldırma programı için: başlangıç görevini oluştur/sil ve çık.
        if (args.Contains("--register-startup"))
        {
            Shutdown(StartupManager.SetEnabled(true) is null ? 0 : 1);
            return;
        }
        if (args.Contains("--unregister-startup"))
        {
            StartupManager.SetEnabled(false);
            Shutdown(0);
            return;
        }

        var restarted = args.Contains("--restarted") || args.Contains("--updated");
        _singleInstance = SingleInstance.TryCreate(args.Contains("--quick"), restarted ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        if (_singleInstance is null)
        {
            Shutdown(0);
            return;
        }

        AppServices.Settings = AppSettings.Load();
        Updater.CleanupLeftovers();

        var roots = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
        AppServices.Indexes = new IndexManager(CreateIndexOptions);
        AppServices.Indexes.IndexChanged += (_, _) => AppServices.IndexChangedFlag = true;
        AppServices.Indexes.Start(roots);

        var main = new MainWindow();
        MainWindow = main;
        if (!args.Contains("--tray"))
            main.Show();

        _tray = new TrayIcon(ShowMain, ShowQuickSearch, () => _ = AppServices.Indexes.RescanAllAsync(), Exit, AppServices.Settings.Hotkey);
        _hotkey = new HotkeyService();
        _hotkey.Pressed += (_, _) => ShowQuickSearch();
        _hotkey.Register(AppServices.Settings.Hotkey);

        _singleInstance.Listen(
            () => Dispatcher.BeginInvoke(ShowMain),
            () => Dispatcher.BeginInvoke(ShowQuickSearch));
        if (args.Contains("--quick"))
            ShowQuickSearch();

        // Bakım: günlük boyut kaydı ve güncelleme denetimi.
        _maintenance = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _maintenance.Tick += async (_, _) =>
        {
            _maintenance.Interval = TimeSpan.FromMinutes(30);
            await RunMaintenanceAsync();
        };
        _maintenance.Start();
    }

    private static VolumeIndexerOptions CreateIndexOptions(string root)
    {
        var days = AppServices.Settings.RefreshIfOlderThanDays;
        var fast = StartupManager.IsElevated && WindowsVolume.IsNtfs(root);
        return new VolumeIndexerOptions
        {
            CacheDirectory = AppPaths.CacheDirectory,
            RefreshIfOlderThan = days > 0 ? TimeSpan.FromDays(days) : null,
            FastScan = fast
                ? (path, progress, token) =>
                {
                    using var volume = WindowsVolume.OpenVolumeStream(path);
                    return MftReader.Read(volume, path, progress, token);
                }
                : null,
            JournalFactory = fast ? path => new WindowsUsnJournal(path) : null,
        };
    }

    private void ShowMain()
    {
        if (IsExiting)
            return;
        (MainWindow as MainWindow)?.ShowAndActivate();
    }

    private void ShowQuickSearch()
    {
        if (IsExiting)
            return;
        _quickSearch ??= new QuickSearchWindow();
        if (_quickSearch.IsVisible)
            _quickSearch.Hide();
        else
            _quickSearch.ShowAndFocus();
    }

    public void ShowTrayHint()
    {
        var hotkey = AppServices.Settings.Hotkey;
        _tray?.ShowBalloon("Everything tepside çalışıyor",
            string.IsNullOrWhiteSpace(hotkey) ? "Açmak için tepsideki simgeye tıklayın." : $"Her yerden aramak için {hotkey}'e basın.");
    }

    /// <summary>Genel kısayolu değiştirir ve kaydeder. Kısayol başka programdaysa false.</summary>
    public bool SetHotkey(string text)
    {
        AppServices.Settings.Hotkey = text;
        AppServices.Settings.Save();
        _tray?.UpdateHotkeyText(text);
        return _hotkey?.Register(text) ?? false;
    }

    public void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ExePath, "--restarted") { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception)
        {
            // Kullanıcı UAC'yi iptal etti.
            return;
        }
        Exit();
    }

    /// <summary>Yeniden başlatmadan önce: indeksi kaydet, kısayolu ve tek örnek kilidini bırak.</summary>
    public void PrepareForRestart()
    {
        if (IsExiting)
            return;
        IsExiting = true;
        _maintenance?.Stop();
        _hotkey?.Dispose();
        _tray?.Dispose();
        AppServices.Indexes?.Dispose();
        _singleInstance?.Dispose();
    }

    public new void Exit()
    {
        PrepareForRestart();
        _quickSearch?.Close();
        MainWindow?.Close();
        Shutdown();
    }

    private async Task RunMaintenanceAsync()
    {
        try
        {
            await Task.Run(SnapshotScheduler.TakeDueSnapshots);

            var settings = AppServices.Settings;
            if (!settings.AutoCheckUpdates || DateTime.UtcNow - settings.LastUpdateCheckUtc < TimeSpan.FromHours(20))
                return;
            var release = await Updater.CheckAsync(settings.IncludePrereleases, CancellationToken.None);
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            settings.Save();
            if (release is null || release.VersionText == settings.SkippedVersion)
                return;
            (MainWindow as MainWindow)?.ShowUpdateBanner(release);
            if (MainWindow?.IsVisible != true)
                _tray?.ShowBalloon("Everything güncellemesi", $"Yeni sürüm hazır: {release.VersionText}. Ayarlar'dan güncelleyebilirsiniz.");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Bakım hatası: " + ex.Message);
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.Log("Beklenmeyen hata: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "Beklenmeyen hata", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
