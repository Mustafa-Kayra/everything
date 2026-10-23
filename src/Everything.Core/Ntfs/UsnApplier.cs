namespace Everything.Core.Ntfs;

/// <summary>USN günlüğü kayıtlarını indekse uygular.</summary>
public sealed class UsnApplier
{
    private readonly FileIndex _index;
    private readonly IUsnJournal _journal;
    private readonly Dictionary<long, int> _byReference;
    private readonly Dictionary<long, string> _pendingOldNames = new();

    public UsnApplier(FileIndex index, IUsnJournal journal)
    {
        _index = index;
        _journal = journal;
        index.Lock.EnterReadLock();
        try
        {
            _byReference = index.BuildFileReferenceMap();
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
    }

    /// <summary>Kayıtları sırayla uygular; indeksi değiştiren kayıt sayısını döndürür.</summary>
    public int Apply(IReadOnlyList<UsnRecord> records, CancellationToken cancellationToken = default)
    {
        var applied = 0;
        // Aynı dosyaya art arda gelen içerik değişikliklerini tek okumaya indir.
        var refreshed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = record.Reason;
            if ((reason & (UsnReason.FileCreate | UsnReason.FileDelete | UsnReason.RenameOldName | UsnReason.RenameNewName | UsnReason.ContentChanges)) == 0)
                continue;

            var parentPath = ResolveDirectory(record.ParentReference);
            if (parentPath is null)
                continue;
            var path = Path.Join(parentPath, record.Name);
            if (_index.GetRelativePath(path) is null)
                continue;

            if ((reason & UsnReason.FileDelete) != 0)
            {
                IndexMutations.RemovePath(_index, path);
                _byReference.Remove(record.FileReference);
                _pendingOldNames.Remove(record.FileReference);
                refreshed.Remove(path);
            }
            else if ((reason & UsnReason.RenameOldName) != 0)
            {
                _pendingOldNames[record.FileReference] = path;
                continue;
            }
            else if ((reason & UsnReason.RenameNewName) != 0)
            {
                if (_pendingOldNames.Remove(record.FileReference, out var oldPath))
                    IndexMutations.Rename(_index, oldPath, path);
                else
                    Remember(record.FileReference, IndexMutations.AddOrUpdate(_index, path));
                Remember(record.FileReference, Find(path));
                refreshed.Clear();
            }
            else
            {
                var structural = (reason & UsnReason.FileCreate) != 0;
                if (!structural && !refreshed.Add(path))
                    continue;
                Remember(record.FileReference, IndexMutations.AddOrUpdate(_index, path));
            }
            applied++;
        }

        // Eşi gelmeyen eski adlar: dosya izlenen birimin dışına taşınmış demektir.
        foreach (var (reference, oldPath) in _pendingOldNames)
        {
            IndexMutations.RemovePath(_index, oldPath);
            _byReference.Remove(reference);
            applied++;
        }
        _pendingOldNames.Clear();
        return applied;
    }

    private string? ResolveDirectory(long reference)
    {
        if (_byReference.TryGetValue(reference, out var id))
        {
            _index.Lock.EnterReadLock();
            try
            {
                if (!_index.IsDeleted(id) && _index.GetFileReference(id) == reference)
                    return _index.GetFullPath(id);
            }
            finally
            {
                _index.Lock.ExitReadLock();
            }
            _byReference.Remove(reference);
        }

        var path = _journal.ResolvePath(reference);
        if (path is null)
            return null;
        Remember(reference, Find(path));
        return path;
    }

    private int Find(string path)
    {
        _index.Lock.EnterReadLock();
        try
        {
            return _index.FindPath(path);
        }
        finally
        {
            _index.Lock.ExitReadLock();
        }
    }

    private void Remember(long reference, int id)
    {
        if (id == FileIndex.None || reference == 0)
            return;
        _index.Lock.EnterWriteLock();
        try
        {
            if (!_index.IsDeleted(id))
            {
                _index.SetFileReference(id, reference);
                _byReference[reference] = id;
            }
        }
        finally
        {
            _index.Lock.ExitWriteLock();
        }
    }
}
