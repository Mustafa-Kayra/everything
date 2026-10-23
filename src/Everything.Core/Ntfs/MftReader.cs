using System.Buffers.Binary;
using System.Text;

namespace Everything.Core.Ntfs;

/// <summary>
/// NTFS biriminin Ana Dosya Tablosunu (MFT) ham birim akışından doğrudan okuyarak
/// <see cref="FileIndex"/> oluşturur. Klasörleri tek tek dolaşmaktan çok daha hızlıdır;
/// Windows'ta çağıran "\\.\C:" üzerinde açılmış bir akış verir (yönetici yetkisi gerekir).
/// </summary>
public static class MftReader
{
    private const int RootRecord = 5;
    private const int FirstUserRecord = 16;
    private const int ChunkSize = 8 * 1024 * 1024;
    private const int BootReadSize = 4096;
    private const uint FileAttributeReparsePoint = 0x400;
    private const byte NamespaceDos = 2;

    private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>NTFS biriminin MFT'sini ham birim akışından okur ve bir <see cref="FileIndex"/> oluşturur.</summary>
    /// <param name="volume">Birimin başından itibaren okunabilen, konumlanabilir akış.</param>
    /// <param name="rootPath">İndeksin kök yolu (ör. "C:\").</param>
    /// <exception cref="InvalidDataException">Akış geçerli bir NTFS birimi değilse.</exception>
    public static FileIndex Read(Stream volume, string rootPath, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(rootPath);
        if (!volume.CanSeek || !volume.CanRead)
            throw new ArgumentException("Birim akışı okunabilir ve konumlanabilir olmalıdır.", nameof(volume));

        // 4096 bayt hem 512 hem 4096 baytlık sektörlerin katıdır; sektör boyutunu henüz bilmiyoruz.
        var bootBuffer = VolumeReader.AllocateAligned(BootReadSize);
        var bootLength = new VolumeReader(volume, 512).ReadAt(0, bootBuffer.Span);
        var boot = NtfsBootSector.Parse(bootBuffer.Span[..bootLength]);
        var reader = new VolumeReader(volume, boot.BytesPerSector);

        var (runs, mftSize) = ReadMftLayout(reader, boot);
        var recordCount = mftSize / boot.BytesPerFileRecord;
        if (recordCount <= RootRecord || recordCount > int.MaxValue)
            throw new InvalidDataException($"Geçersiz MFT boyutu: {mftSize} bayt.");

        var table = new RecordTable((int)recordCount);
        ReadRecords(reader, boot, runs, table, rootPath, progress, cancellationToken);
        var index = table.BuildIndex(rootPath, progress, cancellationToken);
        progress?.Report(new ScanProgress(rootPath, index.Count, string.Empty));
        return index;
    }

