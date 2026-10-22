namespace Everything.Core;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

/// <summary>
/// Ağaç haritasındaki tek bir kutu. <see cref="Depth"/> kökün doğrudan alt öğeleri için 0'dır.
/// <see cref="Category"/> dosyalar için <see cref="FileCategories.GetCategory"/>, klasörler için "Klasör".
/// </summary>
public sealed record TreemapNode(int Id, string Name, bool IsDirectory, long Size, TreemapRect Rect, int Depth, string Category);

/// <summary>Kareleştirilmiş ağaç haritası yerleşimi (Bruls, Huizing, van Wijk).</summary>
public static class TreemapLayout
{
    /// <summary>
    /// Boyutları <paramref name="bounds"/> içine, en-boy oranları 1'e yakın dikdörtgenler olarak yerleştirir.
    /// <paramref name="sizes"/> büyükten küçüğe sıralı ve hepsi pozitif olmalıdır. Her boyut için bir dikdörtgen
    /// döner; dikdörtgenler sınırları tam olarak doldurur (alanlar toplamı = sınır alanı).
    /// </summary>
    public static TreemapRect[] Squarify(IReadOnlyList<double> sizes, TreemapRect bounds)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var n = sizes.Count;
        var rects = new TreemapRect[n];
        if (n == 0)
            return rects;

        double total = 0;
        for (var i = 0; i < n; i++)
        {
            if (!(sizes[i] > 0) || double.IsInfinity(sizes[i]))
                throw new ArgumentOutOfRangeException(nameof(sizes), "Boyutlar pozitif olmalıdır.");
            total += sizes[i];
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            Array.Fill(rects, new TreemapRect(bounds.X, bounds.Y, 0, 0));
            return rects;
        }

        // Boyutları alana ölçekle; böylece satır hesapları doğrudan piksel alanıyla yapılır.
        var scale = bounds.Area / total;
        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        var start = 0;
        while (start < n)
        {
            var side = Math.Min(w, h);
            var rowSum = sizes[start] * scale;
            var rowMin = rowSum;
            var rowMax = rowSum;
            var worst = Worst(rowSum, rowMin, rowMax, side);
            var end = start + 1;

            // Satıra eklemek en kötü en-boy oranını bozmadıkça öğe eklemeye devam et.
            while (end < n)
            {
                var a = sizes[end] * scale;
                var newSum = rowSum + a;
                var newMin = Math.Min(rowMin, a);
                var newMax = Math.Max(rowMax, a);
                var newWorst = Worst(newSum, newMin, newMax, side);
                if (newWorst > worst)
                    break;
                rowSum = newSum;
                rowMin = newMin;
                rowMax = newMax;
                worst = newWorst;
                end++;
            }

            var last = end == n;
            if (w >= h)
            {
                // Satır sol kenara dikey bir sütun olarak yerleşir.
                var thickness = last ? w : Math.Min(w, rowSum / h);
                var pos = y;
                for (var i = start; i < end; i++)
                {
                    var extent = i == end - 1 ? y + h - pos : sizes[i] * scale / thickness;
                    rects[i] = new TreemapRect(x, pos, thickness, extent);
                    pos += extent;
                }
                x += thickness;
                w -= thickness;
            }
            else
            {
                // Satır üst kenara yatay olarak yerleşir.
                var thickness = last ? h : Math.Min(h, rowSum / w);
                var pos = x;
                for (var i = start; i < end; i++)
                {
                    var extent = i == end - 1 ? x + w - pos : sizes[i] * scale / thickness;
                    rects[i] = new TreemapRect(pos, y, extent, thickness);
                    pos += extent;
                }
                y += thickness;
                h -= thickness;
            }
            start = end;
        }
        return rects;
    }

    /// <summary>Satırdaki en kötü en-boy oranı: max(s²·max/sum², sum²/(s²·min)).</summary>
    private static double Worst(double sum, double min, double max, double side)
    {
        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }
}

/// <summary>İndeksin bir alt ağacından iç içe ağaç haritası üretir. Çağıranın okuma kilidi almasına gerek yoktur.</summary>
public static class TreemapBuilder
{
    public const string DirectoryCategory = "Klasör";

