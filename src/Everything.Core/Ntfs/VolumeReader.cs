using System.Runtime.InteropServices;

namespace Everything.Core.Ntfs;

/// <summary>
/// Ham birim akışından okuma yapar. Windows'ta "\\.\C:" tanıtıcıları yalnızca sektör sınırında
/// başlayan ve sektör katı uzunluktaki okumaları kabul ettiği için tüm okumalar bu kurala uyar;
/// kısa okumalar (akış istenenden az veri döndürdüğünde) tamamlanana kadar tekrarlanır.
/// </summary>
internal sealed class VolumeReader
{
    private readonly Stream _stream;

    public VolumeReader(Stream stream, int bytesPerSector)
    {
        _stream = stream;
        BytesPerSector = bytesPerSector;
    }

    public int BytesPerSector { get; }

    /// <summary>
    /// <paramref name="offset"/> konumundan <paramref name="buffer"/> dolana ya da akış bitene kadar okur.
    /// Okunan bayt sayısını döndürür.
    /// </summary>
    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset % BytesPerSector != 0 || buffer.Length % BytesPerSector != 0)
            throw new InvalidOperationException($"Hizalanmamış birim okuması: konum {offset}, uzunluk {buffer.Length}.");

        _stream.Seek(offset, SeekOrigin.Begin);
        var total = 0;
        while (total < buffer.Length)
        {
            var n = _stream.Read(buffer[total..]);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    /// <summary>Tamamını okur; birim erken biterse <see cref="InvalidDataException"/> fırlatır.</summary>
    public void ReadExactly(long offset, Span<byte> buffer)
    {
        if (ReadAt(offset, buffer) != buffer.Length)
            throw new InvalidDataException($"NTFS birimi beklenenden kısa (konum {offset}).");
    }

    /// <summary>
    /// Kalıcı olmayan bir özniteliğin <paramref name="byteOffset"/> konumundaki verisini aralık listesi
    /// üzerinden okur. Konum ve uzunluk küme katı olmalıdır; seyrek aralıklar sıfırla doldurulur.
    /// </summary>
    public void ReadRuns(List<DataRun> runs, int bytesPerCluster, long byteOffset, Span<byte> buffer)
    {
        var runIndex = 0;
        while (buffer.Length > 0)
        {
            var vcn = byteOffset / bytesPerCluster;
            while (runIndex < runs.Count && runs[runIndex].Vcn + runs[runIndex].Length <= vcn)
                runIndex++;
            if (runIndex == runs.Count || runs[runIndex].Vcn > vcn)
                throw new InvalidDataException("NTFS öznitelik verisi aralık listesinin dışında.");

            var run = runs[runIndex];
            var runEnd = (run.Vcn + run.Length) * bytesPerCluster;
            var count = (int)Math.Min(buffer.Length, runEnd - byteOffset);
            if (run.IsSparse)
                buffer[..count].Clear();
            else
                ReadExactly(run.Lcn * bytesPerCluster + (byteOffset - run.Vcn * bytesPerCluster), buffer[..count]);

            buffer = buffer[count..];
            byteOffset += count;
        }
    }

    /// <summary>
    /// Bellekte <paramref name="alignment"/> sınırında başlayan sabitlenmiş (pinned) bir tampon ayırır.
    /// FILE_FLAG_NO_BUFFERING ile açılmış tanıtıcılar bellek adresinin de hizalı olmasını ister.
    /// </summary>
    public static Memory<byte> AllocateAligned(int length, int alignment = 4096)
    {
        var array = GC.AllocateUninitializedArray<byte>(length + alignment, pinned: true);
        var address = (long)Marshal.UnsafeAddrOfPinnedArrayElement(array, 0);
        var skip = (int)((alignment - address % alignment) % alignment);
        return array.AsMemory(skip, length);
    }
}
