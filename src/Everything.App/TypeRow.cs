using Everything.Core;

namespace Everything.App;

/// <summary>Kategori / uzantı tablosu satırı.</summary>
public sealed class TypeRow
{
    public TypeRow(TypeUsage usage, long total)
    {
        Key = usage.Key;
        Category = usage.Category;
        Size = usage.Size;
        FileCount = usage.FileCount;
        Percent = total > 0 ? 100.0 * usage.Size / total : 0;
    }

    public string Key { get; }
    public string Category { get; }
    public long Size { get; }
    public int FileCount { get; }
    public double Percent { get; }

    public string SizeText => ByteSize.Format(Size);
    public string PercentText => Percent.ToString("0.0") + " %";
    public string CountText => FileCount.ToString("N0");
}
