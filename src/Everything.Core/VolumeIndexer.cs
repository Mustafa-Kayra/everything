using System.Threading.Channels;

namespace Everything.Core;

public enum IndexState
{
    NotStarted,
    Loading,
    Scanning,
    Ready,
    Failed,
}

/// <summary>
/// Tek bir kök klasörün (ör. "C:\") indeksini yönetir: önbellekten yükler, tam tarama yapar
/// ve <see cref="FileSystemWatcher"/> ile değişiklikleri canlı olarak indekse işler.
/// </summary>
public sealed class VolumeIndexer : IDisposable
{
    private enum ChangeKind { CreatedOrChanged, Deleted, Renamed }

    private readonly record struct Change(ChangeKind Kind, string Path, string? OldPath = null);

    private readonly string? _cacheFile;
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;
    private Task? _consumer;
    private volatile FileIndex? _index;
    private int _rescanRequested;
    private int _pending;
    private bool _dirty;

    public VolumeIndexer(string rootPath, string? cacheDirectory = null)
    {
        RootPath = rootPath;
        if (cacheDirectory is not null)
            _cacheFile = Path.Combine(cacheDirectory, CacheFileName(rootPath));
    }

    public string RootPath { get; }

    public FileIndex? Index => _index;

    public IndexState State { get; private set; } = IndexState.NotStarted;

