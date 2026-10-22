using System.Threading.Channels;
using Everything.Core.Ntfs;

namespace Everything.Core;

public enum IndexState
{
    NotStarted,
    Loading,
    Scanning,
    Ready,
    Failed,
}

public sealed class VolumeIndexerOptions
{
    public string? CacheDirectory { get; init; }

    /// <summary>Hızlı tam tarama (ör. NTFS MFT okuma). null döndürürse normal klasör taraması yapılır.</summary>
    public Func<string, IProgress<ScanProgress>?, CancellationToken, FileIndex?>? FastScan { get; init; }

    /// <summary>
    /// NTFS USN günlüğü (yönetici gerekir). Varsa açılışta yalnızca kapalıyken olan değişiklikler okunur
    /// ve canlı güncellemeler de günlükten alınır; yoksa <see cref="FileSystemWatcher"/> kullanılır.
    /// </summary>
    public Func<string, IUsnJournal?>? JournalFactory { get; init; }

    /// <summary>Günlük yoksa önbellek bundan eskiyse arka planda yeniden taranır (null = asla).</summary>
    public TimeSpan? RefreshIfOlderThan { get; init; }

    public TimeSpan JournalPollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Tek bir kök klasörün (ör. "C:\") indeksini yönetir. Açılışta önbellekten yükler ve tam tarama
/// yapmaz: USN günlüğü varsa kapalıyken olan değişiklikleri uygular, yoksa önbellek yeterince
/// yeniyse olduğu gibi kullanır. Değişiklikleri canlı olarak indekse işler.
/// </summary>
public sealed class VolumeIndexer : IDisposable
{
    private enum ChangeKind { CreatedOrChanged, Deleted, Renamed }

    private readonly record struct Change(ChangeKind Kind, string Path, string? OldPath = null);

    private readonly VolumeIndexerOptions _options;
    private readonly string? _cacheFile;
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;
    private IUsnJournal? _journal;
    private UsnApplier? _applier;
    private volatile FileIndex? _index;
    private int _rescanRequested;
    private int _pending;
    private bool _dirty;

    public VolumeIndexer(string rootPath, string? cacheDirectory = null)
        : this(rootPath, new VolumeIndexerOptions { CacheDirectory = cacheDirectory })
    {
    }

    public VolumeIndexer(string rootPath, VolumeIndexerOptions options)
    {
        RootPath = rootPath;
        _options = options;
        if (options.CacheDirectory is not null)
            _cacheFile = Path.Combine(options.CacheDirectory, CacheFileName(rootPath));
    }

    public string RootPath { get; }

    public FileIndex? Index => _index;

    public IndexState State { get; private set; } = IndexState.NotStarted;

    public int ItemsScanned { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Canlı güncellemeler USN günlüğünden mi geliyor (yönetici + NTFS).</summary>
    public bool UsesJournal => _journal is not null;

    /// <summary>Son tam tarama hızlı yöntemle (MFT) mi yapıldı.</summary>
    public bool UsedFastScan { get; private set; }

    /// <summary>Açılışta günlükten uygulanan değişiklik sayısı.</summary>
    public int CatchUpChanges { get; private set; }

    /// <summary>Durum veya tarama ilerlemesi değişti.</summary>
    public event EventHandler? StateChanged;

    /// <summary>İndeks değişti (canlı güncelleme ya da yeni tarama).</summary>
    public event EventHandler? IndexChanged;

    /// <summary>Önbelleği yükler, değişiklikleri yakalar ve canlı izlemeyi başlatır.</summary>
    public async Task StartAsync()
    {
        SetState(IndexState.Loading);
        FileIndex? cached = null;
        if (_cacheFile is not null)
        {
            cached = await Task.Run(() => IndexSerializer.TryLoad(_cacheFile)).ConfigureAwait(false);
            if (cached is not null && string.Equals(cached.RootPath, RootPath, FileIndex.NameComparison))
            {
                _index = cached;
                ItemsScanned = cached.Count;
                IndexChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                cached = null;
            }
        }

        _journal = TryOpenJournal();
        var needsScan = cached is null;

        if (cached is not null && _journal is not null)
        {
            // Kapalıyken olan değişiklikleri günlükten uygula; olmazsa tam tara.
            needsScan = !await Task.Run(() => TryCatchUp(cached)).ConfigureAwait(false);
        }
        else if (cached is not null && _options.RefreshIfOlderThan is { } maxAge)
        {
            needsScan = DateTime.UtcNow - cached.LastUpdatedUtc > maxAge;
        }

        if (_journal is null)
            StartWatcher();
        _ = Task.Run(_journal is null ? ConsumeChangesAsync : PollJournalAsync);

        if (needsScan)
        {
            await RescanAsync().ConfigureAwait(false);
        }
        else
        {
            SetState(IndexState.Ready);
            if (_dirty)
                TrySaveCache();
        }
    }

    /// <summary>Tüm ağacı yeniden tarar. Tarama süresince gelen değişiklikler sonra uygulanır.</summary>
    public async Task RescanAsync()
    {
        await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _rescanRequested, 0);
            SetState(IndexState.Scanning);
            var progress = new SynchronousProgress<ScanProgress>(p =>
            {
                ItemsScanned = p.ItemsFound;
                StateChanged?.Invoke(this, EventArgs.Empty);
            });

            // Taramadan ÖNCEKİ günlük konumu: tarama sırasında olan değişiklikler sonra yeniden uygulanır.
            UsnJournalState? journalState = null;
            try
            {
                journalState = _journal?.Query();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }

            var fresh = await Task.Run(() => FullScan(progress), _cts.Token).ConfigureAwait(false);
            if (journalState is { } state)
            {
                fresh.UsnJournalId = state.JournalId;
                fresh.NextUsn = state.NextUsn;
            }
            _index = fresh;
            if (_journal is not null)
                _applier = new UsnApplier(fresh, _journal);
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

    /// <summary>Bekleyen tüm canlı değişiklikler işlenene kadar bekler.</summary>
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
        _journal?.Dispose();
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

    private FileIndex FullScan(IProgress<ScanProgress> progress)
    {
        if (_options.FastScan is { } fast)
        {
            try
            {
                if (fast(RootPath, progress, _cts.Token) is { } index)
                {
                    UsedFastScan = true;
                    return index;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                // Hızlı tarama olmadı (ör. NTFS değil ya da yetki yok): normal taramaya düş.
            }
        }
        UsedFastScan = false;
        return FileScanner.Scan(RootPath, progress, _cts.Token);
    }

    private IUsnJournal? TryOpenJournal()
    {
        try
        {
            return _options.JournalFactory?.Invoke(RootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Önbellekteki konumdan bu yana olan değişiklikleri uygular. Olmuyorsa false.</summary>
    private bool TryCatchUp(FileIndex index)
    {
        var journal = _journal!;
        try
        {
            var state = journal.Query();
            if (index.UsnJournalId != state.JournalId || index.NextUsn < state.FirstUsn || index.NextUsn == 0)
                return false;

            _applier = new UsnApplier(index, journal);
            var total = 0;
            var usn = index.NextUsn;
            while (usn < state.NextUsn)
            {
                var records = journal.Read(state.JournalId, usn, out var next, _cts.Token);
                total += _applier.Apply(records, _cts.Token);
                if (next <= usn)
                    break;
                usn = next;
            }
            index.NextUsn = usn;
            index.LastUpdatedUtc = DateTime.UtcNow;
            CatchUpChanges = total;
            _dirty = total > 0;
            if (total > 0)
                IndexChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is UsnJournalResetException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private void SetState(IndexState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PollJournalAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_options.JournalPollInterval, _cts.Token).ConfigureAwait(false);
                var index = _index;
                if (index is null || _applier is null || State != IndexState.Ready)
                    continue;

                var changed = 0;
                await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    var records = _journal!.Read(index.UsnJournalId, index.NextUsn, out var next, _cts.Token);
                    if (records.Count > 0)
                        changed = _applier.Apply(records, _cts.Token);
                    index.NextUsn = next;
                    if (changed > 0)
                    {
                        index.LastUpdatedUtc = DateTime.UtcNow;
                        _dirty = true;
                    }
                }
                catch (UsnJournalResetException)
                {
                    RequestRescan();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                }
                finally
                {
                    _gate.Release();
                }

                if (changed > 0)
                    IndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
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

    private static void Apply(FileIndex index, Change change)
    {
        try
        {
            switch (change.Kind)
            {
                case ChangeKind.CreatedOrChanged:
                    IndexMutations.AddOrUpdate(index, change.Path);
                    break;
                case ChangeKind.Deleted:
                    IndexMutations.RemovePath(index, change.Path);
                    break;
                case ChangeKind.Renamed:
                    IndexMutations.Rename(index, change.OldPath!, change.Path);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
