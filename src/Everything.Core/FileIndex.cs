using System.Text;

namespace Everything.Core;

/// <summary>
/// Bir sürücünün (veya klasörün) tüm dosya ve klasörlerini bellekte tutan sıkıştırılmış indeks.
/// Kayıtlar paralel dizilerde saklanır; her kayıt bir tamsayı kimlikle (id) temsil edilir.
/// Kimlik 0 her zaman kök klasördür. Klasör boyutları alt ağacın toplamıdır.
/// </summary>
public sealed class FileIndex
{
    public const int RootId = 0;
    public const int None = -1;

    private const byte FlagDirectory = 1;
    private const byte FlagDeleted = 2;
    private const byte FlagReparsePoint = 4;

    public static StringComparison NameComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private string[] _names;
    private int[] _parent;
    private int[] _firstChild;
    private int[] _nextSibling;
    private long[] _size;
    private long[] _modified;
    private int[] _fileCount;
    private byte[] _flags;
    private int _count;
    private int _liveCount;

    public FileIndex(string rootPath, int capacity = 1024)
    {
        RootPath = rootPath;
        capacity = Math.Max(capacity, 16);
        _names = new string[capacity];
        _parent = new int[capacity];
        _firstChild = new int[capacity];
        _nextSibling = new int[capacity];
        _size = new long[capacity];
        _modified = new long[capacity];
        _fileCount = new int[capacity];
        _flags = new byte[capacity];

        AddEntry(None, rootPath, isDirectory: true, size: 0, modifiedTicks: 0, isReparsePoint: false);
    }

