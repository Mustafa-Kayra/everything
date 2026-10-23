using Everything.Core;

namespace Everything.App.Services;

/// <summary>Arama sekmesi ve hızlı arama penceresinin ortak arama/sıralama mantığı.</summary>
internal static class SearchService
{
    public static Task<(SearchResult Result, List<ResultItem> Items)> RunAsync(
        string text, int maxResults, string sortColumn, bool descending, CancellationToken cancellationToken)
    {
        var query = SearchQuery.Parse(text, AppServices.Settings.IgnoreDiacritics);
        var indexes = AppServices.Indexes.GetIndexes();
        return Task.Run(() =>
        {
            var result = SearchEngine.Search(indexes, query, maxResults, cancellationToken);
            var items = new List<ResultItem>(result.Hits.Count);
            foreach (var group in result.Hits.GroupBy(h => h.Index))
            {
                group.Key.Lock.EnterReadLock();
                try
                {
                    foreach (var hit in group)
                        items.Add(new ResultItem(hit.Index, hit.Id));
                }
                finally
                {
                    group.Key.Lock.ExitReadLock();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (result, Sort(items, sortColumn, descending));
        }, cancellationToken);
    }

    public static List<ResultItem> Sort(List<ResultItem> items, string column, bool descending)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<ResultItem> ordered = column switch
        {
            "Boyut" => descending ? items.OrderByDescending(i => i.Size) : items.OrderBy(i => i.Size),
            "Değiştirilme" => descending ? items.OrderByDescending(i => i.Modified) : items.OrderBy(i => i.Modified),
            "Konum" => descending ? items.OrderByDescending(i => i.Folder, comparer) : items.OrderBy(i => i.Folder, comparer),
            _ => descending ? items.OrderByDescending(i => i.Name, comparer) : items.OrderBy(i => i.Name, comparer),
        };
        return ordered.ThenBy(i => i.Name, comparer).ToList();
    }

    public static bool SameItems(List<ResultItem> a, List<ResultItem> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Index != b[i].Index || a[i].Id != b[i].Id || a[i].Size != b[i].Size || a[i].Name != b[i].Name)
                return false;
        }
        return true;
    }
}
