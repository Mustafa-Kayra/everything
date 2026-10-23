using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Everything.App.Services;
using Everything.Core;

namespace Everything.App.Views;

public partial class SpaceView : UserControl
{
    private CancellationTokenSource? _analysisCts;
    private FileIndex? _treeIndex;
    private int _selectedDirectory = FileIndex.None;
    private int _growthVersion;
    private bool? _legendDark;

    public SpaceView()
    {
        InitializeComponent();
        UiStyles.BindTreeItems(SpaceTree);
        foreach (var list in new[] { CategoryList, TypeList, LargestList, GrowthList })
            UiStyles.CompactList(list);
        SpaceTree.ContextMenu = ItemMenu.Create(() => SpaceTree.SelectedItem is FolderNode node && node.GetFullPath() is { } p ? new[] { p } : Array.Empty<string>(),
            () => AppServices.IndexChangedFlag = true);
        LargestList.ContextMenu = ItemMenu.Create(() => LargestList.SelectedItems.OfType<ResultItem>().Select(i => i.FullPath).ToList(),
            () => AppServices.IndexChangedFlag = true);
        GrowthList.ContextMenu = ItemMenu.Create(() => GrowthList.SelectedItem is GrowthRow row ? new[] { row.Path } : Array.Empty<string>());

        Treemap.HoverChanged += OnTreemapHover;
        Treemap.NodeOpened += OnTreemapOpened;
        Treemap.NodeClicked += (_, node) => HoverText.Text = Describe(node);
        AppServices.Tick += (_, _) => OnTick();
    }

    /// <summary>Kökleri yükler (pencere açılınca bir kez).</summary>
    public void Initialize(IReadOnlyList<string> roots)
    {
        VolumeCombo.ItemsSource = roots;
        if (roots.Count > 0)
            VolumeCombo.SelectedIndex = 0;
    }

    /// <summary>Verilen sürücüyü seçer (Diskler sekmesinden gelinirken).</summary>
    public bool SelectVolume(string root)
    {
        var match = VolumeCombo.Items.OfType<string>().FirstOrDefault(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;
        VolumeCombo.SelectedItem = match;
        return true;
    }

    public void EnsureBuilt()
    {
        if (_treeIndex is null && VolumeCombo.SelectedItem is string root)
            BuildTree(root);
    }

    private void OnTick()
    {
        // Yeni tarama bittiyse (indeks nesnesi değişti) ağacı yeniden kur.
        if (VolumeCombo.SelectedItem is string root && AppServices.Indexes.FindVolume(root)?.Index is { } index && index != _treeIndex && IsVisible)
            BuildTree(root);
        var dark = TreemapPalette.IsDarkTheme();
        if (_legendDark != dark)
            BuildLegend(dark);
    }

    private void BuildLegend(bool dark)
    {
        _legendDark = dark;
        Legend.Children.Clear();
        foreach (var entry in TreemapPalette.Entries)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
            panel.Children.Add(new Rectangle
            {
                Width = 10,
                Height = 10,
                RadiusX = 2,
                RadiusY = 2,
                Fill = TreemapPalette.Brush(dark ? entry.Dark : entry.Light),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(new TextBlock { Text = entry.Label, FontSize = 12, Style = (Style)FindResource("Secondary") });
            Legend.Children.Add(panel);
        }
    }

    private void OnVolumeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VolumeCombo.SelectedItem is string root)
            BuildTree(root);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (VolumeCombo.SelectedItem is string root)
            BuildTree(root);
    }

    private void OnRescan(object sender, RoutedEventArgs e)
    {
        if (VolumeCombo.SelectedItem is string root && AppServices.Indexes.FindVolume(root) is { } volume)
            _ = Task.Run(volume.RescanAsync);
    }

