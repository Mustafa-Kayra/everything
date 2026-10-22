using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Everything.Core;

namespace Everything.App.Views;

/// <summary>
/// Kategoriye göre renklendirilmiş iç içe treemap. Klasörler çerçeve + başlık, dosyalar dolu
/// kutular olarak çizilir. Düzen arka planda hesaplanır; boyut değişince yeniden kurulur.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double HeaderHeight = 16;
    private readonly DispatcherTimer _relayout;
    private FileIndex? _index;
    private int _rootId = FileIndex.None;
    private List<TreemapNode> _nodes = new();
    private TreemapNode? _hover;
    private CancellationTokenSource? _cts;
    private Size _builtFor;

    public TreemapControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        _relayout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _relayout.Tick += (_, _) =>
        {
            _relayout.Stop();
            Rebuild();
        };
    }

    /// <summary>Fare altındaki öğe değişti (null = boş alan).</summary>
    public event EventHandler<TreemapNode?>? HoverChanged;

    /// <summary>Bir klasöre çift tıklandı: içine gir.</summary>
    public event EventHandler<TreemapNode>? NodeOpened;

    /// <summary>Bir öğeye tıklandı.</summary>
    public event EventHandler<TreemapNode>? NodeClicked;

    public FileIndex? Index => _index;
    public int RootId => _rootId;

    public void Show(FileIndex index, int rootId)
    {
        if (_index == index && _rootId == rootId)
            return;
        _index = index;
        _rootId = rootId;
        Rebuild();
    }

    public void Refresh() => Rebuild();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _relayout.Stop();
        _relayout.Start();
    }

    private async void Rebuild()
    {
        _cts?.Cancel();
        var index = _index;
        var root = _rootId;
        var size = RenderSize;
        if (index is null || root == FileIndex.None || size.Width < 20 || size.Height < 20)
        {
            _nodes = new List<TreemapNode>();
            InvalidateVisual();
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var nodes = await Task.Run(() => TreemapBuilder.Build(index, root, new TreemapRect(0, 0, size.Width, size.Height), maxNodes: 6000, minArea: 20, padding: 2, headerHeight: HeaderHeight));
        if (cts.IsCancellationRequested)
            return;
        _nodes = nodes;
        _builtFor = size;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Boş alanda da fare olayları gelsin.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_nodes.Count == 0)
            return;

        var dark = TreemapPalette.IsDarkTheme();
        var frameFill = (Brush?)TryFindResource("SubtleFillColorSecondaryBrush") ?? Brushes.Transparent;
        var framePen = new Pen((Brush?)TryFindResource("DividerStrokeColorDefaultBrush") ?? Brushes.Gray, 1);
        framePen.Freeze();
        var textBrush = (Brush?)TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // Önce klasör çerçeveleri (büyükten küçüğe), sonra dosyalar.
        foreach (var node in _nodes)
        {
            if (!node.IsDirectory)
                continue;
            var r = ToRect(node.Rect);
            dc.DrawRectangle(frameFill, framePen, r);
            if (TreemapBuilder.ShowsHeader(node.Rect, HeaderHeight))
                DrawLabel(dc, node.Name, r.X + 4, r.Y + 1, r.Width - 8, textBrush, typeface, 11, dpi);
        }

        foreach (var node in _nodes)
        {
            if (node.IsDirectory)
                continue;
            var r = ToRect(node.Rect);
            // 1 piksellik içe çekme: komşu kutular arasında 2 px boşluk kalır.
            r.Inflate(-1, -1);
            if (r.Width <= 0 || r.Height <= 0)
                continue;
            var color = TreemapPalette.ColorFor(node.Category, dark);
            dc.DrawRoundedRectangle(TreemapPalette.Brush(color), null, r, 2, 2);
            if (r.Width > 70 && r.Height > 18)
                DrawLabel(dc, node.Name, r.X + 4, r.Y + 2, r.Width - 8, TreemapPalette.TextOn(color), typeface, 11, dpi);
        }

        if (_hover is { } hover)
        {
            var r = ToRect(hover.Rect);
            var pen = new Pen((Brush?)TryFindResource("TextFillColorPrimaryBrush") ?? Brushes.Black, 2);
            pen.Freeze();
            dc.DrawRectangle(null, pen, r);
        }
    }

    private Rect ToRect(TreemapRect r)
    {
        // Pencere boyutu değişip yeni düzen gelene kadar eskisini ölçekle.
        var sx = _builtFor.Width > 0 ? RenderSize.Width / _builtFor.Width : 1;
        var sy = _builtFor.Height > 0 ? RenderSize.Height / _builtFor.Height : 1;
        return new Rect(r.X * sx, r.Y * sy, Math.Max(0, r.Width * sx), Math.Max(0, r.Height * sy));
    }

    private static void DrawLabel(DrawingContext dc, string text, double x, double y, double maxWidth, Brush brush, Typeface typeface, double size, double dpi)
    {
        if (maxWidth < 12)
            return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, dpi)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(formatted, new Point(x, y));
    }

    private TreemapNode? HitTest(Point p)
    {
        // Düğümler alana göre büyükten küçüğe sıralı: en küçük (en derin) eşleşmeyi bul.
        for (var i = _nodes.Count - 1; i >= 0; i--)
        {
            if (ToRect(_nodes[i].Rect).Contains(p))
                return _nodes[i];
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            InvalidateVisual();
            HoverChanged?.Invoke(this, hit);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
        HoverChanged?.Invoke(this, null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } node)
            NodeClicked?.Invoke(this, node);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2 && HitTest(e.GetPosition(this)) is { } node)
        {
            // Dosyaya çift tıklanırsa bulunduğu klasöre gir.
            NodeOpened?.Invoke(this, node);
            e.Handled = true;
        }
    }
}

