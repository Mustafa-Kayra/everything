using System.Windows;
using Everything.Core;

namespace Everything.App.Services;

/// <summary>Uygulama genelinde paylaşılan nesneler.</summary>
internal static class AppServices
{
    public static AppSettings Settings { get; set; } = new();

    public static IndexManager Indexes { get; set; } = null!;

    /// <summary>Uygulama yönetici olarak mı çalışıyor (hızlı NTFS modu için gerekir).</summary>
    public static bool IsElevated => StartupManager.IsElevated;

    /// <summary>Durum çubuğu zamanlayıcısıyla (~300 ms) tetiklenir; görünümler kendi tazelemelerini burada yapar.</summary>
    public static event EventHandler? Tick;

    /// <summary>Herhangi bir sürücünün indeksi değişti (iş parçacığından bağımsız bayrak).</summary>
    public static volatile bool IndexChangedFlag;

    public static void RaiseTick() => Tick?.Invoke(null, EventArgs.Empty);

    /// <summary>Ayarlar sekmesinde bir ayar değişti.</summary>
    public static event EventHandler? SettingsChanged;

    public static void RaiseSettingsChanged() => SettingsChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>Arama sekmesine geçip verilen sorguyu çalıştırır (ikinci parametre: boyuta göre sırala).</summary>
    public static Action<string, bool>? ShowSearch { get; set; }

    /// <summary>Alan analizi sekmesine geçip verilen kökü/klasörü açar.</summary>
    public static Action<string>? ShowSpaceAnalysis { get; set; }

    public static Window? MainWindow => Application.Current.MainWindow;

    /// <summary>İndekslenen sürücü kökleri ("C:\", "D:\"…).</summary>
    public static IReadOnlyList<string> Roots => Indexes.Volumes.Select(v => v.RootPath).ToList();
}
