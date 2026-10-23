using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Everything.App.Services;
using Everything.Core;

namespace Everything.App.Views;

public partial class DuplicatesView : UserControl
{
    private const string AllDrives = "Tüm diskler";
    private const string PickFolder = "Klasör seç…";

    private readonly ObservableCollection<DuplicateGroupRow> _groups = new();
    private CancellationTokenSource? _cts;
    private string? _customFolder;

    public DuplicatesView()
    {
        InitializeComponent();
        GroupTree.ItemsSource = _groups;
        GroupTree.ContextMenu = ItemMenu.Create(() => GroupTree.SelectedItem is DuplicateFileRow row ? new[] { row.Path } : Array.Empty<string>());
        MinSizeCombo.SelectedIndex = AppServices.Settings.DuplicateMinSize switch
        {
            <= 102400 => 0,
            <= 1048576 => 1,
            <= 10485760 => 2,
            _ => 3,
        };
    }

    public void Initialize(IReadOnlyList<string> roots)
    {
        _roots = roots;
        RebuildScopes(AllDrives);
    }

    private IReadOnlyList<string> _roots = Array.Empty<string>();

    private void RebuildScopes(string selected)
    {
        var items = new List<string> { AllDrives };
        items.AddRange(_roots);
        if (_customFolder is not null)
            items.Add(_customFolder);
        items.Add(PickFolder);
        ScopeCombo.ItemsSource = items;
        ScopeCombo.SelectedItem = selected;
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScopeCombo.SelectedItem as string != PickFolder)
            return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Kopyaların aranacağı klasör" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _customFolder = dialog.FolderName;
            RebuildScopes(_customFolder);
        }
        else
        {
            ScopeCombo.SelectedIndex = 0;
        }
    }

    private List<(FileIndex Index, int ScopeId)> ResolveScopes()
    {
        var selected = ScopeCombo.SelectedItem as string ?? AllDrives;
        var scopes = new List<(FileIndex, int)>();
        foreach (var index in AppServices.Indexes.GetIndexes())
        {
            if (selected == AllDrives)
            {
                scopes.Add((index, FileIndex.RootId));
                continue;
            }
            index.Lock.EnterReadLock();
            try
            {
                var id = index.FindPath(selected);
                if (id != FileIndex.None)
                    scopes.Add((index, id));
            }
            finally
            {
                index.Lock.ExitReadLock();
            }
        }
        return scopes;
    }

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        var scopes = ResolveScopes();
        if (scopes.Count == 0)
        {
            StatusText.Text = "Seçilen konum henüz indekslenmedi.";
            return;
        }

        var minSize = long.Parse((string)((ComboBoxItem)MinSizeCombo.SelectedItem).Tag);
        AppServices.Settings.DuplicateMinSize = minSize;
        AppServices.Settings.Save();

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var excluded = new List<string> { windows };
        foreach (var root in AppServices.Roots)
        {
            excluded.Add(Path.Combine(root, "$Recycle.Bin"));
            excluded.Add(Path.Combine(root, "System Volume Information"));
        }

        _cts = new CancellationTokenSource();
        FindButtonText.Text = "Durdur";
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        ActionBar.IsEnabled = false;
        _groups.Clear();
        var progress = new Progress<DuplicateProgress>(p =>
        {
            Progress.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
            StatusText.Text = p.Total > 0 ? $"{p.Stage}…  {Progress.Value:P0}" : p.Stage + "…";
        });

        try
        {
            var options = new DuplicateOptions
            {
                MinSize = minSize,
                ExcludedPaths = excluded,
                FileIdentity = FileIdentity.Get,
                MaxDegreeOfParallelism = 4,
            };
            var token = _cts.Token;
            var groups = await Task.Run(() => DuplicateFinder.Find(scopes, options, progress, token), token);
            foreach (var group in groups)
                _groups.Add(new DuplicateGroupRow(group, OnSelectionChanged));
            var wasted = groups.Sum(g => g.WastedBytes);
            StatusText.Text = groups.Count == 0
                ? "Kopya dosya bulunamadı."
                : $"{groups.Count:N0} grup, {groups.Sum(g => g.Files.Count):N0} dosya · silinebilecek kopyalar {ByteSize.Format(wasted)} yer kaplıyor. Silmek istediklerinizi işaretleyin.";
            ActionBar.IsEnabled = groups.Count > 0;
            UpdateDeleteButton();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Durduruldu.";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            FindButtonText.Text = "Kopyaları bul";
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSelectionChanged(DuplicateFileRow row)
    {
        // Bir gruptaki tüm kopyalar silinemez: en az biri kalmalı.
        var group = _groups.FirstOrDefault(g => g.Files.Contains(row));
        if (group is not null && row.IsSelected && group.Files.All(f => f.IsSelected))
        {
            row.IsSelected = false;
            StatusText.Text = "Her gruptan en az bir dosya kalmalı; hepsini seçemezsiniz.";
        }
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton()
    {
        var selected = _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        DeleteButton.IsEnabled = selected.Count > 0;
        DeleteButtonText.Text = selected.Count == 0
            ? "Seçilenleri Geri Dönüşüm Kutusu'na taşı"
            : $"{selected.Count:N0} dosyayı Geri Dönüşüm Kutusu'na taşı ({ByteSize.Format(selected.Sum(f => f.Size))})";
    }

    private void AutoSelect(bool keepNewest)
    {
        foreach (var group in _groups)
        {
            var keep = keepNewest
                ? group.Files.MaxBy(f => f.Modified)
                : group.Files.MinBy(f => f.Modified);
            foreach (var file in group.Files)
                file.SetSelectedSilently(file != keep);
        }
        UpdateDeleteButton();
    }

    private void OnKeepNewest(object sender, RoutedEventArgs e) => AutoSelect(keepNewest: true);

    private void OnKeepOldest(object sender, RoutedEventArgs e) => AutoSelect(keepNewest: false);

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var file in _groups.SelectMany(g => g.Files))
            file.SetSelectedSilently(false);
        UpdateDeleteButton();
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        var selected = _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        if (_groups.Any(g => g.Files.All(f => f.IsSelected)))
        {
            StatusText.Text = "Her gruptan en az bir dosya kalmalı.";
            return;
        }
        if (Shell.MoveToRecycleBin(selected.Select(f => f.Path).ToList(), Window.GetWindow(this)) == 0)
            return;

        foreach (var group in _groups.ToList())
        {
            foreach (var file in group.Files.Where(f => !File.Exists(f.Path)).ToList())
                group.Files.Remove(file);
            if (group.Files.Count < 2)
                _groups.Remove(group);
            else
                group.Refresh();
        }
        AppServices.IndexChangedFlag = true;
        StatusText.Text = $"{selected.Count:N0} dosya Geri Dönüşüm Kutusu'na taşındı ({ByteSize.Format(selected.Sum(f => f.Size))}).";
        UpdateDeleteButton();
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GroupTree.SelectedItem is DuplicateFileRow row)
            Shell.Open(row.Path);
    }
}

