using System.Buffers.Binary;
using System.Numerics;

namespace Everything.Core.Ntfs;

/// <summary>NTFS önyükleme sektöründen (VBR) okunan birim geometrisi.</summary>
internal readonly record struct NtfsBootSector(int BytesPerSector, int BytesPerCluster, long MftLcn, int BytesPerFileRecord)
{
    /// <summary>Önyükleme sektörünü çözümler; NTFS değilse <see cref="InvalidDataException"/> fırlatır.</summary>
    public static NtfsBootSector Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512
            || !sector.Slice(3, 8).SequenceEqual("NTFS    "u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(sector[510..]) != 0xAA55)
        {
            throw new InvalidDataException("Akış bir NTFS birimi değil (önyükleme sektörü imzası bulunamadı).");
        }

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0B..]);
        if (bytesPerSector < 256 || bytesPerSector > 4096 || !BitOperations.IsPow2(bytesPerSector))
            throw new InvalidDataException($"Geçersiz NTFS sektör boyutu: {bytesPerSector}.");

        // 0x80'den büyük değerler 2^(256 - değer) sektör anlamına gelir (64 KB üstü kümeler).
        int rawSpc = sector[0x0D];
        long sectorsPerCluster = rawSpc <= 0x80 ? rawSpc : 1L << (256 - rawSpc);
        var bytesPerCluster = sectorsPerCluster * bytesPerSector;
        if (sectorsPerCluster == 0 || !BitOperations.IsPow2(sectorsPerCluster) || bytesPerCluster > 2 * 1024 * 1024)
            throw new InvalidDataException($"Geçersiz NTFS küme boyutu ({rawSpc}).");

        var mftLcn = BinaryPrimitives.ReadInt64LittleEndian(sector[0x30..]);
        if (mftLcn <= 0)
            throw new InvalidDataException("Geçersiz $MFT başlangıç kümesi.");

        // İşaretli bayt: pozitifse küme sayısı, negatif -n ise 2^n bayt.
        var rawRecord = (sbyte)sector[0x40];
        var bytesPerRecord = rawRecord > 0 ? rawRecord * bytesPerCluster : 1L << -rawRecord;
        if (bytesPerRecord < 256 || bytesPerRecord > 65536 || !BitOperations.IsPow2(bytesPerRecord))
            throw new InvalidDataException($"Geçersiz MFT kayıt boyutu ({rawRecord}).");

        return new NtfsBootSector(bytesPerSector, (int)bytesPerCluster, mftLcn, (int)bytesPerRecord);
    }
}
