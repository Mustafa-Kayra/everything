using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Everything.Core.Ntfs;

/// <summary>NTFS birimlerine ham erişim (yönetici gerekir).</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsVolume
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    public static bool IsNtfs(string rootPath)
    {
        try
        {
            var drive = new DriveInfo(rootPath);
            return drive.IsReady && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"C:\" → \\.\C: birim tanıtıcısı.</summary>
    public static SafeFileHandle OpenVolume(string rootPath)
    {
        var letter = char.ToUpperInvariant(rootPath[0]);
        var handle = CreateFile($@"\\.\{letter}:", GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new UnauthorizedAccessException($"{letter}: birimi açılamadı (yönetici gerekir): " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        return handle;
    }

    /// <summary>MFT okuyucu için ham birim akışı (okumalar sektör hizalı olmalıdır).</summary>
    public static Stream OpenVolumeStream(string rootPath) => new VolumeStream(OpenVolume(rootPath));

    private sealed class VolumeStream : Stream
    {
        private readonly SafeFileHandle _handle;
        private long _position;

        public VolumeStream(SafeFileHandle handle) => _handle = handle;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override unsafe int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override unsafe int Read(Span<byte> buffer)
        {
            if (!SetFilePointerEx(_handle, _position, out _, 0))
                throw new IOException("Birimde konumlanılamadı: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            fixed (byte* p = buffer)
            {
                if (!ReadFile(_handle, p, buffer.Length, out var read, IntPtr.Zero))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 38) // ERROR_HANDLE_EOF
                        return 0;
                    throw new IOException("Birim okunamadı: " + new Win32Exception(error).Message);
                }
                _position += read;
                return read;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => throw new NotSupportedException(),
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _handle.Dispose();
            base.Dispose(disposing);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFilePointerEx(SafeFileHandle handle, long distance, out long newPosition, uint method);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(SafeFileHandle handle, byte* buffer, int count, out int read, IntPtr overlapped);
}

/// <summary>Windows NTFS USN günlüğü (FSCTL_QUERY/READ_USN_JOURNAL).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsUsnJournal : IUsnJournal
{
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlCreateUsnJournal = 0x000900E7;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalEntryDeleted = 1181;
    private const int ErrorHandleEof = 38;
    private const int BufferSize = 256 * 1024;
    private const int MaxRecordsPerRead = 200_000;

    private readonly SafeFileHandle _volume;
    private readonly string _rootPath;
    private readonly byte[] _buffer = new byte[BufferSize];

    public WindowsUsnJournal(string rootPath)
    {
        _rootPath = rootPath;
        _volume = WindowsVolume.OpenVolume(rootPath);
        if (!IsActive())
        {
            // Günlük kapalıysa aç (sistem sürücüsünde her zaman açıktır).
            var create = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(create, 32UL * 1024 * 1024);
            BinaryPrimitives.WriteUInt64LittleEndian(create.AsSpan(8), 8UL * 1024 * 1024);
            Control(FsctlCreateUsnJournal, create, Span<byte>.Empty, out _);
            Query();
        }
    }

    private bool IsActive()
    {
        try
        {
            Query();
            return true;
        }
        catch (UsnJournalResetException)
        {
            return false;
        }
    }

    public UsnJournalState Query()
    {
        Span<byte> output = stackalloc byte[80];
        var error = Control(FsctlQueryUsnJournal, Span<byte>.Empty, output, out _);
        if (error is ErrorJournalNotActive or ErrorJournalDeleteInProgress)
            throw new UsnJournalResetException("USN günlüğü etkin değil.");
        if (error != 0)
            throw new IOException("USN günlüğü sorgulanamadı: " + new Win32Exception(error).Message);
        return new UsnJournalState(
            BinaryPrimitives.ReadUInt64LittleEndian(output),
            BinaryPrimitives.ReadInt64LittleEndian(output[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[16..]));
    }

    public List<UsnRecord> Read(ulong journalId, long startUsn, out long nextUsn, CancellationToken cancellationToken)
    {
        var records = new List<UsnRecord>();
        nextUsn = startUsn;
        Span<byte> input = stackalloc byte[40];

        while (records.Count < MaxRecordsPerRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // READ_USN_JOURNAL_DATA_V0
            BinaryPrimitives.WriteInt64LittleEndian(input, nextUsn);
            BinaryPrimitives.WriteUInt32LittleEndian(input[8..], 0xFFFFFFFF); // tüm nedenler
            BinaryPrimitives.WriteUInt32LittleEndian(input[12..], 0); // ReturnOnlyOnClose
            BinaryPrimitives.WriteUInt64LittleEndian(input[16..], 0); // Timeout
            BinaryPrimitives.WriteUInt64LittleEndian(input[24..], 0); // BytesToWaitFor
            BinaryPrimitives.WriteUInt64LittleEndian(input[32..], journalId);

            var error = Control(FsctlReadUsnJournal, input, _buffer, out var returned);
            if (error == ErrorHandleEof)
                break;
            if (error != 0)
            {
                if (error is ErrorJournalEntryDeleted or ErrorJournalNotActive or ErrorJournalDeleteInProgress || Query().JournalId != journalId)
                    throw new UsnJournalResetException("USN günlüğü sıfırlanmış: " + new Win32Exception(error).Message);
                throw new IOException("USN günlüğü okunamadı: " + new Win32Exception(error).Message);
            }
            if (returned < 8)
                break;

            var next = BinaryPrimitives.ReadInt64LittleEndian(_buffer);
            var offset = 8;
            while (offset + 60 <= returned)
            {
                var span = _buffer.AsSpan(offset, returned - offset);
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span);
                if (length <= 0 || length > span.Length)
                    break;
                var major = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
                if (major == 2)
                {
                    var reference = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
                    var parent = BinaryPrimitives.ReadInt64LittleEndian(span[16..]);
                    var usn = BinaryPrimitives.ReadInt64LittleEndian(span[24..]);
                    var reason = (UsnReason)BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                    var attributes = BinaryPrimitives.ReadUInt32LittleEndian(span[52..]);
                    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[56..]);
                    var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span[58..]);
                    var name = MemoryMarshal.Cast<byte, char>(span.Slice(nameOffset, nameLength)).ToString();
                    records.Add(new UsnRecord(usn, reference, parent, reason, name, (attributes & 0x10) != 0));
                }
                offset += length;
            }

            if (next <= nextUsn)
                break;
            nextUsn = next;
            if (returned <= 8)
                break;
        }
        return records;
    }

    public string? ResolvePath(long fileReference)
    {
        // FILE_ID_DESCRIPTOR: dwSize, Type (0 = FileIdType), 16 baytlık birleşim.
        Span<byte> descriptor = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 24);
        BinaryPrimitives.WriteInt64LittleEndian(descriptor[8..], fileReference);
        using var handle = OpenFileById(_volume, ref MemoryMarshal.GetReference(descriptor), 0x80 /* FILE_READ_ATTRIBUTES */, 0x7, IntPtr.Zero, 0x02000000 /* BACKUP_SEMANTICS */);
        if (handle.IsInvalid)
            return null;

        var buffer = new char[1024];
        uint length;
        unsafe
        {
            fixed (char* p = buffer)
                length = GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length)
            return null;
        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return null;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        return path.Length == 2 && path[1] == ':' ? path + "\\" : path;
    }

    public void Dispose() => _volume.Dispose();

    public override string ToString() => $"USN günlüğü ({_rootPath})";

    private unsafe int Control(uint code, ReadOnlySpan<byte> input, Span<byte> output, out int returned)
    {
        fixed (byte* inPtr = input)
        fixed (byte* outPtr = output)
        {
            var ok = DeviceIoControl(_volume, code, inPtr, input.Length, outPtr, output.Length, out returned, IntPtr.Zero);
            return ok ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, byte* input, int inputSize, byte* output, int outputSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle OpenFileById(SafeFileHandle volume, ref byte fileId, uint access, uint share, IntPtr security, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);
}
