using Everything.Core.Ntfs;

namespace Everything.Core.Tests;

/// <summary>
/// Testler için bellek içi USN günlüğü. Yollara sabit dosya referansları atar; ResolvePath
/// referansı ilk görüldüğü yola (yeniden adlandırmalarda güncellenerek) çevirir.
/// </summary>
internal sealed class FakeUsnJournal : IUsnJournal
{
    private readonly string _root;
    private readonly List<UsnRecord> _records = new();
    private readonly Dictionary<string, long> _references = new();
    private readonly Dictionary<long, string> _paths = new();
    private ulong _journalId = 1;
    private long _firstUsn = 1;
    private long _nextUsn = 1;
    private long _nextReference = 100;

    public FakeUsnJournal(string root)
    {
        _root = root;
        _references[root] = 5;
        _paths[5] = root;
    }

    /// <summary>VolumeIndexer günlüğü kapatınca paylaşılan nesne bozulmasın diye sarmalayıcı.</summary>
    public IUsnJournal Share() => new Shared(this);

    public void Add(UsnReason reason, string path, bool isDirectory = false)
    {
        lock (_records)
        {
            var parent = Path.GetDirectoryName(path)!;
            var reference = ReferenceOf(path);
            if ((reason & UsnReason.RenameOldName) != 0)
                _pendingRename = reference;
            if ((reason & UsnReason.RenameNewName) != 0 && _pendingRename is { } renamed)
            {
                reference = renamed;
                var old = _paths[renamed];
                _references.Remove(old);
                _references[path] = renamed;
                _paths[renamed] = path;
                _pendingRename = null;
            }
            _records.Add(new UsnRecord(_nextUsn, reference, ReferenceOf(parent), reason, Path.GetFileName(path), isDirectory));
            _nextUsn += 100;
        }
    }

    private long? _pendingRename;

    public void Reset()
    {
        lock (_records)
        {
            _journalId++;
            _records.Clear();
            _firstUsn = _nextUsn;
        }
    }

    public UsnJournalState Query()
    {
        lock (_records)
            return new UsnJournalState(_journalId, _firstUsn, _nextUsn);
    }

    public List<UsnRecord> Read(ulong journalId, long startUsn, out long nextUsn, CancellationToken cancellationToken)
    {
        lock (_records)
        {
            if (journalId != _journalId || startUsn < _firstUsn)
                throw new UsnJournalResetException("günlük sıfırlandı");
            nextUsn = _nextUsn;
            return _records.Where(r => r.Usn >= startUsn).ToList();
        }
    }

    public string? ResolvePath(long fileReference)
    {
        lock (_records)
            return _paths.TryGetValue(fileReference, out var path) && (Directory.Exists(path) || File.Exists(path)) ? path : null;
    }

    public void Dispose()
    {
    }

    private long ReferenceOf(string path)
    {
        if (!_references.TryGetValue(path, out var reference))
        {
            reference = _nextReference++;
            _references[path] = reference;
            _paths[reference] = path;
        }
        return reference;
    }

    private sealed class Shared : IUsnJournal
    {
        private readonly FakeUsnJournal _inner;

        public Shared(FakeUsnJournal inner) => _inner = inner;

        public UsnJournalState Query() => _inner.Query();

        public List<UsnRecord> Read(ulong journalId, long startUsn, out long nextUsn, CancellationToken cancellationToken) =>
            _inner.Read(journalId, startUsn, out nextUsn, cancellationToken);

        public string? ResolvePath(long fileReference) => _inner.ResolvePath(fileReference);

        public void Dispose()
        {
        }
    }
}
