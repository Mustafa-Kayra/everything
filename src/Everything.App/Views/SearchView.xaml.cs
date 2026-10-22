using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Everything.App.Services;
using Everything.Core;

namespace Everything.App.Views;

public partial class SearchView : UserControl
{
    private const int MaxResults = 200_000;
    private const int MaxTextPreviewBytes = 64 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "log", "json", "xml", "csv", "ini", "cfg", "conf", "yml", "yaml", "toml", "cs", "js", "ts", "tsx", "jsx",
        "py", "java", "c", "cpp", "h", "hpp", "go", "rs", "rb", "php", "html", "htm", "css", "scss", "sql", "sh", "bat", "cmd",
        "ps1", "psm1", "reg", "gitignore", "editorconfig", "xaml", "csproj", "sln", "props", "targets", "kt", "swift", "lua", "env", "svg",
    };

    /// <summary>WPF'in (WIC) doğrudan çözebildiği resim türleri.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "bmp", "gif", "tif", "tiff", "ico", "webp", "jfif", "heic",
    };

    private readonly DispatcherTimer _searchDelay;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;
    private DateTime _lastSearch = DateTime.MinValue;
    private string _sortColumn = "Ad";
    private bool _sortDescending;
    private List<ResultItem> _results = new();

    public SearchView()
    {
        InitializeComponent();
        UiStyles.CompactList(ResultsList);
        ResultsList.ContextMenu = ItemMenu.Create(SelectedPaths, () => AppServices.IndexChangedFlag = true);

        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _ = RunSearchAsync(keepSelection: false);
        };

        Loaded += (_, _) =>
        {
            PreviewToggle.IsChecked = AppServices.Settings.ShowPreview;
            ApplyPreviewVisibility();
        };
        AppServices.Tick += (_, _) => OnTick();
        AppServices.SettingsChanged += (_, _) =>
        {
            PreviewToggle.IsChecked = AppServices.Settings.ShowPreview;
            ApplyPreviewVisibility();
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                _ = RunSearchAsync(keepSelection: true);
        };
    }

    public void FocusSearch(string? text = null)
    {
        if (text is not null)
        {
            SearchBox.Text = text;
            SearchBox.CaretIndex = text.Length;
        }
        SearchBox.Focus();
        if (text is null)
            SearchBox.SelectAll();
    }

    /// <summary>Sıralamayı değiştirir (ör. alan analizinden gelinirken boyuta göre).</summary>
    public void SetSort(string column, bool descending)
    {
        _sortColumn = column;
        _sortDescending = descending;
    }

    private IReadOnlyList<string> SelectedPaths() =>
        ResultsList.SelectedItems.OfType<ResultItem>().Select(i => i.FullPath).ToList();

    private void OnTick()
    {
        // İndeks değiştiyse (canlı güncelleme / tarama bitti) aramayı en fazla 2 sn'de bir tazele.
        if (AppServices.IndexChangedFlag)
            _indexDirty = true;
        if (_indexDirty && (DateTime.UtcNow - _lastSearch).TotalSeconds >= 2)
        {
            _indexDirty = false;
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                _ = RunSearchAsync(keepSelection: true);
        }
    }

    private bool _indexDirty;

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
        var text = SearchBox.Text;
        var selected = keepSelection ? ResultsList.SelectedItem as ResultItem : null;

        try
        {
            var (result, items) = await SearchService.RunAsync(text, MaxResults, _sortColumn, _sortDescending, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            if (!(keepSelection && SearchService.SameItems(_results, items)))
            {
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
            }

            var empty = string.IsNullOrWhiteSpace(text);
            SearchInfo.Text = empty
                ? string.Empty
                : result.Truncated
                    ? $"{result.TotalMatches:N0} sonuç ({MaxResults:N0} tanesi gösteriliyor) · {result.Elapsed.TotalMilliseconds:N0} ms"
                    : $"{result.TotalMatches:N0} sonuç · {result.Elapsed.TotalMilliseconds:N0} ms";
            if (!empty && AppServices.Indexes.GetIndexes().Count == 0)
                SearchInfo.Text = "Diskler taranıyor, birazdan sonuçlar görünecek…";
        }
        catch (OperationCanceledException)
        {
        }
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
            _sortDescending = header is "Boyut" or "Değiştirilme";
        }
        _results = SearchService.Sort(_results, _sortColumn, _sortDescending);
        ResultsList.ItemsSource = _results;
    }

    private void OnShowSearchHelp(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(Window.GetWindow(this),
            "rapor 2024            adında hem \"rapor\" hem \"2024\" geçenler\n" +
            "sarki                 Türkçe karakter gerekmez: \"Şarkı\" da bulunur\n" +
            "\"yıllık rapor\"         tam ifade\n" +
            "*.mp4  /  IMG_????.jpg   joker karakterler\n" +
            "!temp                 \"temp\" geçenleri hariç tut\n" +
            "ext:pdf;docx          uzantıya göre\n" +
            "size:>1gb  size:10mb..1gb   boyuta göre (dosyalar)\n" +
            "folder: size:>10gb    10 GB'tan büyük klasörler\n" +
            "dm:bugün  dm:dün  dm:buhafta  dm:buay  dm:son7g  dm:2024  dm:>2024-05-01\n" +
            "file: / folder:       yalnızca dosyalar / klasörler\n" +
            "path:Belgeler  veya  Belgeler\\rapor   tam yolda ara\n\n" +
            "Kısayollar: Ctrl+F arama kutusu · Enter aç · Ctrl+C yolu kopyala · Del sil · Esc temizle · F5 yeniden tara",
            "Arama ipuçları", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is ResultItem item)
            Shell.Open(item.FullPath);
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultItem item)
            return;
        if (e.Key == Key.Enter)
        {
            Shell.Open(item.FullPath);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Shell.CopyPath(string.Join(Environment.NewLine, SelectedPaths()));
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (Shell.MoveToRecycleBin(SelectedPaths(), Window.GetWindow(this)) > 0)
                AppServices.IndexChangedFlag = true;
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- önizleme

    private void OnPreviewToggled(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.ShowPreview = PreviewToggle.IsChecked == true;
        AppServices.Settings.Save();
        ApplyPreviewVisibility();
    }

    private void ApplyPreviewVisibility()
    {
        var show = AppServices.Settings.ShowPreview;
        PreviewPanel.Visibility = PreviewSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = show ? new GridLength(300) : new GridLength(0);
        PreviewColumn.MinWidth = show ? 220 : 0;
        SplitterColumn.Width = show ? new GridLength(12) : new GridLength(0);
        if (show)
            _ = UpdatePreviewAsync();
    }

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppServices.Settings.ShowPreview)
            _ = UpdatePreviewAsync();
    }

    private async Task UpdatePreviewAsync()
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        if (ResultsList.SelectedItem is not ResultItem item)
        {
            PreviewContent.Visibility = Visibility.Collapsed;
            PreviewEmpty.Visibility = Visibility.Visible;
            return;
        }

        PreviewEmpty.Visibility = Visibility.Collapsed;
        PreviewContent.Visibility = Visibility.Visible;
        PreviewName.Text = item.Name;
        PreviewType.Text = item.IsDirectory ? "Klasör" : DescribeType(item.Name);
        PreviewSize.Text = item.IsDirectory
            ? $"{item.SizeText} · {item.Index.GetFileCount(item.Id):N0} dosya"
            : $"{item.SizeText} ({item.Size:N0} bayt)";
        PreviewModified.Text = item.Modified?.ToString("f") ?? "—";
        PreviewFolder.Text = item.Folder;
        PreviewImage.Source = null;
        PreviewIcon.Source = item.Icon;
        PreviewIcon.Visibility = Visibility.Visible;
        PreviewText.Visibility = Visibility.Collapsed;
        PreviewText.Text = string.Empty;

        var path = item.FullPath;
        var ext = FileCategories.GetExtension(item.Name);

        // Metin dosyaları: ilk 64 KB.
        if (!item.IsDirectory && TextExtensions.Contains(ext) && item.Size < 50L * 1024 * 1024)
        {
            var text = await Task.Run(() => ReadTextPreview(path));
            if (cts.IsCancellationRequested)
                return;
            if (text is not null)
            {
                PreviewText.Text = text;
                PreviewText.Visibility = Visibility.Visible;
            }
        }

        // Küçük resim: resimler doğrudan çözülür, diğerleri (video, PDF…) Gezgin'den alınır.
        var thumbnail = !item.IsDirectory && ImageExtensions.Contains(ext)
            ? await Task.Run(() => DecodeImage(path, 512))
            : null;
        thumbnail ??= await ShellThumbnails.GetAsync(path, 256);
        if (cts.IsCancellationRequested || thumbnail is null)
            return;
        PreviewImage.Source = thumbnail;
        PreviewIcon.Visibility = Visibility.Collapsed;
    }

    private static System.Windows.Media.Imaging.BitmapSource? DecodeImage(string path, int maxSize)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = maxSize;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    private static string DescribeType(string name)
    {
        var ext = FileCategories.GetExtension(name);
        if (ext.Length == 0)
            return "Dosya";
        var category = FileCategories.GetCategory(ext);
        return category == FileCategories.Other ? $"{ext.ToUpperInvariant()} dosyası" : $"{category} · {ext.ToUpperInvariant()}";
    }

    private static string? ReadTextPreview(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(MaxTextPreviewBytes, stream.Length)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            // İkili dosyaları metin gibi gösterme.
            if (buffer.AsSpan(0, read).IndexOf((byte)0) >= 0 && !HasUtf16Bom(buffer))
                return null;
            using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return stream.Length > read ? text + "\n…" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasUtf16Bom(byte[] buffer) =>
        buffer.Length >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF));
}
