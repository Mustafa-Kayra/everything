namespace Everything.Core;

/// <summary>Birden çok sürücünün indeksini birlikte yönetir.</summary>
public sealed class IndexManager : IDisposable
{
    private readonly List<VolumeIndexer> _volumes = new();
    private readonly string? _cacheDirectory;
    private Timer? _saveTimer;

    public IndexManager(string? cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
    }

    public IReadOnlyList<VolumeIndexer> Volumes => _volumes;

    public event EventHandler? StateChanged;
    public event EventHandler? IndexChanged;

    /// <summary>Hazır olan tüm indekslerin anlık listesi.</summary>
    public List<FileIndex> GetIndexes() =>
        _volumes.Select(v => v.Index).Where(i => i is not null).Select(i => i!).ToList();

    public int TotalItems => _volumes.Sum(v => v.Index?.Count ?? 0);

    public bool IsBusy => _volumes.Any(v => v.State is IndexState.Loading or IndexState.Scanning);

    public void Start(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var volume = new VolumeIndexer(root, _cacheDirectory);
            volume.StateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
            volume.IndexChanged += (_, _) => IndexChanged?.Invoke(this, EventArgs.Empty);
            _volumes.Add(volume);
            _ = RunSafe(volume.StartAsync);
        }

        // Canlı değişiklikleri birkaç dakikada bir önbelleğe yaz.
        _saveTimer = new Timer(_ => SaveDirtyCaches(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public VolumeIndexer? FindVolume(string root) =>
        _volumes.FirstOrDefault(v => string.Equals(v.RootPath, root, FileIndex.NameComparison));

    public Task RescanAllAsync() => Task.WhenAll(_volumes.Select(v => RunSafe(v.RescanAsync)));

    public void SaveDirtyCaches()
    {
        foreach (var volume in _volumes)
        {
            if (volume.HasUnsavedChanges && volume.State == IndexState.Ready)
                volume.TrySaveCache();
        }
    }

    public void Dispose()
    {
        _saveTimer?.Dispose();
        SaveDirtyCaches();
        foreach (var volume in _volumes)
            volume.Dispose();
    }

    private static async Task RunSafe(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