    public int ItemsScanned { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Durum veya tarama ilerlemesi değişti.</summary>
    public event EventHandler? StateChanged;

    /// <summary>İndeks değişti (canlı güncelleme ya da yeni tarama).</summary>
    public event EventHandler? IndexChanged;

    /// <summary>Önbelleği yükler (varsa), izlemeyi başlatır ve arka planda tam tarama yapar.</summary>
    public async Task StartAsync()
    {
        SetState(IndexState.Loading);
        if (_cacheFile is not null)
        {
            var cached = await Task.Run(() => IndexSerializer.TryLoad(_cacheFile)).ConfigureAwait(false);
            if (cached is not null && string.Equals(cached.RootPath, RootPath, FileIndex.NameComparison))
            {
                _index = cached;
                ItemsScanned = cached.Count;
                IndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        StartWatcher();
        _consumer = Task.Run(ConsumeChangesAsync);
        await RescanAsync().ConfigureAwait(false);
    }

    /// <summary>Tüm ağacı yeniden tarar. Tarama süresince gelen değişiklikler sonra uygulanır.</summary>
    public async Task RescanAsync()
    {
        await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _rescanRequested, 0);
            SetState(IndexState.Scanning);
            var progressSink = new SynchronousProgress<ScanProgress>(p =>
            {
                ItemsScanned = p.ItemsFound;
                StateChanged?.Invoke(this, EventArgs.Empty);
            });

            var fresh = await Task.Run(() => FileScanner.Scan(RootPath, progressSink, _cts.Token), _cts.Token).ConfigureAwait(false);
            _index = fresh;
            ItemsScanned = fresh.Count;
            _dirty = false;
            SetState(IndexState.Ready);
            IndexChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            SetState(IndexState.Failed);
            return;
        }
        finally
        {
            _gate.Release();
        }

        TrySaveCache();
    }

    /// <summary>İndeksi önbellek dosyasına yazar (hata olursa sessizce geçer).</summary>
    public void TrySaveCache()
    {
        var index = _index;
        if (_cacheFile is null || index is null)
            return;
        try
        {
            IndexSerializer.Save(index, _cacheFile);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public bool HasUnsavedChanges => _dirty;

    /// <summary>Bekleyen tüm canlı değişiklikler işlenene kadar bekler (testler için).</summary>
    public async Task FlushAsync()
    {
        while (Volatile.Read(ref _pending) > 0)
            await Task.Delay(20).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher?.Dispose();
        _changes.Writer.TryComplete();
    }

    internal static string CacheFileName(string rootPath)
    {
        var safe = new string(rootPath.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (safe.Length == 0)
            safe = "root";
        return $"{safe}-{(uint)StableHash(rootPath):x8}.evix";
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in s.ToUpperInvariant())
                h = (h ^ c) * 16777619;
            return h;
        }
    }

    private void SetState(IndexState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartWatcher()
    {
        try
        {
            var watcher = new FileSystemWatcher(RootPath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, e) => Enqueue(new Change(ChangeKind.CreatedOrChanged, e.FullPath));
            watcher.Changed += (_, e) => Enqueue(new Change(ChangeKind.CreatedOrChanged, e.FullPath));
            watcher.Deleted += (_, e) => Enqueue(new Change(ChangeKind.Deleted, e.FullPath));
            watcher.Renamed += (_, e) => Enqueue(new Change(ChangeKind.Renamed, e.FullPath, e.OldFullPath));
            watcher.Error += (_, _) => RequestRescan();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // İzleme desteklenmiyorsa (ör. bazı ağ/USB sürücüler) yalnızca tarama ile çalış.
        }
    }

    private void Enqueue(Change change)
    {
        Interlocked.Increment(ref _pending);
        if (!_changes.Writer.TryWrite(change))
            Interlocked.Decrement(ref _pending);
    }

    private void RequestRescan()
    {
        if (Interlocked.Exchange(ref _rescanRequested, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                    await RescanAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }

    private async Task ConsumeChangesAsync()
    {
        var reader = _changes.Reader;
        var batch = new List<Change>();
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                // Olayların birikmesine kısa bir süre izin ver; aynı yol için tekrarları birleştir.
                await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    batch.Clear();
                    while (batch.Count < 10_000 && reader.TryRead(out var change))
                        batch.Add(change);
                    Interlocked.Add(ref _pending, -batch.Count);

                    var index = _index;
                    if (index is null)
                        continue;

                    string? lastCreated = null;
                    foreach (var change in batch)
                    {
                        if (change.Kind == ChangeKind.CreatedOrChanged)
                        {
                            if (string.Equals(lastCreated, change.Path, StringComparison.Ordinal))
                                continue;
                            lastCreated = change.Path;
                        }
                        else
                        {
                            lastCreated = null;
                        }
                        Apply(index, change);
                    }
                    index.LastUpdatedUtc = DateTime.UtcNow;
                    _dirty = true;
                }
                finally
                {
                    _gate.Release();
                }
                IndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Apply(FileIndex index, Change change)
    {
        try
        {
            switch (change.Kind)
            {
                case ChangeKind.CreatedOrChanged:
                    AddOrUpdate(index, change.Path, depth: 0);
                    break;
                case ChangeKind.Deleted:
                    RemovePath(index, change.Path);
                    break;
                case ChangeKind.Renamed:
                    Rename(index, change.OldPath!, change.Path);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private static void RemovePath(FileIndex index, string path)
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

    private static void Rename(FileIndex index, string oldPath, string newPath)
    {
        index.Lock.EnterWriteLock();
        try
        {
            var oldId = index.FindPath(oldPath);
            var newParent = index.FindPath(Path.GetDirectoryName(newPath) ?? string.Empty);
            var existing = index.FindPath(newPath);
            if (oldId > FileIndex.RootId && newParent != FileIndex.None && existing == FileIndex.None)
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
        AddOrUpdate(index, newPath, depth: 0);
    }

    private static void AddOrUpdate(FileIndex index, string path, int depth)
    {
        var relative = index.GetRelativePath(path);
        if (string.IsNullOrEmpty(relative) || depth > 64)
            return;

        if (!FileScanner.TryStat(path, out var isDir, out var size, out var modified, out var isReparse))
        {
            RemovePath(index, path);
            return;
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
                return;
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
                if (index.FindChild(parentId, Path.GetFileName(path)) != FileIndex.None)
                    return;
                var id = index.AddLive(parentId, Path.GetFileName(path), isDir, size, modified, isReparse);
                if (subtree is not null)
                    index.ImportSubtree(id, subtree);
                return;
            }
        }
        finally
        {
            index.Lock.ExitWriteLock();
        }

        // Üst klasör henüz indekste yok: önce onu (içeriğiyle birlikte) ekle.
        AddOrUpdate(index, parentPath, depth + 1);
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
