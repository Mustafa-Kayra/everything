using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Everything.App.Native;
using Everything.App.Services;

namespace Everything.App.Views;

/// <summary>Genel kısayolla her yerden açılan küçük arama penceresi.</summary>
public partial class QuickSearchWindow : Window
{
    private const int MaxResults = 50;
    private readonly DispatcherTimer _delay;
    private CancellationTokenSource? _cts;

    public QuickSearchWindow()
    {
        InitializeComponent();

        // Fluent öğe stilini koruyarak satırları sıkılaştır ve tüm genişliğe yay.
        var item = new Style(typeof(ListBoxItem), TryFindResource(typeof(ListBoxItem)) as Style);
        item.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        item.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 3, 8, 3)));
        item.Setters.Add(new Setter(MinHeightProperty, 0.0));
        Results.ItemContainerStyle = item;

        _delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            _ = SearchAsync();
        };
    }

    /// <summary>Pencereyi etkin ekranın üst üçte birinde ortalayıp öne getirir.</summary>
    public void ShowAndFocus()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height * 0.2;
        Show();
        Activate();
        Win32.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _delay.Stop();
        _delay.Start();
    }

    private async Task SearchAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            Results.ItemsSource = null;
            Results.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            // Hızlı arama için en iyi sonuçlar: ada göre sıralı, klasörler ve dosyalar birlikte.
            var (result, items) = await SearchService.RunAsync(SearchBox.Text, 5000, "Ad", false, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            var top = items
                .OrderBy(i => i.Name.StartsWith(SearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ThenBy(i => i.Name.Length)
                .Take(MaxResults)
                .ToList();
            Results.ItemsSource = top;
            Results.Visibility = top.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (top.Count > 0)
                Results.SelectedIndex = 0;
            Footer.Text = top.Count == 0
                ? "Sonuç yok"
                : $"{result.TotalMatches:N0} sonuç  ·  Enter aç  ·  Ctrl+Enter konumunu aç  ·  Ctrl+C yolu kopyala  ·  Esc kapat";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter when Results.SelectedItem is ResultItem item:
                Hide();
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    Shell.ShowInExplorer(item.FullPath);
                else
                    Shell.Open(item.FullPath);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control && SearchBox.SelectionLength == 0 && Results.SelectedItem is ResultItem item:
                Shell.CopyPath(item.FullPath);
                Footer.Text = "Yol kopyalandı: " + item.FullPath;
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (Results.Items.Count == 0)
            return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, Results.Items.Count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is ResultItem item)
        {
            Hide();
            Shell.Open(item.FullPath);
        }
    }
}
