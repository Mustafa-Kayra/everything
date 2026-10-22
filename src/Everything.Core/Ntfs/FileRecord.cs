using System.Buffers.Binary;

namespace Everything.Core.Ntfs;

/// <summary>
/// MFT dosya kaydı ("FILE") ve öznitelik başlıklarını çözmek için yardımcılar.
/// Tümü doğrudan tampon üzerinde çalışır; bellek ayırmaz.
/// </summary>
internal static class FileRecord
{
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrEnd = 0xFFFFFFFF;

    public const ushort FlagInUse = 1;
    public const ushort FlagDirectory = 2;

    /// <summary>Kayıt numarası için 48 bitlik maske (dosya referansının üst 16 biti sıra numarasıdır).</summary>
    public const ulong RecordNumberMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>Güncelleme dizisi (fixup) adımı; kayıt boyutundan bağımsız olarak her zaman 512 bayttır.</summary>
    private const int FixupStride = 512;

    private const uint FileSignature = 0x454C4946; // "FILE"

    public static ushort Flags(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
    public static ushort SequenceNumber(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]);
    /// <summary>
    /// Ek kayıtlarda temel kaydın tam dosya referansı; temel kayıtlarda 0. Kayıt numarası için
    /// <see cref="RecordNumberMask"/> uygulanmalıdır ($MFT'nin ek kayıtlarında kayıt numarası 0'dır, sıra numarası değildir).
    /// </summary>
    public static ulong BaseReference(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]);

    /// <summary>
    /// "FILE" imzasını doğrular ve güncelleme dizisini uygular: her 512 baytlık bloğun son iki baytı
    /// diske yazılırken sıra numarasıyla değiştirilmiştir; asıl değerler dizide saklanır.
    /// Eşleşmeyen blok yarım kalmış bir yazımı gösterir ve kayıt geçersiz sayılır.
    /// </summary>
    public static bool TryApplyFixups(Span<byte> record)
    {
        if (record.Length < 0x30 || BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature)
            return false;

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 2 || (usaOffset & 1) != 0 || usaOffset < 0x28
            || usaOffset + usaCount * 2 > FixupStride || (usaCount - 1) * FixupStride > record.Length)
        {
            return false;
        }

        var usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (var i = 1; i < usaCount; i++)
        {
            var pos = i * FixupStride - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[pos..]) != usn)
                return false;
            record[pos] = record[usaOffset + i * 2];
            record[pos + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>Kalıcı (resident) özniteliğin değerini döndürür; sınırlar tutarsızsa false.</summary>
    public static bool TryGetResidentValue(ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (attribute[8] != 0 || attribute.Length < 0x18)
            return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        if (offset > attribute.Length || length > (uint)(attribute.Length - offset))
            return false;
        value = attribute.Slice(offset, (int)length);
        return true;
    }

    public static bool IsNonResident(ReadOnlySpan<byte> attribute) => attribute[8] != 0 && attribute.Length >= 0x40;

    public static bool IsUnnamed(ReadOnlySpan<byte> attribute) => attribute[9] == 0;

    /// <summary>Kalıcı olmayan özniteliğin bu parçasının başlangıç VCN'i.</summary>
    public static long StartVcn(ReadOnlySpan<byte> attribute) => BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]);

    /// <summary>Kalıcı olmayan özniteliğin gerçek veri boyutu (yalnızca VCN 0 parçasında geçerlidir).</summary>
    public static long DataSize(ReadOnlySpan<byte> attribute) => BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);

    /// <summary>Kalıcı olmayan özniteliğin aralık listesi (mapping pairs) baytları.</summary>
    public static ReadOnlySpan<byte> RunList(ReadOnlySpan<byte> attribute)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..]);
        return offset < attribute.Length ? attribute[offset..] : default;
    }

    public static AttributeEnumerator Attributes(ReadOnlySpan<byte> record) => new(record);

    /// <summary>Kayıttaki öznitelikleri sırayla dolaşır; bozuk bir başlıkta durur.</summary>
    public ref struct AttributeEnumerator
    {
        private readonly ReadOnlySpan<byte> _record;
        private readonly int _end;
        private int _offset;

        public AttributeEnumerator(ReadOnlySpan<byte> record)
        {
            _record = record;
            _offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
            _end = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
            Current = default;
            Type = 0;
        }

        public ReadOnlySpan<byte> Current { get; private set; }

        public uint Type { get; private set; }

        public bool MoveNext()
        {
            if (_offset + 16 > _end)
                return false;
            Type = BinaryPrimitives.ReadUInt32LittleEndian(_record[_offset..]);
            if (Type == AttrEnd)
                return false;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(_record[(_offset + 4)..]);
            if (length < 16 || length > (uint)(_end - _offset))
                return false;
            Current = _record.Slice(_offset, (int)length);
            _offset += (int)length;
            return true;
        }
    }
}
