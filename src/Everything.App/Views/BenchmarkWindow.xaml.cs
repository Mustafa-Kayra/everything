using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Everything.Core;

namespace Everything.App.Views;

public partial class BenchmarkWindow : Window
{
    private static readonly string[] Tests = { "SEQ1M Q8T1", "SEQ1M Q1T1", "RND4K Q32T1", "RND4K Q1T1" };

    private readonly string _root;
    private readonly Dictionary<string, (TextBlock Read, TextBlock Write)> _cells = new();
    private CancellationTokenSource? _cts;

    public BenchmarkWindow(string root)
    {
        InitializeComponent();
        _root = root;
        TitleText.Text = $"{root.TrimEnd('\\')} sürücüsü";

        for (var i = 0; i < Tests.Length; i++)
        {
            var label = new TextBlock { Text = Tests[i], VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            var read = Cell();
            var write = Cell();
            Grid.SetRow(label, i + 1);
            Grid.SetRow(read, i + 1);
            Grid.SetRow(write, i + 1);
            Grid.SetColumn(read, 1);
            Grid.SetColumn(write, 2);
            ResultGrid.Children.Add(label);
            ResultGrid.Children.Add(read);
            ResultGrid.Children.Add(write);
            _cells[Tests[i]] = (read, write);
        }
    }

    private static TextBlock Cell() => new()
    {
        Text = "—",
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 15,
    };

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        _cts = new CancellationTokenSource();
        StartButton.Content = "Durdur";
        SizeCombo.IsEnabled = false;
        foreach (var (read, write) in _cells.Values)
            read.Text = write.Text = "—";
        var size = long.Parse((string)((ComboBoxItem)SizeCombo.SelectedItem).Tag);
        var progress = new Progress<BenchmarkProgress>(p =>
        {
            Progress.Value = p.Fraction;
            StatusText.Text = p.Stage;
        });

        try
        {
            var results = await DiskBenchmark.RunAsync(_root, size, TimeSpan.FromSeconds(5), progress, _cts.Token);
            foreach (var r in results)
            {
                if (_cells.TryGetValue(r.Name, out var cells))
                {
                    cells.Read.Text = r.ReadMBps.ToString("N1");
                    cells.Write.Text = r.WriteMBps.ToString("N1");
                    cells.Read.ToolTip = $"{r.ReadIops:N0} IOPS";
                    cells.Write.ToolTip = $"{r.WriteIops:N0} IOPS";
                }
            }
            StatusText.Text = "Tamamlandı. Rastgele 4K sonuçlarının üzerine gelince IOPS değerleri görünür.";
            Progress.Value = 1;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Durduruldu.";
            Progress.Value = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Test yapılamadı: " + ex.Message;
            Progress.Value = 0;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            StartButton.Content = "Başlat";
            SizeCombo.IsEnabled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e) => _cts?.Cancel();
}
