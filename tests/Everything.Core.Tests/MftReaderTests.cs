using System.Buffers.Binary;
using System.IO.Compression;
using Everything.Core.Ntfs;

namespace Everything.Core.Tests;

/// <summary>
/// Testlerde kullanılan NTFS imajlarını (TestData/*.img.gz) geçici dosyalara açar.
/// İmajlar TestData/make-ntfs-image.sh ile üretilmiştir.
/// </summary>
public sealed class NtfsImageFixture : IDisposable
{
    private readonly Dictionary<string, string> _paths = new();
    private readonly object _gate = new();

    /// <summary>İmajın açılmış hâlinin yolunu döndürür (ilk çağrıda açılır).</summary>
    public string Get(string name)
    {
        lock (_gate)
        {
            if (_paths.TryGetValue(name, out var existing))
                return existing;
            var path = Path.Combine(Path.GetTempPath(), "everything-ntfs-" + Guid.NewGuid().ToString("N") + ".img");
            using (var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", name + ".img.gz")))
            using (var gzip = new GZipStream(source, CompressionMode.Decompress))
            using (var target = File.Create(path))
                gzip.CopyTo(target);
            _paths[name] = path;
            return path;
        }
    }

    public void Dispose()
    {
        foreach (var path in _paths.Values)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}

public class MftReaderTests : IClassFixture<NtfsImageFixture>
{
    private const string Root = "/ntfs";

    // make-ntfs-image.sh: 512 bayt sektör / 512 bayt küme (1 KB kayıtlar iki kümeye ve MFT parçalarına bölünür)
    // ve 4096 bayt sektör / 8192 bayt küme (4 KB kayıtlar).
    public static TheoryData<string, int> Images => new()
    {
        { "ntfs-512-512", 512 },
        { "ntfs-4096-8192", 4096 },
    };

    public static TheoryData<string> ImageNames => new() { "ntfs-512-512", "ntfs-4096-8192" };

    private readonly NtfsImageFixture _images;

    public MftReaderTests(NtfsImageFixture images) => _images = images;

    private FileStream Open(string image) =>
        new(_images.Get(image), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);

    private FileIndex ReadImage(string image)
    {
        using var stream = Open(image);
        return MftReader.Read(stream, Root);
    }

    private static string P(params string[] parts) => Path.Join([Root, .. parts]);

    private static int Find(FileIndex index, params string[] parts)
    {
        var id = index.FindPath(P(parts));
        Assert.True(id != FileIndex.None, "Bulunamadı: " + P(parts));
        return id;
    }

    [Theory]
    [MemberData(nameof(ImageNames))]
    public void Read_BuildsExpectedTree(string image)
    {
        var index = ReadImage(image);

        Assert.Equal(Root, index.GetName(FileIndex.RootId));
        // 3053 dosya (40 ek sabit bağlantı dahil) + 9 klasör
        Assert.Equal(3062, index.Count);
        Assert.Equal(3053, index.GetFileCount(FileIndex.RootId));
        Assert.Equal(16_296_739, index.GetSize(FileIndex.RootId));

        var files = new (string Path, long Size)[]
        {
            ("empty.txt", 0),
            ("tiny.txt", 100),
            ("docs/readme.md", 600),
            ("docs/nested/deep/big.bin", 3_145_728),
            ("sparse.bin", 10 * 1024 * 1024),
            ("frag/a.bin", 1_310_720),
            ("frag/b.bin", 1_310_720),
            ("Türkçe/ğüşıöç İ.txt", 1234),
            ("links/original.txt", 500),
            ("docs/hardlink.txt", 500),
            ("hl/target.bin", 777),
            ("ads.txt", 10), // Alternatif veri akışı (5000 bayt) sayılmaz.
            ("link-to-docs", 16), // ntfs-3g'nin Interix sembolik bağlantısı: sıradan bir dosya
            ("many/f0000.txt", 0),
            ("many/f0007.txt", 0),
            ("many/f1234.txt", 1234 % 7),
            ("many/f2999.txt", 2999 % 7),
        };
        foreach (var (path, size) in files)
        {
            var id = Find(index, path.Split('/'));
            Assert.False(index.IsDirectory(id), path);
            Assert.Equal(size, index.GetSize(id));
        }

        var folders = new (string Path, long Size, int Files)[]
        {
            ("docs", 3_146_828, 3),
            ("docs/nested", 3_145_728, 1),
            ("frag", 2_621_440, 2),
            ("Türkçe", 1234, 1),
            ("links", 500, 1),
            ("hl", 41 * 777, 41),
            ("many", 8994, 3000),
        };
        foreach (var (path, size, count) in folders)
        {
            var id = Find(index, path.Split('/'));
            Assert.True(index.IsDirectory(id), path);
            Assert.Equal(size, index.GetSize(id));
            Assert.Equal(count, index.GetFileCount(id));
        }
        Assert.Equal(3000, index.GetChildren(Find(index, "many")).Count());
    }

    [Theory]
    [MemberData(nameof(ImageNames))]
    public void Read_SkipsMetadataAndDeletedRecords(string image)
    {
        var index = ReadImage(image);

        foreach (var name in new[] { "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend", "deleted.txt" })
            Assert.Equal(FileIndex.None, index.FindPath(P(name)));
        Assert.DoesNotContain(index.GetChildren(FileIndex.RootId), id => index.GetName(id).StartsWith('$'));
        Assert.Equal(FileIndex.None, index.FindPath(P("$Extend", "$Reparse")));
    }

    [Theory]
    [MemberData(nameof(ImageNames))]
    public void Read_HardLinksAppearOncePerLink(string image)
    {
        var index = ReadImage(image);

        var original = Find(index, "links", "original.txt");
        var link = Find(index, "docs", "hardlink.txt");
        Assert.NotEqual(original, link);
        Assert.Equal(index.GetFileReference(original), index.GetFileReference(link));

        // 41 adlı dosyanın $FILE_NAME öznitelikleri ek kayıtlara ($ATTRIBUTE_LIST) taşmıştır.
        var target = Find(index, "hl", "target.bin");
        var hl = Find(index, "hl");
        var children = index.GetChildren(hl).ToList();
        Assert.Equal(41, children.Count);
        Assert.All(children, id =>
        {
            Assert.Equal(777, index.GetSize(id));
            Assert.Equal(index.GetFileReference(target), index.GetFileReference(id));
        });
        Find(index, "hl", "link-with-a-rather-long-name-to-fill-the-mft-record-40.bin");
    }

    [Theory]
    [MemberData(nameof(ImageNames))]
    public void Read_ReadsTimesReparsePointsAndReferences(string image)
    {
        var index = ReadImage(image);

        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), index.GetModifiedUtc(Find(index, "tiny.txt")));
        Assert.Equal(new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc), index.GetModifiedUtc(Find(index, "docs", "nested", "deep", "big.bin")));
        Assert.Equal(new DateTime(2019, 11, 12, 13, 14, 15, DateTimeKind.Utc), index.GetModifiedUtc(Find(index, "docs")));
        // Diğer kayıtlar imaj oluşturulurken yazıldı.
        var readme = index.GetModifiedUtc(Find(index, "docs", "readme.md"));
        Assert.InRange(readme, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow);

