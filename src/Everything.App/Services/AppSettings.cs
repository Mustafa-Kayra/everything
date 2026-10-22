using System.IO;
using System.Text.Json;

namespace Everything.App.Services;

/// <summary>Kullanıcı ayarları; settings.json olarak saklanır.</summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Hızlı arama penceresini açan genel kısayol (ör. "Alt+Space"). Boşsa kapalı.</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>Pencere kapatılınca uygulama tepside çalışmaya devam etsin.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Aramada Türkçe karakter ve aksanları yok say ("sarki" → "Şarkı").</summary>
    public bool IgnoreDiacritics { get; set; } = true;

    public bool ShowPreview { get; set; } = true;

    /// <summary>
    /// Yönetici izni olmadan çalışırken indeks bu kadar günden eskiyse arka planda tazelenir (0 = asla).
    /// Yönetici olarak çalışırken USN günlüğü kullanıldığından gerekmez.
    /// </summary>
    public int RefreshIfOlderThanDays { get; set; } = 3;

    public bool AutoCheckUpdates { get; set; } = true;
    public bool IncludePrereleases { get; set; } = true;
    public DateTime LastUpdateCheckUtc { get; set; }
    public string? SkippedVersion { get; set; }

    public long DuplicateMinSize { get; set; } = 1024 * 1024;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppPaths.Log("Ayarlar okunamadı: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("Ayarlar kaydedilemedi: " + ex.Message);
        }
    }
}
