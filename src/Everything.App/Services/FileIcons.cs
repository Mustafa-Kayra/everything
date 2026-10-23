using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Everything.App.Native;

namespace Everything.App.Services;

/// <summary>
/// Windows'un dosya türü ikonları. Çoğu tür için ikon uzantıya göre bir kez alınıp önbelleğe
/// konur; exe/lnk/ico gibi her dosyada farklı olanlar dosyanın kendisinden okunur.
/// UI iş parçacığından çağrılmalıdır.
/// </summary>
internal static class FileIcons
{
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase) { "exe", "lnk", "ico", "url", "appref-ms", "msc", "scr", "cpl" };
    private static readonly Dictionary<string, ImageSource?> ByExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> ByPath = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _folder;
    private static ImageSource? _drive;

    public static ImageSource? Get(string fullPath, bool isDirectory)
    {
        if (isDirectory)
        {
            if (fullPath.Length <= 3)
                return _drive ??= Load(fullPath, Win32.FileAttributeDirectory, useAttributes: false);
            return _folder ??= Load("folder", Win32.FileAttributeDirectory, useAttributes: true);
        }

        var ext = Path.GetExtension(fullPath).TrimStart('.');
        if (PerFileExtensions.Contains(ext))
        {
            if (ByPath.TryGetValue(fullPath, out var cached))
                return cached;
            if (ByPath.Count > 2000)
                ByPath.Clear();
            return ByPath[fullPath] = Load(fullPath, Win32.FileAttributeNormal, useAttributes: false)
                                      ?? GetByExtension(ext);
        }
        return GetByExtension(ext);
    }

    private static ImageSource? GetByExtension(string ext)
    {
        if (ByExtension.TryGetValue(ext, out var icon))
            return icon;
        return ByExtension[ext] = Load("file." + ext, Win32.FileAttributeNormal, useAttributes: true);
    }

    private static ImageSource? Load(string path, uint attributes, bool useAttributes)
    {
        var info = new Win32.ShFileInfo();
        var flags = Win32.ShgfiIcon | Win32.ShgfiSmallIcon | (useAttributes ? Win32.ShgfiUseFileAttributes : 0);
        try
        {
            var result = Win32.SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<Win32.ShFileInfo>(), flags);
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
                return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (info.Icon != IntPtr.Zero)
                Win32.DestroyIcon(info.Icon);
        }
    }
}
