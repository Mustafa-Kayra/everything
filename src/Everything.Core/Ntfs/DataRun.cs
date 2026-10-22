using System.Buffers.Binary;

namespace Everything.Core.Ntfs;

/// <summary>Kalıcı olmayan bir özniteliğin bitişik küme dizisi. <see cref="Lcn"/> = -1 seyrek (sparse) aralıktır.</summary>
internal readonly record struct DataRun(long Vcn, long Lcn, long Length)
{
    public const long Sparse = -1;

    public bool IsSparse => Lcn == Sparse;

    /// <summary>
    /// "Mapping pairs" (runlist) dizisini çözer ve <paramref name="runs"/> listesine ekler.
    /// Her başlık baytının alt yarısı uzunluk alanının, üst yarısı LCN farkı alanının bayt sayısıdır;
    /// LCN farkı işaretlidir ve bir önceki aralığa görelidir. Fark alanı yoksa aralık seyrektir.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, long startVcn, List<DataRun> runs)
    {
        var vcn = startVcn;
        long lcn = 0;
        var pos = 0;
        while (pos < data.Length && data[pos] != 0)
        {
            var header = data[pos++];
            var lengthSize = header & 0x0F;
            var offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || pos + lengthSize + offsetSize > data.Length)
                return false;

            var length = ReadUnsigned(data.Slice(pos, lengthSize));
            pos += lengthSize;
            if (length <= 0)
                return false;

            if (offsetSize == 0)
            {
                runs.Add(new DataRun(vcn, Sparse, length));
            }
            else
            {
                lcn += ReadSigned(data.Slice(pos, offsetSize));
                pos += offsetSize;
                if (lcn < 0)
                    return false;
                runs.Add(new DataRun(vcn, lcn, length));
            }
            vcn += length;
        }
        return true;
    }

    /// <summary>1–8 baytlık küçük-endian işaretsiz tamsayı okur (8 baytta taşarsa negatif döner).</summary>
    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        Span<byte> buffer = stackalloc byte[8];
        buffer.Clear();
        bytes.CopyTo(buffer);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    /// <summary>1–8 baytlık küçük-endian işaretli tamsayı okur.</summary>
    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        Span<byte> buffer = stackalloc byte[8];
        bytes.CopyTo(buffer);
        // İşaret bitini kalan baytlara yay.
        if ((bytes[^1] & 0x80) != 0)
            buffer[bytes.Length..].Fill(0xFF);
        else
            buffer[bytes.Length..].Clear();
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }
}
