using System.Buffers.Binary;
using System.Management;
using System.Runtime.InteropServices;
using Everything.Core;
using Microsoft.Win32.SafeHandles;

namespace Everything.App.Services;

/// <summary>Disk sağlık sayaçları (SMART). Alanlar okunamadıysa null'dır.</summary>
public sealed class DiskHealth
{
    public int? TemperatureC { get; set; }
    public int? TemperatureMaxC { get; set; }

    /// <summary>SSD'nin tükenen ömrü (%). Kalan ömür = 100 - bu değer.</summary>
    public int? WearPercent { get; set; }

    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    public long? BytesWritten { get; set; }
    public long? BytesRead { get; set; }
    public long? ReadErrors { get; set; }
    public long? WriteErrors { get; set; }
    public long? MediaErrors { get; set; }
    public int? AvailableSparePercent { get; set; }
    public bool CriticalWarning { get; set; }

    public bool HasAny =>
        TemperatureC is not null || WearPercent is not null || PowerOnHours is not null || BytesWritten is not null;

    public List<(string Label, string Value)> ToRows()
    {
        var rows = new List<(string, string)>();
        if (WearPercent is { } wear)
            rows.Add(("Kalan ömür", $"%{Math.Max(0, 100 - wear)}"));
        if (TemperatureC is { } t)
            rows.Add(("Sıcaklık", TemperatureMaxC is { } max and > 0 ? $"{t} °C (en fazla {max} °C)" : $"{t} °C"));
        if (PowerOnHours is { } hours)
            rows.Add(("Çalışma süresi", hours >= 48 ? $"{hours:N0} saat (~{hours / 24.0:N0} gün)" : $"{hours:N0} saat"));
        if (PowerCycles is { } cycles)
            rows.Add(("Açılıp kapanma", $"{cycles:N0} kez"));
        if (BytesWritten is { } written)
            rows.Add(("Toplam yazılan", ByteSize.Format(written)));
        if (BytesRead is { } read)
            rows.Add(("Toplam okunan", ByteSize.Format(read)));
        if (AvailableSparePercent is { } spare)
            rows.Add(("Yedek alan", $"%{spare}"));
        var errors = (ReadErrors ?? 0) + (WriteErrors ?? 0) + (MediaErrors ?? 0);
        if (ReadErrors is not null || WriteErrors is not null || MediaErrors is not null)
            rows.Add(("Hatalar", errors == 0 ? "Yok" : $"{errors:N0}"));
        if (CriticalWarning)
            rows.Add(("Uyarı", "Disk kritik uyarı bildiriyor — yedek alın"));
        return rows;
    }
}

/// <summary>
/// SMART bilgilerini Windows Storage API (MSFT_StorageReliabilityCounter, yönetici gerekir)
/// ve NVMe sağlık günlüğünden (IOCTL_STORAGE_QUERY_PROPERTY, çoğu sistemde yöneticisiz de çalışır) okur.
/// </summary>
internal static class DiskHealthProvider
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    public static DiskHealth Query(uint diskNumber)
    {
        var health = new DiskHealth();
        TryReadReliabilityCounter(diskNumber, health);
        TryReadNvmeLog(diskNumber, health);
        return health;
    }

    private static void TryReadReliabilityCounter(uint diskNumber, DiskHealth health)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                $"SELECT * FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'");
            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    foreach (ManagementObject counter in disk.GetRelated("MSFT_StorageReliabilityCounter"))
                    {
                        using (counter)
                        {
                            health.TemperatureC ??= ToInt(counter["Temperature"]);
                            health.TemperatureMaxC ??= ToInt(counter["TemperatureMax"]);
                            health.WearPercent ??= ToInt(counter["Wear"]);
                            health.PowerOnHours ??= ToLong(counter["PowerOnHours"]);
                            health.PowerCycles ??= ToLong(counter["StartStopCycleCount"]);
                            health.ReadErrors ??= ToLong(counter["ReadErrorsTotal"]);
                            health.WriteErrors ??= ToLong(counter["WriteErrorsTotal"]);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidCastException)
        {
            // Yönetici değilse erişim reddedilir; NVMe günlüğü yine de denenir.
        }

        // Bazı sürücüler okunamayan alanlar için 0 döndürür.
        if (health.TemperatureC == 0)
            health.TemperatureC = null;
        if (health.TemperatureMaxC == 0)
            health.TemperatureMaxC = null;
        if (health.PowerOnHours == 0)
            health.PowerOnHours = null;
    }

    private static void TryReadNvmeLog(uint diskNumber, DiskHealth health)
    {
        using var handle = CreateFile($@"\\.\PhysicalDrive{diskNumber}", 0, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return;

        // STORAGE_PROPERTY_QUERY + STORAGE_PROTOCOL_SPECIFIC_DATA + 512 bayt günlük.
        const int headerSize = 8;
        const int protocolDataSize = 40;
        const int logSize = 512;
        var buffer = new byte[headerSize + protocolDataSize + logSize];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, 50); // StorageDeviceProtocolSpecificProperty
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 0); // PropertyStandardQuery
        var p = span[headerSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, 3); // ProtocolTypeNvme
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], 2); // NVMeDataTypeLogPage
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], 2); // NVME_LOG_PAGE_HEALTH_INFO
        BinaryPrimitives.WriteInt32LittleEndian(p[12..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(p[16..], protocolDataSize); // ProtocolDataOffset
        BinaryPrimitives.WriteInt32LittleEndian(p[20..], logSize); // ProtocolDataLength

        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero)
            || returned < headerSize + protocolDataSize + 200)
            return;

        // Çıktı: STORAGE_PROTOCOL_DATA_DESCRIPTOR (Version, Size, ProtocolSpecificData) + günlük.
        var descriptor = buffer.AsSpan(8, protocolDataSize);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor[16..]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(descriptor[20..]);
        if (offset <= 0 || length < 200 || 8 + offset + 200 > returned)
            return;
        var log = buffer.AsSpan(8 + offset);

        health.CriticalWarning = log[0] != 0;
        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        if (kelvin > 200)
            health.TemperatureC ??= kelvin - 273;
        health.AvailableSparePercent = log[3];
        health.WearPercent ??= Math.Min(255, (int)log[5]);
        // Veri birimleri 1000 × 512 bayttır.
        health.BytesRead = (long)Math.Min(long.MaxValue / 512000, BinaryPrimitives.ReadUInt64LittleEndian(log[32..])) * 512000;
        health.BytesWritten = (long)Math.Min(long.MaxValue / 512000, BinaryPrimitives.ReadUInt64LittleEndian(log[48..])) * 512000;
        health.PowerCycles ??= (long)BinaryPrimitives.ReadUInt64LittleEndian(log[112..]);
        health.PowerOnHours ??= (long)BinaryPrimitives.ReadUInt64LittleEndian(log[128..]);
        health.MediaErrors = (long)BinaryPrimitives.ReadUInt64LittleEndian(log[160..]);
    }

    private static int? ToInt(object? value) => value is null ? null : Convert.ToInt32(value);

    private static long? ToLong(object? value) => value is null ? null : Convert.ToInt64(value);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
