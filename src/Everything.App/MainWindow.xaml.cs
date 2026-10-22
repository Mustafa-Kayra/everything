using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Everything.Core;

namespace Everything.App;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand FocusSearchCommand = new();
    public static readonly RoutedCommand RescanCommand = new();

    private const int MaxResults = 200_000;

    private readonly IndexManager _manager;
    private readonly DispatcherTimer _searchDelay;
    private readonly DispatcherTimer _statusTimer;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _analysisCts;
    private volatile bool _indexChanged;
    private DateTime _lastSearch = DateTime.MinValue;
    private string _sortColumn = "Ad";
    private bool _sortDescending;
    private List<ResultItem> _results = new();
    private FileIndex? _treeIndex;
    private bool _disksLoaded;

    public MainWindow()
    {
        InitializeComponent();
        ApplyContainerStyles();

        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "cache");
        _manager = new IndexManager(cacheDir);
        _manager.IndexChanged += (_, _) => _indexChanged = true;

        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _ = RunSearchAsync(keepSelection: false);
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _statusTimer.Tick += (_, _) => OnStatusTick();
    }

    /// <summary>
    /// Fluent temasının öğe stillerini koruyarak bağlama/hizalama ayarlarını ekler
    /// (XAML'de BasedOn ile tema stiline statik olarak bağlanmak güvenilir değil).
    /// </summary>
    private void ApplyContainerStyles()
    {
        var treeItem = new Style(typeof(TreeViewItem), TryFindResource(typeof(TreeViewItem)) as Style);
        treeItem.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(FolderNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        treeItem.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(FolderNode.IsSelected)) { Mode = BindingMode.TwoWay }));
        SpaceTree.ItemContainerStyle = treeItem;

        // Sağa hizalı "Boyut" sütunu için hücre içeriği tüm genişliği kaplamalı.
        var baseStyle = TryFindResource(GridView.GridViewItemContainerStyleKey) as Style
                        ?? TryFindResource(typeof(ListViewItem)) as Style;
        var listItem = new Style(typeof(ListViewItem), baseStyle);
        listItem.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        // Fluent varsayılanı dosya listesi için fazla ferah; satırları sıkılaştır.
        listItem.Setters.Add(new Setter(MinHeightProperty, 30.0));
        listItem.Setters.Add(new Setter(HeightProperty, 30.0));
        listItem.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 0, 8, 0)));
        foreach (var list in new[] { ResultsList, CategoryList, TypeList, LargestList })
            list.ItemContainerStyle = listItem;
    }

    // ---------------------------------------------------------------- yaşam döngüsü

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var roots = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => d.RootDirectory.FullName)
            .ToList();

        _manager.Start(roots);
        VolumeCombo.ItemsSource = roots;
        if (roots.Count > 0)
            VolumeCombo.SelectedIndex = 0;

        _statusTimer.Start();
        SearchBox.Focus();
        _ = LoadDisksAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _statusTimer.Stop();
        _searchCts?.Cancel();
        _analysisCts?.Cancel();
        StatusText.Text = "İndeks kaydediliyor…";
        _manager.Dispose();
    }

    private void OnStatusTick()
    {
        var parts = _manager.Volumes.Select(v => v.State switch
        {
            IndexState.Ready => $"{v.RootPath.TrimEnd('\\')}  {v.Index?.Count ?? 0:N0}",
            IndexState.Scanning when v.Index is not null => $"{v.RootPath.TrimEnd('\\')} güncelleniyor… {v.ItemsScanned:N0}",
            IndexState.Scanning => $"{v.RootPath.TrimEnd('\\')} taranıyor… {v.ItemsScanned:N0}",
            IndexState.Loading => $"{v.RootPath.TrimEnd('\\')} yükleniyor…",
            IndexState.Failed => $"{v.RootPath.TrimEnd('\\')} hata: {v.Error}",
            _ => v.RootPath,
        });
        StatusText.Text = $"{_manager.TotalItems:N0} öğe indekslendi     ·     " + string.Join("     ", parts);
        BusyBar.Visibility = _manager.IsBusy ? Visibility.Visible : Visibility.Collapsed;

        if (_indexChanged && (DateTime.UtcNow - _lastSearch).TotalSeconds >= 2)
        {
            _indexChanged = false;
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                _ = RunSearchAsync(keepSelection: true);

            // Yeni tarama bittiyse ağacı yeni indeksle kur.
            if (VolumeCombo.SelectedItem is string root && _manager.FindVolume(root)?.Index is { } index && index != _treeIndex)
                BuildTree(root);
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs)
            return;
        if (Tabs.SelectedItem == SpaceTab && _treeIndex is null && VolumeCombo.SelectedItem is string root)
            BuildTree(root);
        if (Tabs.SelectedItem == DisksTab && !_disksLoaded)
            _ = LoadDisksAsync();
    }

    private void OnFocusSearch(object sender, ExecutedRoutedEventArgs e)
    {
        Tabs.SelectedItem = SearchTab;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnRescanAll(object sender, ExecutedRoutedEventArgs e) => _ = _manager.RescanAllAsync();

    // ---------------------------------------------------------------- arama

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && ResultsList.Items.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
            (ResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ResultsList.Items.Count > 0)
        {
            Shell.Open(((ResultItem)ResultsList.Items[0]).FullPath);
            e.Handled = true;
        }
    }

    private async Task RunSearchAsync(bool keepSelection)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _lastSearch = DateTime.UtcNow;

        var query = SearchQuery.Parse(SearchBox.Text);
        var indexes = _manager.GetIndexes();
        var sortColumn = _sortColumn;
        var descending = _sortDescending;
        var selected = keepSelection ? ResultsList.SelectedItem as ResultItem : null;

        try
        {
            var (result, items) = await Task.Run(() =>
            {
                var r = SearchEngine.Search(indexes, query, MaxResults, cts.Token);
                var list = new List<ResultItem>(r.Hits.Count);
                foreach (var group in r.Hits.GroupBy(h => h.Index))
                {
                    group.Key.Lock.EnterReadLock();
                    try
                    {
                        foreach (var hit in group)
                            list.Add(new ResultItem(hit.Index, hit.Id));
                    }
                    finally
                    {
                        group.Key.Lock.ExitReadLock();
                    }
                }
                cts.Token.ThrowIfCancellationRequested();
                return (r, Sort(list, sortColumn, descending));
            }, cts.Token);

            if (cts.IsCancellationRequested)
                return;

            // Canlı güncellemede sonuçlar aynıysa listeyi yenileme (kaydırma konumu korunur).
            if (keepSelection && SameItems(_results, items))
                return;

            _results = items;
            ResultsList.ItemsSource = items;
            if (selected is not null)
            {
                var again = items.FirstOrDefault(i => i.Index == selected.Index && i.Id == selected.Id);
                if (again is not null)
                {
                    ResultsList.SelectedItem = again;
                    ResultsList.ScrollIntoView(again);
                }
            }

            SearchInfo.Text = query.IsEmpty
                ? string.Empty
                : result.Truncated
                    ? $"{result.TotalMatches:N0} sonuç ({MaxResults:N0} tanesi gösteriliyor) • {result.Elapsed.TotalMilliseconds:N0} ms"
                    : $"{result.TotalMatches:N0} sonuç • {result.Elapsed.TotalMilliseconds:N0} ms";
            if (indexes.Count == 0 && !query.IsEmpty)
                SearchInfo.Text = "Diskler taranıyor, birazdan sonuçlar görünecek…";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool SameItems(List<ResultItem> a, List<ResultItem> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Index != b[i].Index || a[i].Id != b[i].Id || a[i].Size != b[i].Size || a[i].Name != b[i].Name)
                return false;
        }
        return true;
    }

    private static List<ResultItem> Sort(List<ResultItem> items, string column, bool descending)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<ResultItem> ordered = column switch
        {
            "Boyut" => descending ? items.OrderByDescending(i => i.Size) : items.OrderBy(i => i.Size),
            "Değiştirilme" => descending ? items.OrderByDescending(i => i.Modified) : items.OrderBy(i => i.Modified),
            "Konum" => descending ? items.OrderByDescending(i => i.Folder, comparer) : items.OrderBy(i => i.Folder, comparer),
            _ => descending ? items.OrderByDescending(i => i.Name, comparer) : items.OrderBy(i => i.Name, comparer),
        };
        return ordered.ThenBy(i => i.Name, comparer).ToList();
    }

    private void OnResultHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column.Header: string header })
            return;
        if (header == _sortColumn)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = header;
            // Boyut ve tarih için büyükten küçüğe başla.
            _sortDescending = header is "Boyut" or "Değiştirilme";
        }
        _results = Sort(_results, _sortColumn, _sortDescending);
        ResultsList.ItemsSource = _results;
    }

    private void OnShowSearchHelp(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "rapor 2024            adında hem \"rapor\" hem \"2024\" geçenler\n" +
            "\"yıllık rapor\"         tam ifade\n" +
            "*.mp4  /  IMG_????.jpg   joker karakterler\n" +
            "!temp                 \"temp\" geçenleri hariç tut\n" +
            "ext:pdf;docx          uzantıya göre\n" +
            "size:>1gb  size:<10kb  size:10mb..1gb   boyuta göre (dosyalar)\n" +
            "folder: size:>10gb    10 GB'tan büyük klasörler\n" +
            "file: / folder:       yalnızca dosyalar / klasörler\n" +
            "path:Belgeler  veya  Belgeler\\rapor   tam yolda ara\n\n" +
            "Kısayollar: Ctrl+F arama kutusu • Enter ilk sonucu aç • Esc temizle • F5 yeniden tara",
            "Arama ipuçları", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- sonuç / menü işlemleri

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView { SelectedItem: ResultItem item })
            Shell.Open(item.FullPath);
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListView { SelectedItem: ResultItem item } list)
            return;
        if (e.Key == Key.Enter)
        {
            Shell.Open(item.FullPath);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Shell.CopyPath(string.Join(Environment.NewLine, list.SelectedItems.OfType<ResultItem>().Select(i => i.FullPath)));
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteItem(item.FullPath);
            e.Handled = true;
        }
    }

    /// <summary>Sağ tık menüsünün açıldığı listedeki/ağaçtaki seçili öğelerin yolları.</summary>
    private static List<string> GetMenuTargets(object sender)
    {
        var target = (sender as MenuItem)?.Parent is ContextMenu menu ? menu.PlacementTarget : null;
        return target switch
        {
            ListView list => list.SelectedItems.OfType<ResultItem>().Select(i => i.FullPath).ToList(),
            TreeView { SelectedItem: FolderNode node } when node.GetFullPath() is { } path => new List<string> { path },
            _ => new List<string>(),
        };
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        foreach (var path in GetMenuTargets(sender).Take(10))
            Shell.Open(path);
    }

    private void OnMenuShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (GetMenuTargets(sender).FirstOrDefault() is { } path)
            Shell.ShowInExplorer(path);
    }

    private void OnMenuCopyPath(object sender, RoutedEventArgs e)
    {
        var paths = GetMenuTargets(sender);
        if (paths.Count > 0)
            Shell.CopyPath(string.Join(Environment.NewLine, paths));
    }

    private void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        if (GetMenuTargets(sender).FirstOrDefault() is { } path)
            DeleteItem(path);
    }

    private void DeleteItem(string path)
    {
        if (Shell.MoveToRecycleBin(path, this))
            _indexChanged = true; // izleyici silmeyi indekse işleyecek; görünümü tazele
    }

    // ---------------------------------------------------------------- diskler

    private async Task LoadDisksAsync()
    {
        _disksLoaded = true;
        DisksInfo.Text = "Disk bilgileri okunuyor…";
        try
        {
            var disks = await Task.Run(DiskInfoProvider.GetDisks);
            DisksList.ItemsSource = disks;
            DisksInfo.Text = $"{disks.Count} sürücü • Toplam {ByteSize.Format(disks.Sum(d => d.TotalSize))}, " +
                             $"boş {ByteSize.Format(disks.Sum(d => d.FreeSpace))}";
        }
        catch (Exception ex)
        {
            DisksInfo.Text = "Disk bilgileri okunamadı: " + ex.Message;
        }
    }

    private void OnRefreshDisks(object sender, RoutedEventArgs e) => _ = LoadDisksAsync();

    private void OnAnalyzeDisk(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string root)
            return;
        var match = VolumeCombo.Items.OfType<string>().FirstOrDefault(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            MessageBox.Show(this, "Bu sürücü indekslenmiyor (yalnızca yerel ve çıkarılabilir diskler taranır).", "Alan analizi",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        VolumeCombo.SelectedItem = match;
        Tabs.SelectedItem = SpaceTab;
    }

    private void OnOpenDisk(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string root)
            Shell.Open(root);
    }

    // ---------------------------------------------------------------- alan analizi

    private void OnVolumeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VolumeCombo.SelectedItem is string root)
            BuildTree(root);
    }

    private void OnRefreshSpace(object sender, RoutedEventArgs e)
    {
        if (VolumeCombo.SelectedItem is string root)
            BuildTree(root);
    }

    private void OnRescanVolume(object sender, RoutedEventArgs e)
    {
        if (VolumeCombo.SelectedItem is string root && _manager.FindVolume(root) is { } volume)
            _ = Task.Run(volume.RescanAsync);
    }

    private void BuildTree(string root)
    {
        var volume = _manager.FindVolume(root);
        var index = volume?.Index;
        _treeIndex = index;
        if (index is null)
        {
            SpaceTree.ItemsSource = null;
            SpaceInfo.Text = "Sürücü taranıyor, tamamlanınca analiz burada görünecek…";
            SelectionTitle.Text = string.Empty;
            CategoryList.ItemsSource = TypeList.ItemsSource = LargestList.ItemsSource = null;
            return;
        }

        var rootNode = FolderNode.CreateRoot(index);
        rootNode.IsExpanded = true;
        rootNode.IsSelected = true;
        SpaceTree.ItemsSource = new[] { rootNode };

        var disk = DriveInfo.GetDrives().FirstOrDefault(d => string.Equals(d.RootDirectory.FullName, root, StringComparison.OrdinalIgnoreCase));
        var used = disk is { IsReady: true } ? disk.TotalSize - disk.TotalFreeSpace : 0;
        SpaceInfo.Text = used > 0
            ? $"Dosyalar {ByteSize.Format(rootNode.Size)}  ·  Diskte kullanılan {ByteSize.Format(used)}"
            : $"Dosyalar {ByteSize.Format(rootNode.Size)}";
        SpaceInfo.ToolTip = used > rootNode.Size
            ? $"Aradaki {ByteSize.Format(used - rootNode.Size)}; yönetici izni olmadan erişilemeyen klasörler, gölge kopyalar ve sistem dosyalarıdır."
            : null;
        AnalyzeSelection(rootNode);
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode node)
            AnalyzeSelection(node);
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SpaceTree.SelectedItem is FolderNode { IsDirectory: false } node && node.GetFullPath() is { } path)
            Shell.Open(path);
    }

    private async void AnalyzeSelection(FolderNode node)
    {
        if (node.Index is null || node.IsAggregate)
            return;

        var index = node.Index;
        var id = node.IsDirectory ? node.Id : index.GetParent(node.Id);
        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;

        string path;
        long total;
        int files;
        index.Lock.EnterReadLock();
        try
        {
            path = index.GetFullPath(id);
            total = index.GetSize(id);
            files = index.GetFileCount(id);
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
        SelectionTitle.Text = $"{path}  —  {ByteSize.Format(total)}, {files:N0} dosya";

        try
        {
            var (types, categories, largest) = await Task.Run(() =>
            {
                var byExt = SpaceAnalyzer.GetUsageByExtension(index, id, cts.Token);
                var byCategory = SpaceAnalyzer.GroupByCategory(byExt);
                var top = SpaceAnalyzer.GetLargestFiles(index, id, 500, cts.Token);
                List<ResultItem> topItems;
                index.Lock.EnterReadLock();
                try
                {
                    topItems = top.Where(i => !index.IsDeleted(i)).Select(i => new ResultItem(index, i)).ToList();
                }
                finally
                {
                    index.Lock.ExitReadLock();
                }
                return (byExt.Select(t => new TypeRow(t, total)).ToList(),
                        byCategory.Select(t => new TypeRow(t, total)).ToList(),
                        topItems);
            }, cts.Token);

            if (cts.IsCancellationRequested)
                return;
            TypeList.ItemsSource = types;
            CategoryList.ItemsSource = categories;
            LargestList.ItemsSource = largest;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnTypeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TypeList.SelectedItem is not TypeRow row || SpaceTree.SelectedItem is not FolderNode node || node.Index is null)
            return;
        var folder = node.IsDirectory ? node.GetFullPath() : Path.GetDirectoryName(node.GetFullPath());
        var ext = row.Key == FileCategories.NoExtension ? "file: !." : "ext:" + row.Key.TrimStart('.');
        var scope = folder is null || folder.Length <= 3 ? string.Empty : $" path:\"{folder.TrimEnd('\\')}\\\"";
        Tabs.SelectedItem = SearchTab;
        _sortColumn = "Boyut";
        _sortDescending = true;
        SearchBox.Text = ext + scope;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }
}
