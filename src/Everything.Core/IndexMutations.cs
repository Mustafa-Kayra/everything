namespace Everything.Core;

/// <summary>
/// Yol tabanlı canlı indeks güncellemeleri. Dosya sistemi durumu her seferinde yeniden okunur,
/// bu yüzden işlemler idempotenttir: aynı değişikliği iki kez uygulamak sorun çıkarmaz.
/// </summary>
internal static class IndexMutations
{
    public static void RemovePath(FileIndex index, string path)
    {
        index.Lock.EnterWriteLock();
        try
        {
            var id = index.FindPath(path);
            if (id > FileIndex.RootId)
                index.Remove(id);
        }
        finally
        {
            index.Lock.ExitWriteLock();
        }
    }

    public static void Rename(FileIndex index, string oldPath, string newPath)
    {
        index.Lock.EnterWriteLock();
        try
        {
            var oldId = index.FindPath(oldPath);
            var newParent = index.FindPath(Path.GetDirectoryName(newPath) ?? string.Empty);
            var existing = index.FindPath(newPath);
            if (oldId > FileIndex.RootId && newParent != FileIndex.None && (existing == FileIndex.None || existing == oldId))
            {
                index.Move(oldId, newParent, Path.GetFileName(newPath));
                return;
            }
            if (oldId > FileIndex.RootId)
                index.Remove(oldId);
        }
        finally
        {
            index.Lock.ExitWriteLock();
        }
        AddOrUpdate(index, newPath);
    }

    /// <summary>Yolu diskteki güncel haliyle indekse ekler/günceller; artık yoksa siler. Kaydın kimliğini döndürür.</summary>
    public static int AddOrUpdate(FileIndex index, string path, int depth = 0)
    {
        var relative = index.GetRelativePath(path);
        if (string.IsNullOrEmpty(relative) || depth > 64)
            return relative is { Length: 0 } ? FileIndex.RootId : FileIndex.None;

        if (!FileScanner.TryStat(path, out var isDir, out var size, out var modified, out var isReparse))
        {
            RemovePath(index, path);
            return FileIndex.None;
        }

        int existing;
        index.Lock.EnterReadLock();
        try
        {
            existing = index.FindPath(path);
        }
        finally
        {
            index.Lock.ExitReadLock();
        }

        if (existing != FileIndex.None)
        {
            if (index.IsDirectory(existing) == isDir)
            {
                index.Lock.EnterWriteLock();
                try
                {
                    if (!index.IsDeleted(existing))
                        index.UpdateFile(existing, size, modified);
                }
                finally
                {
                    index.Lock.ExitWriteLock();
                }
                return existing;
            }

            // Aynı adla tür değişmiş (dosya silinip klasör oluşturulmuş vb.).
            RemovePath(index, path);
        }

        // Yeni klasörün içeriğini kilit dışında tara (ör. içeri taşınmış/kopyalanmış klasörler).
        var subtree = isDir && !isReparse ? FileScanner.Scan(path) : null;

        var parentPath = Path.GetDirectoryName(path)!;
        index.Lock.EnterWriteLock();
        try
        {
            var parentId = index.FindPath(parentPath);
            if (parentId != FileIndex.None)
            {
                var current = index.FindChild(parentId, Path.GetFileName(path));
                if (current != FileIndex.None)
                    return current;
                var id = index.AddLive(parentId, Path.GetFileName(path), isDir, size, modified, isReparse);
                if (subtree is not null)
                    index.ImportSubtree(id, subtree);
                return id;
            }
        }
        finally
        {
            index.Lock.ExitWriteLock();
        }

        // Üst klasör henüz indekste yok: önce onu (içeriğiyle birlikte) ekle.
        AddOrUpdate(index, parentPath, depth + 1);
        index.Lock.EnterReadLock();
        try
        {
            return index.FindPath(path);
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
    }
}