    private void BuildTree(string root)
    {
        var volume = AppServices.Indexes.FindVolume(root);
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

    private void OnLargestDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LargestList.SelectedItem is ResultItem item)
            Shell.Open(item.FullPath);
    }

    private async void AnalyzeSelection(FolderNode node)
    {
        if (node.Index is null || node.IsAggregate)
            return;
        var index = node.Index;
        var id = node.IsDirectory ? node.Id : index.GetParent(node.Id);
        await AnalyzeDirectoryAsync(index, id);
    }

    private async Task AnalyzeDirectoryAsync(FileIndex index, int id)
    {
        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        _selectedDirectory = id;

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
        Treemap.Show(index, id);
        UpButton.IsEnabled = id != FileIndex.RootId;
        if (DetailTabs.SelectedItem == GrowthTab)
            _ = LoadGrowthAsync();

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

    // ---------------------------------------------------------------- treemap

    private string Describe(TreemapNode node)
    {
        var index = Treemap.Index;
        if (index is null)
            return node.Name;
        string path;
        index.Lock.EnterReadLock();
        try
        {
            path = index.IsDeleted(node.Id) ? node.Name : index.GetFullPath(node.Id);
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
        var kind = node.IsDirectory ? "Klasör" : node.Category;
        return $"{path}   ·   {ByteSize.Format(node.Size)}   ·   {kind}";
    }

    private void OnTreemapHover(object? sender, TreemapNode? node)
    {
        HoverText.Text = node is null ? "Bir kutunun üzerine gelin; klasöre girmek için çift tıklayın." : Describe(node);
    }

    private void OnTreemapOpened(object? sender, TreemapNode node)
    {
        if (Treemap.Index is not { } index)
            return;
        var target = node.IsDirectory ? node.Id : index.GetParent(node.Id);
        if (!SelectInTree(index, target))
            _ = AnalyzeDirectoryAsync(index, target);
    }

    private void OnTreemapUp(object sender, RoutedEventArgs e)
    {
        if (Treemap.Index is not { } index || _selectedDirectory <= FileIndex.RootId)
            return;
        var parent = index.GetParent(_selectedDirectory);
        if (!SelectInTree(index, parent))
            _ = AnalyzeDirectoryAsync(index, parent);
    }

    /// <summary>Ağaçta verilen klasöre kadar açıp seçer. Klasör görünen ilk 300 öğe arasında değilse false.</summary>
    private bool SelectInTree(FileIndex index, int id)
    {
        if (SpaceTree.ItemsSource is not FolderNode[] { Length: 1 } roots)
            return false;
        var chain = new List<int>();
        for (var cur = id; cur != FileIndex.None && cur != FileIndex.RootId; cur = index.GetParent(cur))
            chain.Add(cur);
        chain.Reverse();

        var node = roots[0];
        foreach (var step in chain)
        {
            node.IsExpanded = true;
            var next = node.Children.FirstOrDefault(c => c.Id == step);
            if (next is null)
                return false;
            node = next;
        }
        node.IsSelected = true;
        return true;
    }

    private void OnDetailTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == DetailTabs && DetailTabs.SelectedItem == GrowthTab)
            _ = LoadGrowthAsync();
    }

    // ---------------------------------------------------------------- büyüyenler

    private void OnGrowthOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            _ = LoadGrowthAsync();
    }

    private async Task LoadGrowthAsync()
    {
        if (VolumeCombo.SelectedItem is not string root || AppServices.Indexes.FindVolume(root)?.Index is not { } index)
            return;
        var days = int.Parse((string)((ComboBoxItem)GrowthPeriod.SelectedItem).Tag);
        var growth = GrowthDirection.SelectedIndex == 0;
        var version = ++_growthVersion;
        GrowthInfo.Text = "Hesaplanıyor…";

        var (rows, info) = await Task.Run(() =>
        {
            var old = SnapshotStore.FindClosest(AppPaths.SnapshotDirectory, root, DateTime.UtcNow.AddDays(-days));
            if (old is null || DateTime.UtcNow - old.TakenUtc < TimeSpan.FromHours(1))
                return (new List<GrowthRow>(), "Henüz karşılaştırılacak kayıt yok. Uygulama her gün klasör boyutlarını kaydeder; birkaç gün sonra burada nelerin büyüdüğünü göreceksiniz.");
            var current = SnapshotStore.Capture(index);
            var list = GrowthAnalyzer.Compare(old, current, 200, growth)
                .Select(g => new GrowthRow(System.IO.Path.Join(root, g.RelativePath), g.OldSize, g.NewSize))
                .ToList();
            var since = old.TakenUtc.ToLocalTime();
            return (list, $"{since:g} tarihindeki kayıtla karşılaştırıldı");
        });
        // Bu arada başka bir hacim/dönem seçildiyse eski sonuç yenisinin üzerine yazılmasın.
        if (version != _growthVersion)
            return;
        GrowthList.ItemsSource = rows;
        GrowthInfo.Text = info;
    }

    private void OnGrowthDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GrowthList.SelectedItem is GrowthRow row && _treeIndex is { } index)
        {
            index.Lock.EnterReadLock();
            int id;
            try
            {
                id = index.FindPath(row.Path);
            }
            finally
            {
                index.Lock.ExitReadLock();
            }
            if (id != FileIndex.None && !SelectInTree(index, id))
                _ = AnalyzeDirectoryAsync(index, id);
        }
    }

    // ---------------------------------------------------------------- dosya türleri

    private void OnTypeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TypeList.SelectedItem is not TypeRow row || _treeIndex is not { } index || _selectedDirectory == FileIndex.None)
            return;
        string folder;
        index.Lock.EnterReadLock();
        try
        {
            folder = index.GetFullPath(_selectedDirectory);
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
        var ext = row.Key == FileCategories.NoExtension ? "file: !." : "ext:" + row.Key.TrimStart('.');
        var scope = folder.Length <= 3 ? string.Empty : $" path:\"{folder.TrimEnd('\\')}\\\"";
        AppServices.ShowSearch?.Invoke(ext + scope, true);
    }
}

/// <summary>"Büyüyenler" listesi satırı.</summary>
public sealed record GrowthRow(string Path, long OldSize, long NewSize)
{
    public string OldText => OldSize == 0 ? "—" : ByteSize.Format(OldSize);
    public string NewText => NewSize == 0 ? "silindi" : ByteSize.Format(NewSize);
    public string DeltaText => (NewSize >= OldSize ? "+" : "−") + ByteSize.Format(Math.Abs(NewSize - OldSize));
}