    /// <summary>Klasör kutusunun üstünde ad şeridine yer ayrılıp ayrılmadığı (çizim tarafı aynı kuralı kullanmalı).</summary>
    public static bool ShowsHeader(TreemapRect rect, double headerHeight) =>
        headerHeight > 0 && rect.Height > headerHeight * 3 && rect.Width > 60;

    /// <summary>
    /// <paramref name="rootId"/> altındaki ağacın iç içe haritası (kökün kendisi dönmez). Klasörler çerçeve olarak
    /// döner; alt öğeleri klasör kutusunun <paramref name="padding"/> kadar küçültülmüş (ve yeterince büyükse
    /// üstte <paramref name="headerHeight"/> kadar ad şeridi bırakılmış) iç alanına yerleşir. Alanı
    /// <paramref name="minArea"/>'dan küçük kutular çizilmez ve içine inilmez; boyutu 0 olanlar atlanır.
    /// Kutular büyükten küçüğe işlenir, böylece <paramref name="maxNodes"/> sınırına ulaşıldığında
    /// büyük öğeler her zaman haritada yer alır. Sonuç ebeveynler çocuklarından önce gelecek şekilde sıralıdır.
    /// </summary>
    public static List<TreemapNode> Build(
        FileIndex index,
        int rootId,
        TreemapRect bounds,
        int maxNodes = 8000,
        double minArea = 16,
        double padding = 2,
        double headerHeight = 14)
    {
        var result = new List<TreemapNode>();
        if (maxNodes <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return result;

        index.Lock.EnterReadLock();
        try
        {
            if (index.IsDeleted(rootId) || !index.IsDirectory(rootId))
                return result;

            // En büyük kutu önce: öncelik = -alan.
            var queue = new PriorityQueue<(int Id, TreemapRect Rect, int Depth), double>();
            EnqueueChildren(index, rootId, bounds, 0, minArea, queue);

            while (queue.Count > 0 && result.Count < maxNodes)
            {
                var (id, rect, depth) = queue.Dequeue();
                var name = index.GetName(id);
                var isDir = index.IsDirectory(id);
                var category = isDir ? DirectoryCategory : FileCategories.GetCategory(FileCategories.GetExtension(name));
                result.Add(new TreemapNode(id, name, isDir, index.GetSize(id), rect, depth, category));

                if (!isDir)
                    continue;
                var top = padding + (ShowsHeader(rect, headerHeight) ? headerHeight : 0);
                var inner = new TreemapRect(rect.X + padding, rect.Y + top, rect.Width - 2 * padding, rect.Height - top - padding);
                if (inner.Width > 0 && inner.Height > 0 && inner.Area >= minArea)
                    EnqueueChildren(index, id, inner, depth + 1, minArea, queue);
            }
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
        return result;
    }

    private static void EnqueueChildren(
        FileIndex index,
        int directoryId,
        TreemapRect area,
        int depth,
        double minArea,
        PriorityQueue<(int Id, TreemapRect Rect, int Depth), double> queue)
    {
        var children = new List<(int Id, long Size)>();
        for (var c = index.GetFirstChild(directoryId); c != FileIndex.None; c = index.GetNextSibling(c))
        {
            if (!index.IsDeleted(c) && index.GetSize(c) > 0)
                children.Add((c, index.GetSize(c)));
        }
        if (children.Count == 0)
            return;

        children.Sort((a, b) => b.Size.CompareTo(a.Size));
        var sizes = new double[children.Count];
        for (var i = 0; i < sizes.Length; i++)
            sizes[i] = children[i].Size;

        var rects = TreemapLayout.Squarify(sizes, area);
        for (var i = 0; i < rects.Length; i++)
        {
            // Sıralı olduğundan ilk küçük kutudan sonrakiler de küçüktür.
            if (rects[i].Area < minArea)
                break;
            queue.Enqueue((children[i].Id, rects[i], depth), -rects[i].Area);
        }
    }
}