        var junction = Find(index, "junction");
        Assert.True(index.IsReparsePoint(junction));
        Assert.True(index.IsDirectory(junction));
        Assert.False(index.IsReparsePoint(Find(index, "docs")));

        // Kök klasör kayıt 5'tir; tüm kayıtların referansı kayıt no + sıra no içerir.
        Assert.Equal(5, index.GetFileReference(FileIndex.RootId) & 0xFFFF_FFFF_FFFF);
        foreach (var id in index.PreOrder(FileIndex.RootId).Skip(1))
        {
            var reference = index.GetFileReference(id);
            Assert.True((reference & 0xFFFF_FFFF_FFFF) >= 16, index.GetFullPath(id));
            Assert.True(reference >> 48 != 0, index.GetFullPath(id));
        }
        // Sabit bağlantılar aynı referansı paylaşır: hl/ altında 41 ad tek kayıt, links/docs çifti tek kayıt.
        Assert.Equal(index.Count + 1 - 40 - 1, index.BuildFileReferenceMap().Count);
    }

    [Theory]
    [MemberData(nameof(Images))]
    public void Read_OnlyIssuesSectorAlignedReads(string image, int sectorSize)
    {
        using var inner = Open(image);
        using var stream = new SectorAlignedStream(inner, sectorSize);
        var index = MftReader.Read(stream, Root);

        Assert.Equal(3062, index.Count);
        Assert.True(stream.ReadCount > 0);
    }

    [Fact]
    public void Read_FragmentedMft_FollowsAttributeList()
    {
        // Bu imajda $MFT ~200 parçadır; aralık listesinin devamı ek kayıttadır (kayıt 15) ve
        // $MFT'nin $FILE_NAME'i başka bir ek kayda (kayıt 16) taşınmıştır.
        using var inner = Open("ntfs-fragmented-mft");
        using var stream = new SectorAlignedStream(inner, 512);
        var index = MftReader.Read(stream, Root);

        Assert.Equal(3252, index.Count);
        Assert.Equal(3250, index.GetFileCount(FileIndex.RootId));
        Assert.Equal(1250 * 800, index.GetSize(FileIndex.RootId));
        Assert.Equal(1250 * 800, index.GetSize(Find(index, "fill")));
        Assert.Equal(1250, index.GetFileCount(Find(index, "fill")));
        Assert.Equal(2000, index.GetFileCount(Find(index, "grow")));
        Assert.Equal(800, index.GetSize(Find(index, "fill", "s2500")));
        Assert.Equal(FileIndex.None, index.FindPath(P("fill", "s2499")));
        Assert.Equal(0, index.GetSize(Find(index, "grow", "n2000")));
        Assert.Equal(FileIndex.None, index.FindPath(P("$MFT")));
    }

    [Fact]
    public void Read_HandlesShortReads()
    {
        using var inner = Open("ntfs-512-512");
        using var stream = new SectorAlignedStream(inner, 512, maxReadLength: 1024);
        var index = MftReader.Read(stream, Root);

        Assert.Equal(3062, index.Count);
        Assert.Equal(16_296_739, index.GetSize(FileIndex.RootId));
    }

    [Fact]
    public void Read_ReportsProgressAndHonoursCancellation()
    {
        var reports = new List<ScanProgress>();
        using (var stream = Open("ntfs-512-512"))
            MftReader.Read(stream, Root, new SynchronousProgress<ScanProgress>(reports.Add));
        Assert.NotEmpty(reports);
        Assert.Equal(3062, reports[^1].ItemsFound);
        Assert.Equal(Root, reports[^1].RootPath);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var cancelled = Open("ntfs-512-512");
        Assert.ThrowsAny<OperationCanceledException>(() => MftReader.Read(cancelled, Root, cancellationToken: cts.Token));
    }

    [Fact]
    public void Read_NonNtfsStream_ThrowsInvalidData()
    {
        var random = new byte[1024 * 1024];
        new Random(42).NextBytes(random);
        Assert.Throws<InvalidDataException>(() => MftReader.Read(new MemoryStream(random), Root));
        Assert.Throws<InvalidDataException>(() => MftReader.Read(new MemoryStream(new byte[100]), Root));
        Assert.Throws<InvalidDataException>(() => MftReader.Read(new MemoryStream(), Root));
    }

    [Fact]
    public void Read_CorruptMft_ThrowsInvalidData()
    {
        var bytes = File.ReadAllBytes(_images.Get("ntfs-512-512"));
        var mftOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0x30)) * 512;
        bytes[mftOffset] = (byte)'X'; // "FILE" imzası bozuldu
        var ex = Assert.Throws<InvalidDataException>(() => MftReader.Read(new MemoryStream(bytes), Root));
        Assert.Contains("$MFT", ex.Message);

        // Birim MFT'nin ortasında bitiyor.
        var truncated = File.ReadAllBytes(_images.Get("ntfs-512-512")).AsSpan(0, (int)mftOffset + 64 * 1024).ToArray();
        Assert.Throws<InvalidDataException>(() => MftReader.Read(new MemoryStream(truncated), Root));
    }

    [Fact]
    public void Read_SkipsRecordWithBadFixup()
    {
        var index = ReadImage("ntfs-512-512");
        var record = index.GetFileReference(Find(index, "tiny.txt")) & 0xFFFF_FFFF_FFFF;

        var bytes = File.ReadAllBytes(_images.Get("ntfs-512-512"));
        var mftOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0x30)) * 512;
        // İlk 512 baytlık bloğun son iki baytı güncelleme sıra numarasını tutar; bozulunca kayıt geçersizdir.
        bytes[mftOffset + record * 1024 + 510] ^= 0xFF;
        var damaged = MftReader.Read(new MemoryStream(bytes), Root);

        Assert.Equal(FileIndex.None, damaged.FindPath(P("tiny.txt")));
        Assert.Equal(3061, damaged.Count);
        Assert.NotEqual(FileIndex.None, damaged.FindPath(P("docs", "readme.md")));
    }

    /// <summary>Progress&lt;T&gt; gibi senkronizasyon bağlamına göndermeden, aynı iş parçacığında bildirir.</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>
    /// Ham birim tanıtıcısı gibi davranır: sektör hizalı olmayan her okumada hata fırlatır.
    /// İsteğe bağlı olarak okumaları kısaltarak (kısa okuma) çağıranın döngüsünü sınar.
    /// </summary>
    private sealed class SectorAlignedStream(Stream inner, int sectorSize, int maxReadLength = int.MaxValue) : Stream
    {
        public int ReadCount { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("Birim tanıtıcıları uzunluk bildirmez.");

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ReadCount++;
            if (inner.Position % sectorSize != 0 || buffer.Length % sectorSize != 0)
                throw new IOException($"Hizalanmamış okuma: konum {inner.Position}, uzunluk {buffer.Length}.");
            var length = Math.Min(buffer.Length, maxReadLength);
            return inner.Read(buffer[..length]);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin != SeekOrigin.Begin)
                throw new NotSupportedException();
            return inner.Seek(offset, origin);
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