    /// <summary>İndeksi okurken (arama vb.) okuma kilidi, değiştirirken yazma kilidi alınmalıdır.</summary>
    public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.SupportsRecursion);

    public string RootPath { get; }

    /// <summary>Silinmişler dahil ayrılmış kayıt sayısı (id üst sınırı).</summary>
    public int Capacity => _count;

    /// <summary>Kök hariç, silinmemiş kayıt sayısı.</summary>
    public int Count => _liveCount - 1;

    public DateTime LastUpdatedUtc { get; internal set; } = DateTime.UtcNow;

    public string GetName(int id) => _names[id];
    public int GetParent(int id) => _parent[id];
    public long GetSize(int id) => _size[id];
    public int GetFileCount(int id) => _fileCount[id];
    public bool IsDirectory(int id) => (_flags[id] & FlagDirectory) != 0;
    public bool IsDeleted(int id) => (_flags[id] & FlagDeleted) != 0;
    public bool IsReparsePoint(int id) => (_flags[id] & FlagReparsePoint) != 0;
    public long GetModifiedTicks(int id) => _modified[id];

    public DateTime GetModifiedUtc(int id) =>
        _modified[id] <= 0 ? DateTime.MinValue : new DateTime(_modified[id], DateTimeKind.Utc);

    public int GetFirstChild(int id) => _firstChild[id];
    public int GetNextSibling(int id) => _nextSibling[id];

    public IEnumerable<int> GetChildren(int id)
    {
        for (var c = _firstChild[id]; c != None; c = _nextSibling[c])
            yield return c;
    }

    /// <summary>Kaydın tam yolunu döndürür.</summary>
    public string GetFullPath(int id)
    {
        if (id == RootId)
            return RootPath;

        var chain = new List<int>(16);
        for (var cur = id; cur != RootId && cur != None; cur = _parent[cur])
            chain.Add(cur);

        var sb = new StringBuilder(RootPath.Length + chain.Count * 16);
        sb.Append(RootPath);
        if (sb.Length > 0 && sb[^1] != Path.DirectorySeparatorChar && sb[^1] != Path.AltDirectorySeparatorChar)
            sb.Append(Path.DirectorySeparatorChar);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            sb.Append(_names[chain[i]]);
            if (i > 0)
                sb.Append(Path.DirectorySeparatorChar);
        }
        return sb.ToString();
    }

    /// <summary>Kaydın bulunduğu klasörün tam yolu.</summary>
    public string GetDirectoryPath(int id) =>
        id == RootId ? Path.GetDirectoryName(RootPath) ?? RootPath : GetFullPath(_parent[id]);

    /// <summary>Kaydı ekler ve ebeveyninin alt öğe listesine bağlar. Klasör boyutlarını güncellemez.</summary>
    public int AddEntry(int parent, string name, bool isDirectory, long size, long modifiedTicks, bool isReparsePoint)
    {
        if (_count == _names.Length)
            Grow(_count * 2);

        var id = _count++;
        _names[id] = name;
        _parent[id] = parent;
        _firstChild[id] = None;
        _size[id] = isDirectory ? 0 : size;
        _modified[id] = modifiedTicks;
        _fileCount[id] = 0;
        _flags[id] = (byte)((isDirectory ? FlagDirectory : 0) | (isReparsePoint ? FlagReparsePoint : 0));
        _nextSibling[id] = None;
        if (parent != None)
        {
            _nextSibling[id] = _firstChild[parent];
            _firstChild[parent] = id;
        }
        _liveCount++;
        return id;
    }

    /// <summary>
    /// Tam taramadan sonra klasör boyutlarını ve dosya sayılarını hesaplar.
    /// Ebeveynlerin çocuklarından önce eklendiğini varsayar (tarayıcı bunu garanti eder).
    /// </summary>
    public void ComputeAggregates()
    {
        for (var i = 0; i < _count; i++)
        {
            if (IsDirectory(i))
            {
                _size[i] = 0;
                _fileCount[i] = 0;
            }
        }

        for (var i = _count - 1; i > RootId; i--)
        {
            if (IsDeleted(i))
                continue;
            var p = _parent[i];
            _size[p] += _size[i];
            _fileCount[p] += IsDirectory(i) ? _fileCount[i] : 1;
        }
    }

    /// <summary>Tam yolu verilen kaydı bulur; yoksa <see cref="None"/>.</summary>
    public int FindPath(string fullPath)
    {
        var relative = GetRelativePath(fullPath);
        if (relative is null)
            return None;
        if (relative.Length == 0)
            return RootId;

        var cur = RootId;
        foreach (var part in relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = FindChild(cur, part);
            if (cur == None)
                return None;
        }
        return cur;
    }

    public int FindChild(int parent, string name)
    {
        for (var c = _firstChild[parent]; c != None; c = _nextSibling[c])
        {
            if (string.Equals(_names[c], name, NameComparison))
                return c;
        }
        return None;
    }

    /// <summary>Yeni bir kayıt ekler ve atalarının boyutunu/dosya sayısını günceller.</summary>
    public int AddLive(int parent, string name, bool isDirectory, long size, long modifiedTicks, bool isReparsePoint)
    {
        var id = AddEntry(parent, name, isDirectory, size, modifiedTicks, isReparsePoint);
        if (!isDirectory)
            PropagateDelta(parent, size, 1);
        return id;
    }

    /// <summary>
    /// Var olan bir alt ağacı (ör. yeni oluşturulmuş bir klasörün içeriği) ekledikten sonra
    /// o klasörün toplamlarını hesaplayıp atalarına yansıtır.
    /// </summary>
    public void RecomputeSubtree(int id)
    {
        if (!IsDirectory(id))
            return;
        var oldSize = _size[id];
        var oldCount = _fileCount[id];
        var order = PreOrder(id);
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            if (!IsDirectory(n))
                continue;
            long s = 0;
            var fc = 0;
            for (var c = _firstChild[n]; c != None; c = _nextSibling[c])
            {
                s += _size[c];
                fc += IsDirectory(c) ? _fileCount[c] : 1;
            }
            _size[n] = s;
            _fileCount[n] = fc;
        }
        if (_parent[id] != None)
            PropagateDelta(_parent[id], _size[id] - oldSize, _fileCount[id] - oldCount);
    }

    /// <summary>
    /// Ayrı taranmış bir indeksin içeriğini (kökü hariç) <paramref name="targetDirectoryId"/>
    /// altına kopyalar ve toplamları günceller.
    /// </summary>
    public void ImportSubtree(int targetDirectoryId, FileIndex source)
    {
        var map = new Dictionary<int, int> { [RootId] = targetDirectoryId };
        foreach (var id in source.PreOrder(RootId))
        {
            if (id == RootId || source.IsDeleted(id))
                continue;
            map[id] = AddEntry(map[source._parent[id]], source._names[id], source.IsDirectory(id),
                source._size[id], source._modified[id], source.IsReparsePoint(id));
        }
        RecomputeSubtree(targetDirectoryId);
    }

    /// <summary>Bir dosyanın boyutunu/tarihini günceller.</summary>
    public void UpdateFile(int id, long size, long modifiedTicks)
    {
        _modified[id] = modifiedTicks;
        if (IsDirectory(id))
            return;
        var delta = size - _size[id];
        _size[id] = size;
        if (delta != 0)
            PropagateDelta(_parent[id], delta, 0);
    }

    /// <summary>Kaydı (ve klasörse tüm alt ağacını) siler.</summary>
    public void Remove(int id)
    {
        if (id == RootId || IsDeleted(id))
            return;
        var parent = _parent[id];
        Unlink(id);
        PropagateDelta(parent, -_size[id], -(IsDirectory(id) ? _fileCount[id] : 1));
        foreach (var n in PreOrder(id))
        {
            _flags[n] |= FlagDeleted;
            _liveCount--;
        }
    }

    /// <summary>Kaydı yeniden adlandırır ve gerekirse başka bir klasöre taşır.</summary>
    public void Move(int id, int newParent, string newName)
    {
        if (id == RootId)
            return;
        _names[id] = newName;
        var oldParent = _parent[id];
        if (oldParent == newParent)
            return;

        var size = _size[id];
        var count = IsDirectory(id) ? _fileCount[id] : 1;
        Unlink(id);
        PropagateDelta(oldParent, -size, -count);
        _parent[id] = newParent;
        _nextSibling[id] = _firstChild[newParent];
        _firstChild[newParent] = id;
        PropagateDelta(newParent, size, count);
    }

    /// <summary>Verilen klasör de dahil alt ağacı ön-sırada (ebeveyn önce) döndürür.</summary>
    public List<int> PreOrder(int id)
    {
        var result = new List<int>();
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            result.Add(n);
            for (var c = _firstChild[n]; c != None; c = _nextSibling[c])
                stack.Push(c);
        }
        return result;
    }

    /// <summary>Verilen kaydın <paramref name="ancestor"/> altında olup olmadığını söyler.</summary>
    public bool IsDescendantOf(int id, int ancestor)
    {
        for (var cur = id; cur != None; cur = _parent[cur])
        {
            if (cur == ancestor)
                return true;
        }
        return false;
    }

    internal string? GetRelativePath(string fullPath)
    {
        var root = RootPath.TrimEnd(Separators);
        var path = fullPath.TrimEnd(Separators);
        if (path.Length == root.Length)
            return string.Equals(path, root, NameComparison) ? string.Empty : null;
        if (path.Length < root.Length || !path.StartsWith(root, NameComparison))
            return null;
        // "C:\Foo" kökü "C:\FooBar" ile eşleşmesin.
        if (root.Length > 0 && path[root.Length] != Path.DirectorySeparatorChar && path[root.Length] != Path.AltDirectorySeparatorChar)
            return null;
        return path[(root.Length + 1)..];
    }

    private static readonly char[] Separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    private void Unlink(int id)
    {
        var parent = _parent[id];
        if (parent == None)
            return;
        if (_firstChild[parent] == id)
        {
            _firstChild[parent] = _nextSibling[id];
        }
        else
        {
            for (var c = _firstChild[parent]; c != None; c = _nextSibling[c])
            {
                if (_nextSibling[c] == id)
                {
                    _nextSibling[c] = _nextSibling[id];
                    break;
                }
            }
        }
        _nextSibling[id] = None;
    }

    private void PropagateDelta(int dir, long sizeDelta, int countDelta)
    {
        for (var cur = dir; cur != None; cur = _parent[cur])
        {
            _size[cur] += sizeDelta;
            _fileCount[cur] += countDelta;
        }
    }

    private void Grow(int capacity)
    {
        Array.Resize(ref _names, capacity);
        Array.Resize(ref _parent, capacity);
        Array.Resize(ref _firstChild, capacity);
        Array.Resize(ref _nextSibling, capacity);
        Array.Resize(ref _size, capacity);
        Array.Resize(ref _modified, capacity);
        Array.Resize(ref _fileCount, capacity);
        Array.Resize(ref _flags, capacity);
    }
}
