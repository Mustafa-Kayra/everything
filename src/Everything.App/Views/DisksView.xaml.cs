using System.Windows;
using System.Windows.Controls;
using Everything.App.Services;
using Everything.Core;

namespace Everything.App.Views;

public partial class DisksView : UserControl
{
    private bool _loaded;

    public DisksView()
    {
        InitializeComponent();
    }

    /// <summary>Sekme ilk açıldığında çağrılır.</summary>
    public void EnsureLoaded()
    {
        if (!_loaded)
            _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loaded = true;
        DisksInfo.Text = "Disk bilgileri okunuyor…";
        try
        {
            var disks = await Task.Run(DiskInfoProvider.GetDisks);
            DisksList.ItemsSource = disks;
            DisksInfo.Text = $"{disks.Count} sürücü · Toplam {ByteSize.Format(disks.Sum(d => d.TotalSize))}, " +
                             $"boş {ByteSize.Format(disks.Sum(d => d.FreeSpace))}";

            // SMART bilgilerini fiziksel disk başına bir kez oku.
            var healthByDisk = new Dictionary<uint, DiskHealth>();
            foreach (var disk in disks)
            {
                if (disk.DiskNumber is not { } number)
                {
                    disk.HealthStatus = string.Empty;
                    continue;
                }
                if (!healthByDisk.TryGetValue(number, out var health))
                {
                    health = await Task.Run(() => DiskHealthProvider.Query(number));
                    healthByDisk[number] = health;
                }
                disk.HealthRows = health.ToRows().Select(r => new HealthRow(r.Label, r.Value)).ToList();
                disk.HealthStatus = health.HasAny
                    ? string.Empty
                    : AppServices.IsElevated
                        ? "Bu disk SMART bilgisi vermiyor."
                        : "Sıcaklık, ömür ve çalışma süresi için uygulamayı yönetici olarak çalıştırın.";
            }
        }
        catch (Exception ex)
        {
            DisksInfo.Text = "Disk bilgileri okunamadı: " + ex.Message;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void OnAnalyze(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string root)
            AppServices.ShowSpaceAnalysis?.Invoke(root);
    }

    private void OnBenchmark(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string root)
            new BenchmarkWindow(root) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string root)
            Shell.Open(root);
    }
}