    /// <summary>
    /// $MFT'nin kendi kaydını (0) okur ve adsız $DATA özniteliğinin aralık listesinden MFT'nin
    /// diskteki tüm parçalarını bulur. Çok parçalı MFT'lerde liste $ATTRIBUTE_LIST ile ek kayıtlara
    /// taşmış olabilir; bu parçalar da okunur.
    /// </summary>
    private static (List<DataRun> Runs, long Size) ReadMftLayout(VolumeReader reader, NtfsBootSector boot)
    {
        var recordSize = boot.BytesPerFileRecord;
        var buffer = VolumeReader.AllocateAligned((int)RoundUp(recordSize, boot.BytesPerSector));
        reader.ReadExactly(boot.MftLcn * boot.BytesPerCluster, buffer.Span);
        var record = buffer.Span[..recordSize];
        if (!FileRecord.TryApplyFixups(record) || (FileRecord.Flags(record) & FileRecord.FlagInUse) == 0)
            throw new InvalidDataException("$MFT kaydı (0) bozuk veya okunamadı.");

        var runs = new List<DataRun>();
        long size = -1;
        byte[]? attributeList = null;
        var attributes = FileRecord.Attributes(record);
        while (attributes.MoveNext())
        {
            var attr = attributes.Current;
            if (attributes.Type == FileRecord.AttrData && FileRecord.IsUnnamed(attr)
                && FileRecord.IsNonResident(attr) && FileRecord.StartVcn(attr) == 0)
            {
                size = FileRecord.DataSize(attr);
                if (!DataRun.TryParse(FileRecord.RunList(attr), 0, runs))
                    throw new InvalidDataException("$MFT aralık listesi bozuk.");
            }
            else if (attributes.Type == FileRecord.AttrAttributeList)
            {
                attributeList = ReadAttributeValue(reader, boot, attr);
            }
        }
        if (size <= 0 || runs.Count == 0)
            throw new InvalidDataException("$MFT veri özniteliği bulunamadı.");

        if (attributeList is not null)
            ReadMftExtensionRuns(reader, boot, attributeList, runs);

        // Aralıklar MFT'nin tamamını (küme katına yuvarlanmış) kapsamalıdır.
        long clusters = 0;
        foreach (var run in runs)
        {
            if (run.Vcn != clusters)
                throw new InvalidDataException("$MFT aralık listesi süreksiz.");
            clusters += run.Length;
        }
        if (clusters * boot.BytesPerCluster < RoundUp(size, boot.BytesPerCluster))
            throw new InvalidDataException("$MFT aralık listesi MFT boyutunu kapsamıyor.");
        return (runs, size);
    }

