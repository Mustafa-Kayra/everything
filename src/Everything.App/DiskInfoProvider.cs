using System.IO;
using System.Management;
using Everything.Core;

namespace Everything.App;

/// <summary>Bir sürücünün (birim + fiziksel disk) özellikleri.</summary>
public sealed class DiskInfo
{
    public required string RootPath { get; init; }
    public required string Title { get; init; }
    public string VolumeLabel { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public string DriveType { get; init; } = string.Empty;
    public long TotalSize { get; init; }
    public long FreeSpace { get; init; }
    public long UsedSpace => TotalSize - FreeSpace;
    public double UsedPercent => TotalSize > 0 ? 100.0 * UsedSpace / TotalSize : 0;

    public string Model { get; init; } = "Bilinmiyor";
    public string MediaType { get; init; } = "Bilinmiyor";
    public string BusType { get; init; } = "Bilinmiyor";
    public string Health { get; init; } = "Bilinmiyor";
    public string SerialNumber { get; init; } = string.Empty;
    public long PhysicalSize { get; init; }
    public string VolumeSerial { get; init; } = string.Empty;
    public bool IsSystemDrive { get; init; }

    public string UsageText =>
        $"{ByteSize.Format(UsedSpace)} kullanılıyor  •  {ByteSize.Format(FreeSpace)} boş  •  Toplam {ByteSize.Format(TotalSize)}";

    public string UsedPercentText => UsedPercent.ToString("0.#") + " % dolu";

    public string PhysicalSizeText => PhysicalSize > 0 ? ByteSize.Format(PhysicalSize) : "—";

    public string HealthIcon => Health switch
    {
        "Sağlıklı" => "✅",
        "Uyarı" => "⚠️",
        "Sağlıksız" => "❌",
        _ => "❔",
    };
}

/// <summary>Sürücü bilgilerini <see cref="DriveInfo"/> ve WMI (Storage API) üzerinden toplar.</summary>
internal static class DiskInfoProvider
{
    private sealed record PhysicalDisk(string Model, string MediaType, string BusType, string Health, string Serial, long Size);

    public static List<DiskInfo> GetDisks()
    {
        var letterToDisk = QueryPartitions();
        var physical = QueryPhysicalDisks();
        var volumeSerials = QueryVolumeSerials();
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";

        var result = new List<DiskInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;
            try
            {
                var letter = char.ToUpperInvariant(drive.Name[0]);
                PhysicalDisk? disk = null;
                if (letterToDisk.TryGetValue(letter, out var number))
                    physical.TryGetValue(number, out disk);

                result.Add(new DiskInfo
                {
                    RootPath = drive.RootDirectory.FullName,
                    Title = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"Yerel Disk ({drive.Name.TrimEnd('\\')})" : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})",
                    VolumeLabel = drive.VolumeLabel,
                    FileSystem = drive.DriveFormat,
                    DriveType = DriveTypeName(drive.DriveType),
                    TotalSize = drive.TotalSize,
                    FreeSpace = drive.TotalFreeSpace,
                    Model = disk?.Model ?? "Bilinmiyor",
                    MediaType = disk?.MediaType ?? "Bilinmiyor",
                    BusType = disk?.BusType ?? "Bilinmiyor",
                    Health = disk?.Health ?? "Bilinmiyor",
                    SerialNumber = disk?.Serial ?? string.Empty,
                    PhysicalSize = disk?.Size ?? 0,
                    VolumeSerial = volumeSerials.TryGetValue(letter, out var vs) ? vs : string.Empty,
                    IsSystemDrive = string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return result;
    }

    private static string DriveTypeName(DriveType type) => type switch
    {
        System.IO.DriveType.Fixed => "Yerel disk",
        System.IO.DriveType.Removable => "Çıkarılabilir disk",
        System.IO.DriveType.Network => "Ağ sürücüsü",
        System.IO.DriveType.CDRom => "CD/DVD",
        System.IO.DriveType.Ram => "RAM disk",
        _ => "Diğer",
    };

    /// <summary>Sürücü harfi → fiziksel disk numarası.</summary>
    private static Dictionary<char, uint> QueryPartitions()
    {
        var map = new Dictionary<char, uint>();
        Query(@"root\Microsoft\Windows\Storage", "SELECT DriveLetter, DiskNumber FROM MSFT_Partition", obj =>
        {
            var letterObj = obj["DriveLetter"];
            if (letterObj is null)
                return;
            var letter = letterObj is char c ? c : (char)Convert.ToUInt16(letterObj);
            if (char.IsLetter(letter))
                map[char.ToUpperInvariant(letter)] = Convert.ToUInt32(obj["DiskNumber"]);
        });
        return map;
    }

    private static Dictionary<uint, PhysicalDisk> QueryPhysicalDisks()
    {
        var map = new Dictionary<uint, PhysicalDisk>();
        Query(@"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, Model, MediaType, BusType, HealthStatus, SerialNumber, Size, SpindleSpeed FROM MSFT_PhysicalDisk",
            obj =>
            {
                if (!uint.TryParse(obj["DeviceId"]?.ToString(), out var number))
                    return;
                var model = (obj["FriendlyName"] ?? obj["Model"])?.ToString()?.Trim() ?? "Bilinmiyor";
                var media = Convert.ToUInt16(obj["MediaType"] ?? 0) switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    _ => SpindleGuess(obj["SpindleSpeed"]),
                };
                var bus = Convert.ToUInt16(obj["BusType"] ?? 0) switch
                {
                    1 => "SCSI",
                    2 => "ATAPI",
                    3 => "ATA",
                    4 => "IEEE 1394",
                    6 => "Fibre Channel",
                    7 => "USB",
                    8 => "RAID",
                    9 => "iSCSI",
                    10 => "SAS",
                    11 => "SATA",
                    12 => "SD",
                    13 => "MMC",
                    14 or 15 => "Sanal",
                    16 => "Depolama Alanları",
                    17 => "NVMe",
                    _ => "Bilinmiyor",
                };
                var health = Convert.ToUInt16(obj["HealthStatus"] ?? 5) switch
                {
                    0 => "Sağlıklı",
                    1 => "Uyarı",
                    2 => "Sağlıksız",
                    _ => "Bilinmiyor",
                };
                var size = obj["Size"] is null ? 0 : (long)Convert.ToUInt64(obj["Size"]);
                map[number] = new PhysicalDisk(model, media, bus, health, obj["SerialNumber"]?.ToString()?.Trim() ?? string.Empty, size);
            });
        return map;
    }

    private static string SpindleGuess(object? spindle)
    {
        if (spindle is null)
            return "Bilinmiyor";
        var rpm = Convert.ToUInt32(spindle);
        return rpm == 0 ? "SSD" : rpm == uint.MaxValue ? "Bilinmiyor" : "HDD";
    }

    private static Dictionary<char, string> QueryVolumeSerials()
    {
        var map = new Dictionary<char, string>();
        Query(@"root\cimv2", "SELECT DeviceID, VolumeSerialNumber FROM Win32_LogicalDisk", obj =>
        {
            var id = obj["DeviceID"]?.ToString();
            var serial = obj["VolumeSerialNumber"]?.ToString();
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(serial))
                map[char.ToUpperInvariant(id[0])] = serial.Length == 8 ? serial[..4] + "-" + serial[4..] : serial;
        });
        return map;
    }

    private static void Query(string scope, string wql, Action<ManagementBaseObject> handle)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                using (obj)
                {
                    try
                    {
                        handle(obj);
                    }
                    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ManagementException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            // WMI kullanılamıyorsa yalnızca temel bilgiler gösterilir.
        }
    }
}
