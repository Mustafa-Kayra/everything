namespace Everything.Core;

/// <summary>
/// İndeksi diske kaydeder/yükler; böylece uygulama açılır açılmaz arama yapılabilir.
/// Kayıtlar ön-sırada (ebeveyn önce) yazılır ve silinmiş kayıtlar atlanır.
/// </summary>
public static class IndexSerializer
{
    private const int Magic = 0x58495645; // "EVIX"
    private const int Version = 1;

    public static void Save(FileIndex index, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temp = filePath + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var writer = new BinaryWriter(stream))
        {
            List<int> order;
            index.Lock.EnterReadLock();
            try
            {
                order = index.PreOrder(FileIndex.RootId);
                var newId = new Dictionary<int, int>(order.Count);
                for (var i = 0; i < order.Count; i++)
                    newId[order[i]] = i;

                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(index.RootPath);
                writer.Write(index.LastUpdatedUtc.Ticks);
                writer.Write(order.Count);

                foreach (var id in order)
                {
                    if (id == FileIndex.RootId)
                        continue;
                    var flags = (byte)((index.IsDirectory(id) ? 1 : 0) | (index.IsReparsePoint(id) ? 2 : 0));
                    writer.Write(newId[index.GetParent(id)]);
                    writer.Write(index.GetName(id));
                    writer.Write(flags);
                    writer.Write(index.IsDirectory(id) ? 0L : index.GetSize(id));
                    writer.Write(index.GetModifiedTicks(id));
                }
            }
            finally
            {
                index.Lock.ExitReadLock();
            }
        }

        File.Move(temp, filePath, overwrite: true);
    }

    public static FileIndex? TryLoad(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                return null;

            var root = reader.ReadString();
            var updated = reader.ReadInt64();
            var count = reader.ReadInt32();
            var index = new FileIndex(root, count);
            for (var i = 1; i < count; i++)
            {
                var parent = reader.ReadInt32();
                var name = reader.ReadString();
                var flags = reader.ReadByte();
                var size = reader.ReadInt64();
                var modified = reader.ReadInt64();
                if (parent < 0 || parent >= i)
                    return null;
                index.AddEntry(parent, name, (flags & 1) != 0, size, modified, (flags & 2) != 0);
            }
            index.ComputeAggregates();
            index.LastUpdatedUtc = new DateTime(updated, DateTimeKind.Utc);
            return index;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}