/// <summary>
/// Treemap kategori renkleri: doğrulanmış 8 renklik kategorik palet (açık/koyu tema için ayrı tonlar),
/// sabit sırayla atanır. Kalan kategoriler nötr gri "Diğer"e katlanır.
/// </summary>
public static class TreemapPalette
{
    public sealed record Entry(string Label, string[] Categories, Color Light, Color Dark);

    public static IReadOnlyList<Entry> Entries { get; } = new[]
    {
        new Entry("Video", new[] { "Video" }, Hex("#2a78d6"), Hex("#3987e5")),
        new Entry("Resim", new[] { "Resim" }, Hex("#eb6834"), Hex("#d95926")),
        new Entry("Ses", new[] { "Ses" }, Hex("#1baf7a"), Hex("#199e70")),
        new Entry("Belge", new[] { "Belge" }, Hex("#eda100"), Hex("#c98500")),
        new Entry("Arşiv / Disk görüntüsü", new[] { "Arşiv", "Disk görüntüsü" }, Hex("#e87ba4"), Hex("#d55181")),
        new Entry("Program", new[] { "Program" }, Hex("#008300"), Hex("#008300")),
        new Entry("Oyun / Paket", new[] { "Oyun / Paket" }, Hex("#4a3aa7"), Hex("#9085e9")),
        new Entry("Geçici / Önbellek", new[] { "Geçici / Önbellek" }, Hex("#e34948"), Hex("#e66767")),
        new Entry("Diğer", Array.Empty<string>(), Hex("#a3a29d"), Hex("#6b6a66")),
    };

    private static readonly Dictionary<Color, SolidColorBrush> Brushes = new();

    public static Color ColorFor(string category, bool dark)
    {
        foreach (var entry in Entries)
        {
            if (Array.IndexOf(entry.Categories, category) >= 0)
                return dark ? entry.Dark : entry.Light;
        }
        var other = Entries[^1];
        return dark ? other.Dark : other.Light;
    }

    public static SolidColorBrush Brush(Color color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();
            Brushes[color] = brush;
        }
        return brush;
    }

    /// <summary>Renkli kutu üstündeki etiket için okunur metin rengi.</summary>
    public static Brush TextOn(Color background)
    {
        var luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255;
        return luminance > 0.6 ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
    }

    /// <summary>Fluent teması koyu modda mı (birincil metin rengi açıksa koyudur).</summary>
    public static bool IsDarkTheme() =>
        Application.Current.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush { Color: var c } && c.R + c.G + c.B > 382;

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