public sealed class DuplicateGroupRow : INotifyPropertyChanged
{
    public DuplicateGroupRow(DuplicateGroup group, Action<DuplicateFileRow> onSelectionChanged)
    {
        Size = group.Size;
        foreach (var file in group.Files)
            Files.Add(new DuplicateFileRow(file, onSelectionChanged));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Size { get; }
    public ObservableCollection<DuplicateFileRow> Files { get; } = new();
    public string Title => $"{Files.Count} × {ByteSize.Format(Size)}";
    public string Subtitle => $"{ByteSize.Format(Size * (Files.Count - 1))} fazladan · {Files[0].Name}";

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
    }
}

public sealed class DuplicateFileRow : INotifyPropertyChanged
{
    private readonly Action<DuplicateFileRow> _onSelectionChanged;
    private bool _isSelected;
    private ImageSource? _icon;

    public DuplicateFileRow(DuplicateFile file, Action<DuplicateFileRow> onSelectionChanged)
    {
        _onSelectionChanged = onSelectionChanged;
        Path = file.Path;
        Size = file.Size;
        Modified = file.ModifiedUtc.ToLocalTime();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public string ModifiedText => Modified.ToString("g");
    public ImageSource? Icon => _icon ??= FileIcons.Get(Path, isDirectory: false);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
            _onSelectionChanged(this);
        }
    }

    public void SetSelectedSilently(bool value)
    {
        _isSelected = value;
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
    }
}