    /// <summary>$ATTRIBUTE_LIST girdilerinden $MFT'nin VCN 0 dışındaki $DATA parçalarını okuyup aralıklara ekler.</summary>
    private static void ReadMftExtensionRuns(VolumeReader reader, NtfsBootSector boot, byte[] list, List<DataRun> runs)
    {
        var segments = new List<(long Vcn, ulong Record)>();
        for (var pos = 0; pos + 0x1A <= list.Length;)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(pos));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(pos + 4));
            if (length < 0x1A)
                break;
            var nameLength = list[pos + 6];
            var startVcn = BinaryPrimitives.ReadInt64LittleEndian(list.AsSpan(pos + 8));
            var record = BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(pos + 0x10)) & FileRecord.RecordNumberMask;
            if (type == FileRecord.AttrData && nameLength == 0 && startVcn > 0 && record != 0)
                segments.Add((startVcn, record));
            pos += length;
        }
        segments.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));

        var recordSize = boot.BytesPerFileRecord;
        var cluster = boot.BytesPerCluster;
        foreach (var (vcn, recordNumber) in segments)
        {
            // Ek kaydı içeren küme hizalı pencereyi, şimdiye kadar bilinen aralıklar üzerinden oku.
            var offset = (long)recordNumber * recordSize;
            var start = offset / cluster * cluster;
            var window = VolumeReader.AllocateAligned((int)RoundUp(offset + recordSize - start, cluster));
            reader.ReadRuns(runs, cluster, start, window.Span);
            var record = window.Span.Slice((int)(offset - start), recordSize);
            if (!FileRecord.TryApplyFixups(record))
                throw new InvalidDataException($"$MFT ek kaydı ({recordNumber}) bozuk.");

            var found = false;
            var attributes = FileRecord.Attributes(record);
            while (attributes.MoveNext())
            {
                var attr = attributes.Current;
                if (attributes.Type == FileRecord.AttrData && FileRecord.IsUnnamed(attr)
                    && FileRecord.IsNonResident(attr) && FileRecord.StartVcn(attr) == vcn)
                {
                    if (!DataRun.TryParse(FileRecord.RunList(attr), vcn, runs))
                        throw new InvalidDataException("$MFT aralık listesi bozuk.");
                    found = true;
                }
            }
            if (!found)
                throw new InvalidDataException($"$MFT veri parçası (VCN {vcn}) bulunamadı.");
        }
    }

    /// <summary>Bir özniteliğin değerini (kalıcı ya da kalıcı olmayan) bayt dizisi olarak okur.</summary>
    private static byte[] ReadAttributeValue(VolumeReader reader, NtfsBootSector boot, ReadOnlySpan<byte> attr)
    {
        if (FileRecord.TryGetResidentValue(attr, out var value))
            return value.ToArray();
        if (!FileRecord.IsNonResident(attr))
            throw new InvalidDataException("$MFT öznitelik listesi bozuk.");

        var size = FileRecord.DataSize(attr);
        var runs = new List<DataRun>();
        if (size < 0 || size > 64 * 1024 * 1024 || !DataRun.TryParse(FileRecord.RunList(attr), 0, runs))
            throw new InvalidDataException("$MFT öznitelik listesi bozuk.");
        var buffer = VolumeReader.AllocateAligned((int)RoundUp(Math.Max(size, 1), boot.BytesPerCluster));
        reader.ReadRuns(runs, boot.BytesPerCluster, 0, buffer.Span);
        return buffer.Span[..(int)size].ToArray();
    }

    /// <summary>
    /// MFT'yi büyük, küme hizalı parçalar hâlinde okur ve her kaydı tabloya işler.
    /// Bir sonraki parça arka planda okunurken mevcut parça çözümlenir.
    /// </summary>
    private static void ReadRecords(VolumeReader reader, NtfsBootSector boot, List<DataRun> runs, RecordTable table,
        string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var recordSize = boot.BytesPerFileRecord;
        var cluster = boot.BytesPerCluster;
        // Kayıt ve küme boyutları ikinin kuvvetidir; parça boyutu ikisinin de katıdır.
        var chunkSize = Math.Max(ChunkSize, Math.Max(cluster, recordSize));
        var total = (long)table.Count * recordSize;
        Memory<byte>[] buffers = [VolumeReader.AllocateAligned(chunkSize), VolumeReader.AllocateAligned(chunkSize)];

        Task<int> StartRead(long offset, Memory<byte> buffer) => Task.Run(() =>
        {
            var length = (int)Math.Min(chunkSize, total - offset);
            reader.ReadRuns(runs, cluster, offset, buffer.Span[..(int)RoundUp(length, cluster)]);
            return length;
        }, cancellationToken);

        var lastReport = Environment.TickCount64;
        var current = 0;
        var pending = StartRead(0, buffers[0]);
        try
        {
            for (long offset = 0; offset < total; offset += chunkSize)
            {
                var length = pending.GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
                if (offset + chunkSize < total)
                    pending = StartRead(offset + chunkSize, buffers[current ^ 1]);

                var span = buffers[current].Span;
                var first = (int)(offset / recordSize);
                var count = length / recordSize;
                for (var i = 0; i < count; i++)
                    table.Process(span.Slice(i * recordSize, recordSize), first + i);
                current ^= 1;

                var now = Environment.TickCount64;
                if (progress is not null && now - lastReport > 200)
                {
                    lastReport = now;
                    progress.Report(new ScanProgress(rootPath, table.InUseCount, string.Empty));
                }
            }
        }
        finally
        {
            // Hata durumunda arka plandaki okuma bitmeden akış çağırana geri verilmesin.
            if (!pending.IsCompleted)
                ((IAsyncResult)pending).AsyncWaitHandle.WaitOne();
        }
    }

    private static long RoundUp(long value, long multiple) => (value + multiple - 1) / multiple * multiple;

    /// <summary>
    /// MFT kayıtlarından toplanan bilgiler; kayıt numarasıyla indekslenen paralel dizilerde tutulur.
    /// Her kaydın ilk adı (bağlantısı) dizilerde, sabit bağlantılar (hard link) için ek adlar ayrı listededir.
    /// </summary>
    private sealed class RecordTable
    {
        private const byte InUse = 1;
        private const byte Directory = 2;
        private const byte ReparsePoint = 4;
        private const byte Visited = 8;

        private readonly byte[] _flags;
        private readonly ushort[] _sequence;
        private readonly long[] _modified;
        private readonly long[] _size;
        private readonly int[] _parent;
        private readonly string?[] _name;
        private readonly List<(int Record, int Parent, string Name)> _extraLinks = new();

        public RecordTable(int count)
        {
            Count = count;
            _flags = new byte[count];
            _sequence = new ushort[count];
            _modified = new long[count];
            _size = new long[count];
            _parent = new int[count];
            _name = new string?[count];
        }

        public int Count { get; }

        /// <summary>Kullanımda olan temel kayıt sayısı (ilerleme bildirimi için).</summary>
        public int InUseCount { get; private set; }

        /// <summary>
        /// Tek bir MFT kaydını işler. Ek kayıtların (temel kayıt referansı sıfır olmayan) öznitelikleri
        /// temel kayda aittir; ad ve boyut bilgileri temel kayda yazılır.
        /// </summary>
        public void Process(Span<byte> record, int recordNumber)
        {
            if (!FileRecord.TryApplyFixups(record))
                return;
            var headerFlags = FileRecord.Flags(record);
            if ((headerFlags & FileRecord.FlagInUse) == 0)
                return;

            var baseReference = FileRecord.BaseReference(record);
            var baseRecord = baseReference & FileRecord.RecordNumberMask;
            int target;
            if (baseReference == 0)
            {
                target = recordNumber;
                _flags[target] |= (byte)(InUse | ((headerFlags & FileRecord.FlagDirectory) != 0 ? Directory : 0));
                _sequence[target] = FileRecord.SequenceNumber(record);
                InUseCount++;
            }
            else if (baseRecord < (ulong)Count)
            {
                target = (int)baseRecord;
            }
            else
            {
                return;
            }

            // Kök hariç sistem dosyalarının ($MFT, $Bitmap...) adları gerekmez.
            var wantNames = target >= FirstUserRecord;
            var attributes = FileRecord.Attributes(record);
            while (attributes.MoveNext())
            {
                var attr = attributes.Current;
                switch (attributes.Type)
                {
                    case FileRecord.AttrStandardInformation:
                        if (FileRecord.TryGetResidentValue(attr, out var info) && info.Length >= 0x24)
                        {
                            var fileTime = BinaryPrimitives.ReadInt64LittleEndian(info[8..]);
                            if (fileTime > 0 && fileTime <= MaxFileTime)
                                _modified[target] = DateTime.FromFileTimeUtc(fileTime).Ticks;
                            if ((BinaryPrimitives.ReadUInt32LittleEndian(info[0x20..]) & FileAttributeReparsePoint) != 0)
                                _flags[target] |= ReparsePoint;
                        }
                        break;

                    case FileRecord.AttrFileName:
                        if (wantNames && FileRecord.TryGetResidentValue(attr, out var fileName) && fileName.Length >= 0x42)
                            AddFileName(target, fileName);
                        break;

                    case FileRecord.AttrData:
                        // Adlandırılmış akışlar (ADS) dosya boyutuna sayılmaz.
                        if (!FileRecord.IsUnnamed(attr))
                            break;
                        if (FileRecord.TryGetResidentValue(attr, out var data))
                            _size[target] = data.Length;
                        else if (FileRecord.IsNonResident(attr) && FileRecord.StartVcn(attr) == 0)
                            _size[target] = Math.Max(0, FileRecord.DataSize(attr));
                        break;
                }
            }
        }

        private void AddFileName(int target, ReadOnlySpan<byte> value)
        {
            // Yalnızca DOS (8.3) adları atlanır; Win32, POSIX ve Win32&DOS adları birer bağlantıdır.
            var nameLength = value[0x40];
            if (value[0x41] == NamespaceDos || nameLength == 0 || 0x42 + nameLength * 2 > value.Length)
                return;
            var parent = BinaryPrimitives.ReadUInt64LittleEndian(value) & FileRecord.RecordNumberMask;
            if (parent >= (ulong)Count)
                return;

            var name = Encoding.Unicode.GetString(value.Slice(0x42, nameLength * 2));
            if (_name[target] is null)
            {
                _name[target] = name;
                _parent[target] = (int)parent;
            }
            else
            {
                _extraLinks.Add((target, (int)parent, name));
            }
        }

        /// <summary>
        /// Kökten (kayıt 5) başlayarak genişlik öncelikli dolaşır; böylece ebeveynler çocuklarından önce
        /// eklenir. Köke ulaşamayan kayıtlar ($Extend altındakiler, yetimler) indekse girmez.
        /// </summary>
        public FileIndex BuildIndex(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            if ((_flags[RootRecord] & (InUse | Directory)) != (InUse | Directory))
                throw new InvalidDataException("NTFS kök klasörü (kayıt 5) bulunamadı.");

            // Bağlantı kimliği: < Count ise kaydın ilk adı, değilse _extraLinks[kimlik - Count].
            // Çocuk listeleri CSR biçiminde: ebeveynin çocukları children[start[p]..start[p + 1]].
            var start = new int[Count + 1];
            for (var r = FirstUserRecord; r < Count; r++)
            {
                if (IsLinkable(r))
                    start[_parent[r] + 1]++;
            }
            foreach (var link in _extraLinks)
            {
                if ((_flags[link.Record] & InUse) != 0)
                    start[link.Parent + 1]++;
            }
            for (var p = 0; p < Count; p++)
                start[p + 1] += start[p];

            var children = new int[start[Count]];
            var fill = start.AsSpan(0, Count).ToArray();
            for (var r = FirstUserRecord; r < Count; r++)
            {
                if (IsLinkable(r))
                    children[fill[_parent[r]]++] = r;
            }
            for (var i = 0; i < _extraLinks.Count; i++)
            {
                if ((_flags[_extraLinks[i].Record] & InUse) != 0)
                    children[fill[_extraLinks[i].Parent]++] = Count + i;
            }

            var index = new FileIndex(rootPath, children.Length + 1);
            index.SetFileReference(FileIndex.RootId, FileReference(RootRecord));
            _flags[RootRecord] |= Visited;

            var queue = new Queue<(int Record, int Id)>();
            queue.Enqueue((RootRecord, FileIndex.RootId));
            var lastReport = Environment.TickCount64;
            while (queue.Count > 0)
            {
                var (dir, dirId) = queue.Dequeue();
                for (var c = start[dir]; c < start[dir + 1]; c++)
                {
                    var link = children[c];
                    var (record, name) = link < Count ? (link, _name[link]!) : (_extraLinks[link - Count].Record, _extraLinks[link - Count].Name);
                    var flags = _flags[record];
                    var isDirectory = (flags & Directory) != 0;
                    var id = index.AddEntry(dirId, name, isDirectory, isDirectory ? 0 : _size[record],
                        _modified[record], (flags & ReparsePoint) != 0);
                    index.SetFileReference(id, FileReference(record));

                    // Bir klasör yalnızca bir kez açılır (bozuk kayıtlardaki döngülere karşı).
                    if (isDirectory && (flags & Visited) == 0)
                    {
                        _flags[record] |= Visited;
                        queue.Enqueue((record, id));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                var now = Environment.TickCount64;
                if (progress is not null && now - lastReport > 200)
                {
                    lastReport = now;
                    progress.Report(new ScanProgress(rootPath, index.Count, string.Empty));
                }
            }

            index.ComputeAggregates();
            index.LastUpdatedUtc = DateTime.UtcNow;
            return index;
        }

        /// <summary>Kullanımda, temel kayıt ve adı olan kayıt mı?</summary>
        private bool IsLinkable(int record) => (_flags[record] & InUse) != 0 && _name[record] is not null;

        private long FileReference(int record) => (long)((uint)record | ((ulong)_sequence[record] << 48));
    }
}
