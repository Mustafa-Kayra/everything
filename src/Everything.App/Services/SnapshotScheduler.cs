using Everything.Core;

namespace Everything.App.Services;

/// <summary>"Ne büyüdü?" karşılaştırmaları için günde bir klasör boyutu kaydı alır.</summary>
internal static class SnapshotScheduler
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(20);

    public static void TakeDueSnapshots()
    {
        foreach (var volume in AppServices.Indexes.Volumes)
        {
            if (volume.State != IndexState.Ready || volume.Index is not { } index)
                continue;
            try
            {
                var existing = SnapshotStore.List(AppPaths.SnapshotDirectory, volume.RootPath);
                if (existing.Count > 0 && DateTime.UtcNow - existing[0].TakenUtc < Interval)
                    continue;
                SnapshotStore.Save(SnapshotStore.Capture(index), AppPaths.SnapshotDirectory);
                SnapshotStore.Prune(AppPaths.SnapshotDirectory, volume.RootPath);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                AppPaths.Log($"Boyut kaydı alınamadı ({volume.RootPath}): {ex.Message}");
            }
        }
    }
}
