using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Everything.App.Services;

/// <summary>
/// Fiziksel dosya kimliği (birim seri no + dosya dizini). Aynı kimliğe sahip yollar sabit
/// bağlantıdır (aynı dosya); kopya bulucu bunları kopya saymaz.
/// </summary>
internal static class FileIdentity
{
    // FILETIME 4 bayt hizalıdır; long alanlar için Pack = 4 gerekir (aksi halde yapı 4 bayt kayar).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    public static string? Get(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info))
                return null;
            // Tek bağlantılı dosyalar zaten benzersiz; kimliğe gerek yok.
            if (info.NumberOfLinks <= 1)
                return null;
            return $"{info.VolumeSerialNumber:x8}-{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
