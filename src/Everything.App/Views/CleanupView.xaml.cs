using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Everything.App.Services;
using Everything.Core;

namespace Everything.App.Views;

public partial class CleanupView : UserControl
{
    private readonly List<CleanupTarget> _targets = CleanupTargets.Create();
    private CancellationTokenSource? _cts;
    private bool _measured;

    public CleanupView()
    {
        InitializeComponent();
        TargetList.ItemsSource = _targets;
    }

    /// <summary>Sekme ilk açıldığında boyutları hesaplar.</summary>
    public void EnsureMeasured()
    {
        if (!_measured)
            _ = MeasureAsync();
    }

    private async Task MeasureAsync()
    {
        _measured = true;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        ScanButton.IsEnabled = CleanButton.IsEnabled = false;
        StatusText.Text = "Boyutlar hesaplanıyor…";
        foreach (var target in _targets)
        {
            target.Size = null;
            target.Status = "hesaplanıyor…";
        }

        try
        {
            foreach (var target in _targets)
            {
                target.Size = await Task.Run(() => CleanupTargets.Measure(target, cts.Token), cts.Token);
                target.Status = string.Empty;
            }
            StatusText.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ScanButton.IsEnabled = true;
            UpdateCleanButton();
        }
    }

    private void UpdateCleanButton()
    {
        var selected = _targets.Where(t => t.IsChecked).ToList();
        var total = selected.Sum(t => t.Size ?? 0);
        CleanButton.IsEnabled = selected.Count > 0;
        CleanButtonText.Text = total > 0 ? $"Seçilenleri temizle ({ByteSize.Format(total)})" : "Seçilenleri temizle";
        Summary.Text = $"Toplam {ByteSize.Format(_targets.Where(t => t.CanClean).Sum(t => t.Size ?? 0))} temizlenebilir. Kullanımdaki dosyalar atlanır; belgelerinize dokunulmaz.";
    }

    private void OnTargetToggled(object sender, RoutedEventArgs e) => UpdateCleanButton();

    private void OnScan(object sender, RoutedEventArgs e) => _ = MeasureAsync();

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var selected = _targets.Where(t => t.IsChecked).ToList();
        if (selected.Count == 0)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this),
            "Şunlar kalıcı olarak silinecek (Geri Dönüşüm Kutusu'na gitmez):\n\n" + string.Join("\n", selected.Select(t => "• " + t.Name)),
            "Temizlik", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
            return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        ScanButton.IsEnabled = CleanButton.IsEnabled = false;
        long freed = 0;
        var skipped = 0;
        try
        {
            foreach (var target in selected)
            {
                target.Status = "temizleniyor…";
                var (f, s) = await Task.Run(() => CleanupTargets.Clean(target, cts.Token), cts.Token);
                freed += f;
                skipped += s;
                target.Status = s > 0 ? $"{s:N0} dosya kullanımda" : "temizlendi";
                target.Size = await Task.Run(() => CleanupTargets.Measure(target, cts.Token), cts.Token);
            }
            StatusText.Text = skipped > 0
                ? $"{ByteSize.Format(freed)} boşaltıldı. {skipped:N0} dosya kullanımda olduğu için atlandı."
                : $"{ByteSize.Format(freed)} boşaltıldı.";
            AppServices.IndexChangedFlag = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ScanButton.IsEnabled = true;
            UpdateCleanButton();
        }
    }

    private void OnReview(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CleanupTarget { ReviewQuery: { } query })
            AppServices.ShowSearch?.Invoke(query, true);
    }

    private void OnDiskCleanup(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cleanmgr.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Açılamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
