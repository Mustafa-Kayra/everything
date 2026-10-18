namespace Everything.Core;

public sealed record ChildUsage(int Id, string Name, bool IsDirectory, long Size, int FileCount);

public sealed record TypeUsage(string Key, string Category, long Size, int FileCount);

/// <summary>İndeks üzerinden disk alanı analizleri. Çağıranın okuma kilidi almasına gerek yoktur.</summary>
public static class SpaceAnalyzer
{
    /// <summary>Bir klasörün doğrudan alt öğeleri, büyükten küçüğe.</summary>
    public static List<ChildUsage> GetChildren(FileIndex index, int directoryId)
    {
        index.Lock.EnterReadLock();
        try
        {
            var list = new List<ChildUsage>();
            for (var c = index.GetFirstChild(directoryId); c != FileIndex.None; c = index.GetNextSibling(c))
            {
                if (index.IsDeleted(c))
                    continue;
                var isDir = index.IsDirectory(c);
                list.Add(new ChildUsage(c, index.GetName(c), isDir, index.GetSize(c), isDir ? index.GetFileCount(c) : 1));
            }
            list.Sort((a, b) => b.Size.CompareTo(a.Size));
            return list;
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
    }

    /// <summary>Alt ağaçtaki en büyük <paramref name="count"/> dosya, büyükten küçüğe.</summary>
    public static List<int> GetLargestFiles(FileIndex index, int directoryId, int count, CancellationToken cancellationToken = default)
    {
        index.Lock.EnterReadLock();
        try
        {
            var heap = new PriorityQueue<int, long>(count + 1);
            var visited = 0;
            foreach (var id in EnumerateSubtree(index, directoryId))
            {
                if ((++visited & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (index.IsDirectory(id))
                    continue;
                var size = index.GetSize(id);
                if (heap.Count < count)
                {
                    heap.Enqueue(id, size);
                }
                else if (heap.TryPeek(out _, out var smallest) && size > smallest)
                {
                    heap.DequeueEnqueue(id, size);
                }
            }

            var result = new List<int>(heap.Count);
            while (heap.Count > 0)
                result.Add(heap.Dequeue());
            result.Reverse();
            return result;
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
    }

    /// <summary>Alt ağaçtaki dosyaları uzantıya göre gruplar, büyükten küçüğe.</summary>
    public static List<TypeUsage> GetUsageByExtension(FileIndex index, int directoryId, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, (long Size, int Count)>(StringComparer.Ordinal);
        index.Lock.EnterReadLock();
        try
        {
            var visited = 0;
            foreach (var id in EnumerateSubtree(index, directoryId))
            {
                if ((++visited & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (index.IsDirectory(id))
                    continue;
                var ext = FileCategories.GetExtension(index.GetName(id));
                map.TryGetValue(ext, out var acc);
                map[ext] = (acc.Size + index.GetSize(id), acc.Count + 1);
            }
        }
        finally
        {
            index.Lock.ExitReadLock();
        }

        return map
            .Select(kv => new TypeUsage(
                kv.Key.Length == 0 ? FileCategories.NoExtension : "." + kv.Key,
                FileCategories.GetCategory(kv.Key),
                kv.Value.Size,
                kv.Value.Count))
            .OrderByDescending(t => t.Size)
            .ToList();
    }

    /// <summary>Uzantı dökümünü kategorilere (Video, Resim, …) toplar.</summary>
    public static List<TypeUsage> GroupByCategory(IEnumerable<TypeUsage> byExtension) =>
        byExtension
            .GroupBy(t => t.Category)
            .Select(g => new TypeUsage(g.Key, g.Key, g.Sum(t => t.Size), g.Sum(t => t.FileCount)))
            .OrderByDescending(t => t.Size)
            .ToList();

    private static IEnumerable<int> EnumerateSubtree(FileIndex index, int root)
    {
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n != root)
                yield return n;
            for (var c = index.GetFirstChild(n); c != FileIndex.None; c = index.GetNextSibling(c))
            {
                if (!index.IsDeleted(c))
                    stack.Push(c);
            }
        }
    }
}
