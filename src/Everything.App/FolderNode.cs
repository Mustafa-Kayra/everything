using System.Collections.ObjectModel;
using System.ComponentModel;
using Everything.Core;

namespace Everything.App;

/// <summary>Alan analizi ağacındaki bir düğüm. Alt öğeler genişletildiğinde yüklenir.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private const int MaxChildren = 300;
    private static readonly FolderNode Placeholder = new();
    private bool _isExpanded;
    private bool _isSelected;
    private bool _loaded;

    private FolderNode()
    {
        Name = "Yükleniyor…";
    }

    public FolderNode(FileIndex index, int id, string name, bool isDirectory, long size, int fileCount, long parentSize)
    {
        Index = index;
        Id = id;
        Name = name;
        IsDirectory = isDirectory;
        Size = size;
        FileCount = fileCount;
        Percent = parentSize > 0 ? 100.0 * size / parentSize : 100;
        if (isDirectory && (index.GetFirstChild(id) != FileIndex.None))
            Children.Add(Placeholder);
    }

    /// <summary>"… 1.234 öğe daha" satırı.</summary>
    private FolderNode(string name, long size, long parentSize)
    {
        Name = name;
        Size = size;
        Percent = parentSize > 0 ? 100.0 * size / parentSize : 0;
        IsAggregate = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileIndex? Index { get; }
    public int Id { get; } = FileIndex.None;
    public string Name { get; }
    public bool IsDirectory { get; }
    public bool IsAggregate { get; }
    public long Size { get; }
    public int FileCount { get; }
    public double Percent { get; }

    public string Icon => IsAggregate ? Glyphs.More : IsDirectory ? Glyphs.Folder : Glyphs.Document;
    public string SizeText => ByteSize.Format(Size);
    public string PercentText => Percent >= 99.95 ? "100 %" : Percent.ToString("0.0") + " %";
    public string FileCountText => IsDirectory ? FileCount.ToString("N0") + " dosya" : string.Empty;

    public ObservableCollection<FolderNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            if (value)
                EnsureChildren();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string? GetFullPath()
    {
        if (Index is null || Id == FileIndex.None)
            return null;
        Index.Lock.EnterReadLock();
        try
        {
            return Index.IsDeleted(Id) ? null : Index.GetFullPath(Id);
        }
        finally
        {
            Index.Lock.ExitReadLock();
        }
    }

    public static FolderNode CreateRoot(FileIndex index)
    {
        index.Lock.EnterReadLock();
        try
        {
            var size = index.GetSize(FileIndex.RootId);
            return new FolderNode(index, FileIndex.RootId, index.RootPath, true, size, index.GetFileCount(FileIndex.RootId), size);
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
    }

    private void EnsureChildren()
    {
        if (_loaded || Index is null)
            return;
        _loaded = true;
        Children.Clear();

        var children = SpaceAnalyzer.GetChildren(Index, Id);
        foreach (var c in children.Take(MaxChildren))
            Children.Add(new FolderNode(Index, c.Id, c.Name, c.IsDirectory, c.Size, c.FileCount, Size));

        if (children.Count > MaxChildren)
        {
            var rest = children.Skip(MaxChildren).ToList();
            Children.Add(new FolderNode($"… {rest.Count:N0} öğe daha", rest.Sum(r => r.Size), Size));
        }
    }
}
