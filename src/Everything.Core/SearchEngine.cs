using System.Collections.Concurrent;
using System.Diagnostics;

namespace Everything.Core;

public readonly record struct SearchHit(FileIndex Index, int Id);

public sealed class SearchResult
{
    public static SearchResult Empty { get; } = new(new List<SearchHit>(), 0, TimeSpan.Zero);

    public SearchResult(List<SearchHit> hits, int totalMatches, TimeSpan elapsed)
    {
        Hits = hits;
        TotalMatches = totalMatches;
        Elapsed = elapsed;
    }

    public List<SearchHit> Hits { get; }
    public int TotalMatches { get; }
    public TimeSpan Elapsed { get; }
    public bool Truncated => TotalMatches > Hits.Count;
}

/// <summary>Bellek içi indekslerde paralel, büyük/küçük harf duyarsız arama.</summary>
public static class SearchEngine
{
    private const int ChunkSize = 16 * 1024;

    public static SearchResult Search(IEnumerable<FileIndex> indexes, SearchQuery query, int maxResults = 100_000, CancellationToken cancellationToken = default)
    {
        if (query.IsEmpty)
            return SearchResult.Empty;

        var sw = Stopwatch.StartNew();
        var hits = new ConcurrentBag<List<SearchHit>>();
        var total = 0;

        foreach (var index in indexes)
        {
            index.Lock.EnterReadLock();
            try
            {
                var capacity = index.Capacity;
                var chunks = (capacity + ChunkSize - 1) / ChunkSize;
                Parallel.For(
                    0,
                    chunks,
                    new ParallelOptions { CancellationToken = cancellationToken },
                    () => new List<SearchHit>(),
                    (chunk, _, local) =>
                    {
                        var start = Math.Max(chunk * ChunkSize, FileIndex.RootId + 1);
                        var end = Math.Min(capacity, (chunk + 1) * ChunkSize);
                        for (var id = start; id < end; id++)
                        {
                            if ((id & 1023) == 0 && cancellationToken.IsCancellationRequested)
                                break;
                            if (!Matches(index, id, query))
                                continue;
                            if (Interlocked.Increment(ref total) <= maxResults)
                                local.Add(new SearchHit(index, id));
                        }
                        return local;
                    },
                    local =>
                    {
                        if (local.Count > 0)
                            hits.Add(local);
                    });
            }
            finally
            {
                index.Lock.ExitReadLock();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var list = new List<SearchHit>(Math.Min(total, maxResults));
        foreach (var part in hits)
            list.AddRange(part);
        return new SearchResult(list, total, sw.Elapsed);
    }

    public static bool Matches(FileIndex index, int id, SearchQuery query)
    {
        if (index.IsDeleted(id))
            return false;

        var isDir = index.IsDirectory(id);
        if (query.Kind == EntryKind.File && isDir)
            return false;
        if (query.Kind == EntryKind.Folder && !isDir)
            return false;

        var name = index.GetName(id);

        foreach (var term in query.NameTerms)
        {
            if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        if (query.Extensions is not null)
        {
            if (isDir)
                return false;
            var dot = name.LastIndexOf('.');
            if (dot < 0 || !query.Extensions.Contains(name[(dot + 1)..]))
                return false;
        }

        if (query.MinSize is not null || query.MaxSize is not null)
        {
            // Boyut filtresi varsayılan olarak dosyalara uygulanır; büyük klasörler için "folder: size:>1gb".
            if (isDir && query.Kind != EntryKind.Folder)
                return false;
            var size = index.GetSize(id);
            if (query.MinSize is { } min && size < min)
                return false;
            if (query.MaxSize is { } max && size > max)
                return false;
        }

        foreach (var wildcard in query.Wildcards)
        {
            if (!wildcard.IsMatch(name))
                return false;
        }

        foreach (var term in query.ExcludedTerms)
        {
            if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }

        if (query.NeedsFullPath)
        {
            var path = index.GetFullPath(id);
            foreach (var term in query.PathTerms)
            {
                if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
        }

        return true;
    }
}
