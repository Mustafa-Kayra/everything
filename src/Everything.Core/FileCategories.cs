namespace Everything.Core;

/// <summary>Dosya uzantılarını kullanıcıya anlamlı kategorilere ayırır.</summary>
public static class FileCategories
{
    public const string Other = "Diğer";
    public const string NoExtension = "(uzantısız)";

    private static readonly Dictionary<string, string> Map = Build(new()
    {
        ["Video"] = "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts vob",
        ["Resim"] = "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 nef arw dng svg ico psd",
        ["Ses"] = "mp3 wav flac aac ogg m4a wma opus aiff mid midi",
        ["Belge"] = "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md csv epub",
        ["Arşiv"] = "zip rar 7z tar gz bz2 xz zst cab tgz",
        ["Disk görüntüsü"] = "iso img vhd vhdx vmdk vdi qcow2 wim esd",
        ["Program"] = "exe msi dll sys msix appx bat cmd ps1 com scr",
        ["Oyun / Paket"] = "pak bundle assets unity3d bsa ba2 vpk pck forge",
        ["Kod"] = "cs js ts tsx jsx py java c cpp h hpp go rs rb php html css scss json xml yml yaml sql sh kt swift",
        ["Geçici / Önbellek"] = "tmp temp cache log bak old dmp etl",
        ["Veritabanı"] = "db sqlite sqlite3 mdb accdb ldf mdf edb",
    });

    private static Dictionary<string, string> Build(Dictionary<string, string> source)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, extensions) in source)
        {
            foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[ext] = category;
        }
        return map;
    }

    /// <summary>"Rapor.PDF" → "pdf"; uzantı yoksa boş dize.</summary>
    public static string GetExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1)
            return string.Empty;
        return fileName[(dot + 1)..].ToLowerInvariant();
    }

    public static string GetCategory(string extension) =>
        extension.Length > 0 && Map.TryGetValue(extension, out var category) ? category : Other;
}
