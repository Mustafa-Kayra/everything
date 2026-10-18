using Everything.Core;

namespace Everything.App;

/// <summary>Arama sonucu satırı. Klasör yolu ekranda göründüğünde hesaplanır.</summary>
public sealed class ResultItem
{
    private string? _folder;

    public ResultItem(FileIndex index, int id)
    {
        Index = index;
        Id = id;
        Name = index.GetName(id);
        IsDirectory = index.IsDirectory(id);
        Size = index.GetSize(id);
        var modified = index.GetModifiedUtc(id);
        Modified = modified == DateTime.MinValue ? null : modified.ToLocalTime();
    }

    public FileIndex Index { get; }
    public int Id { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public DateTime? Modified { get; }

    public string Icon => IsDirectory ? "📁" : "📄";

    public string SizeText => ByteSize.Format(Size);

    public string ModifiedText => Modified?.ToString("g") ?? string.Empty;

    public string Folder
    {
        get
        {
            if (_folder is null)
            {
                Index.Lock.EnterReadLock();
                try
                {
                    _folder = Index.GetDirectoryPath(Id);
                }
                finally
                {
                    Index.Lock.ExitReadLock();
                }
            }
            return _folder;
        }
    }

    public string FullPath => System.IO.Path.Join(Folder, Name);
}
