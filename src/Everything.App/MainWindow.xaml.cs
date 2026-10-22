using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Everything.App.Services;
using Everything.Core;

namespace Everything.App;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand FocusSearchCommand = new();
    public static readonly RoutedCommand RescanCommand = new();

    private readonly DispatcherTimer _statusTimer;
    private ReleaseInfo? _pendingRelease;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();

        AppServices.ShowSearch = (query, sortBySize) =>
        {
            ShowAndActivate();
            Tabs.SelectedItem = SearchTab;
            if (sortBySize)
                SearchView.SetSort("Boyut", descending: true);
            SearchView.FocusSearch(query);
        };
        AppServices.ShowSpaceAnalysis = root =>
        {
            if (!SpaceView.SelectVolume(root))
            {
                MessageBox.Show(this, "Bu sürücü indekslenmiyor (yalnızca yerel ve çıkarılabilir diskler taranır).", "Alan analizi",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Tabs.SelectedItem = SpaceTab;
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _statusTimer.Tick += (_, _) => OnStatusTick();
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ShowUpdateBanner(ReleaseInfo release)
    {
        _pendingRelease = release;
        UpdateBannerText.Text = $"Yeni sürüm hazır: {release.VersionText}{(release.IsPrerelease ? " (ön sürüm)" : string.Empty)}";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var roots = AppServices.Roots;
        SpaceView.Initialize(roots);
        DuplicatesView.Initialize(roots);
        ModeBadge.Text = AppServices.IsElevated ? "Hızlı NTFS modu" : string.Empty;
        _statusTimer.Start();
        SearchView.FocusSearch();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.Instance.IsExiting || !AppServices.Settings.CloseToTray)
        {
            _statusTimer.Stop();
            if (!App.Instance.IsExiting)
            {
                e.Cancel = true;
                App.Instance.Exit();
            }
            return;
        }

        // Tepside çalışmaya devam et.
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            App.Instance.ShowTrayHint();
        }
    }

    private void OnStatusTick()
    {
        var parts = AppServices.Indexes.Volumes.Select(v =>
        {
            var name = v.RootPath.TrimEnd('\\');
            return v.State switch
            {
                IndexState.Ready => $"{name}  {v.Index?.Count ?? 0:N0}",
                IndexState.Scanning when v.Index is not null => $"{name}  güncelleniyor… {v.ItemsScanned:N0}",
                IndexState.Scanning => $"{name}  taranıyor… {v.ItemsScanned:N0}",
                IndexState.Loading => $"{name}  yükleniyor…",
                IndexState.Failed => $"{name}  hata: {v.Error}",
                _ => name,
            };
        });
        StatusText.Text = $"{AppServices.Indexes.TotalItems:N0} öğe indekslendi     ·     " + string.Join("     ", parts);
        BusyBar.Visibility = AppServices.Indexes.IsBusy ? Visibility.Visible : Visibility.Collapsed;

        AppServices.RaiseTick();
        AppServices.IndexChangedFlag = false;
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs)
            return;
        if (Tabs.SelectedItem == DisksTab)
            DisksView.EnsureLoaded();
        else if (Tabs.SelectedItem == SpaceTab)
            SpaceView.EnsureBuilt();
        else if (Tabs.SelectedItem == CleanupTab)
            CleanupView.EnsureMeasured();
    }

    private void OnFocusSearch(object sender, ExecutedRoutedEventArgs e)
    {
        Tabs.SelectedItem = SearchTab;
        SearchView.FocusSearch();
    }

    private void OnRescanAll(object sender, ExecutedRoutedEventArgs e) => _ = AppServices.Indexes.RescanAllAsync();

    private void OnShowUpdate(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        Tabs.SelectedItem = SettingsTab;
        if (_pendingRelease is not null)
            SettingsView.ShowUpdate(_pendingRelease);
    }

    private void OnDismissUpdate(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        if (_pendingRelease is not null)
        {
            AppServices.Settings.SkippedVersion = _pendingRelease.VersionText;
            AppServices.Settings.Save();
        }
    }
}
