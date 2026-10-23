using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Everything.App.Services;

namespace Everything.App.Views;

public partial class SettingsView : UserControl
{
    private ReleaseInfo? _release;
    private CancellationTokenSource? _downloadCts;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadValues();
    }

    private void LoadValues()
    {
        _loading = true;
        var s = AppServices.Settings;
        VersionText.Text = $"Everything Arama {AppPaths.CurrentVersionText}  ·  {(AppPaths.IsInstalled ? "Kurulu sürüm" : "Portable sürüm")}";
        if (_release is null && string.IsNullOrEmpty(UpdateStatus.Text))
            UpdateStatus.Text = s.LastUpdateCheckUtc == default ? "Henüz denetlenmedi." : $"Son denetim: {s.LastUpdateCheckUtc.ToLocalTime():g}";
        AutoUpdateCheck.IsChecked = s.AutoCheckUpdates;
        PrereleaseCheck.IsChecked = s.IncludePrereleases;
        StartupCheck.IsChecked = StartupManager.IsEnabled;
        StartupNote.Text = StartupManager.IsElevatedTask
            ? "Görev Zamanlayıcı ile yönetici olarak başlıyor: hızlı NTFS modu her açılışta etkin."
            : AppServices.IsElevated
                ? "Açarsanız uygulama oturum açılışında UAC sormadan yönetici olarak başlar."
                : "Hızlı NTFS modu için bu ayarı yönetici olarak çalışırken açın.";
        CloseToTrayCheck.IsChecked = s.CloseToTray;
        HotkeyBox.Text = string.IsNullOrWhiteSpace(s.Hotkey) ? "Kapalı" : s.Hotkey;
        DiacriticsCheck.IsChecked = s.IgnoreDiacritics;
        PreviewCheck.IsChecked = s.ShowPreview;
        RefreshCombo.SelectedItem = RefreshCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.RefreshIfOlderThanDays.ToString())
                                    ?? RefreshCombo.Items[2];

        var journal = AppServices.Indexes.Volumes.Any(v => v.UsesJournal);
        ModeTitle.Text = AppServices.IsElevated ? "Hızlı NTFS modu: Etkin" : "Hızlı NTFS modu: Kapalı";
        ModeText.Text = AppServices.IsElevated
            ? journal
                ? "Diskler doğrudan NTFS kayıtlarından (MFT) okunuyor; kapalıyken olan değişiklikler açılışta USN günlüğünden saniyeler içinde uygulanıyor."
                : "Yönetici olarak çalışıyor, ancak NTFS olmayan sürücülerde normal tarama kullanılıyor."
            : "Kayıtlı indeks anında yükleniyor. Yönetici olarak çalıştırırsanız ilk tarama saniyeler sürer ve kapalıyken olan değişiklikler de yakalanır.";
        ElevateButton.Visibility = AppServices.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        DataFolderText.Text = "Veriler: " + AppPaths.DataDirectory;
        _loading = false;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var s = AppServices.Settings;
        s.AutoCheckUpdates = AutoUpdateCheck.IsChecked == true;
        s.IncludePrereleases = PrereleaseCheck.IsChecked == true;
        s.CloseToTray = CloseToTrayCheck.IsChecked == true;
        s.IgnoreDiacritics = DiacriticsCheck.IsChecked == true;
        s.ShowPreview = PreviewCheck.IsChecked == true;
        s.Save();
        AppServices.RaiseSettingsChanged();
    }

    private void OnRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RefreshCombo.SelectedItem is not ComboBoxItem item)
            return;
        AppServices.Settings.RefreshIfOlderThanDays = int.Parse((string)item.Tag);
        AppServices.Settings.Save();
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var error = StartupManager.SetEnabled(StartupCheck.IsChecked == true);
        if (error is not null)
            MessageBox.Show(Window.GetWindow(this), error, "Başlangıç ayarı değiştirilemedi", MessageBoxButton.OK, MessageBoxImage.Warning);
        LoadValues();
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab)
            return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            HotkeyStatus.Text = "En az bir değiştirici tuş (Ctrl, Alt, Shift veya Win) kullanın.";
            return;
        }
        ApplyHotkey(HotkeyService.Format(modifiers, key));
    }

    private void OnClearHotkey(object sender, RoutedEventArgs e) => ApplyHotkey(string.Empty);

    private void ApplyHotkey(string text)
    {
        var ok = App.Instance.SetHotkey(text);
        HotkeyBox.Text = string.IsNullOrEmpty(text) ? "Kapalı" : text;
        HotkeyStatus.Text = ok
            ? string.IsNullOrEmpty(text) ? "Kısayol kapalı." : $"{text} ile her yerden hızlı arama açılır."
            : $"{text} başka bir program tarafından kullanılıyor; farklı bir birleşim deneyin.";
    }

    private void OnRestartElevated(object sender, RoutedEventArgs e) => App.Instance.RestartElevated();

    private void OnRescanAll(object sender, RoutedEventArgs e) => _ = AppServices.Indexes.RescanAllAsync();

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => Shell.Open(AppPaths.DataDirectory);

    private void OnOpenRepository(object sender, RoutedEventArgs e) =>
        Shell.Open($"https://github.com/{AppPaths.RepositoryOwner}/{AppPaths.RepositoryName}");

    // ---------------------------------------------------------------- güncellemeler

    /// <summary>Ana penceredeki güncelleme bildiriminden gelinirken sürümü gösterir.</summary>
    public void ShowUpdate(ReleaseInfo release) => ShowRelease(release);

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateStatus.Text = "Denetleniyor…";
        try
        {
            var release = await Updater.CheckAsync(AppServices.Settings.IncludePrereleases, CancellationToken.None);
            AppServices.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            AppServices.Settings.Save();
            if (release is null)
            {
                _release = null;
                UpdatePanel.Visibility = Visibility.Collapsed;
                UpdateStatus.Text = "En son sürümü kullanıyorsunuz.";
            }
            else
            {
                ShowRelease(release);
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Denetlenemedi: " + ex.Message;
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private void ShowRelease(ReleaseInfo release)
    {
        _release = release;
        UpdatePanel.Visibility = Visibility.Visible;
        UpdateStatus.Text = $"Yeni sürüm: {release.VersionText}{(release.IsPrerelease ? " (ön sürüm)" : string.Empty)} · {release.PublishedUtc.ToLocalTime():d}";
        ReleaseNotes.Text = string.IsNullOrWhiteSpace(release.Notes) ? release.Title : release.Notes.Replace("\r\n", "\n");
        InstallButtonText.Text = AppPaths.IsInstalled ? "İndir ve kur" : "İndir ve güncelle";
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_release is null)
            return;
        if (_downloadCts is not null)
        {
            _downloadCts.Cancel();
            return;
        }

        _downloadCts = new CancellationTokenSource();
        InstallButtonText.Text = "İptal";
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        try
        {
            var file = await Updater.DownloadAsync(_release, new Progress<double>(p => DownloadProgress.Value = p), _downloadCts.Token);
            UpdateStatus.Text = "Güncelleme uygulanıyor; uygulama yeniden başlayacak…";
            Updater.Apply(file, App.Instance.PrepareForRestart);
            App.Instance.Exit();
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "İndirme iptal edildi.";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Güncellenemedi: " + ex.Message;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            DownloadProgress.Visibility = Visibility.Collapsed;
            InstallButtonText.Text = AppPaths.IsInstalled ? "İndir ve kur" : "İndir ve güncelle";
        }
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        if (_release is not null)
            Process.Start(new ProcessStartInfo(_release.PageUrl) { UseShellExecute = true });
    }
}
