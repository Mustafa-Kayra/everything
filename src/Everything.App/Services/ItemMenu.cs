using System.Windows;
using System.Windows.Controls;

namespace Everything.App.Services;

/// <summary>Dosya listeleri için ortak sağ tık menüsü.</summary>
internal static class ItemMenu
{
    /// <param name="paths">Menü açıldığında seçili öğelerin tam yolları.</param>
    /// <param name="afterDelete">Silme sonrası görünümü tazelemek için.</param>
    public static ContextMenu Create(Func<IReadOnlyList<string>> paths, Action? afterDelete = null)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Aç", "", () =>
        {
            foreach (var path in paths().Take(10))
                Shell.Open(path);
        }, bold: true));
        menu.Items.Add(Item("Dosya konumunu aç", "", () =>
        {
            if (paths().FirstOrDefault() is { } path)
                Shell.ShowInExplorer(path);
        }));
        menu.Items.Add(Item("Tam yolu kopyala", "", () =>
        {
            var selected = paths();
            if (selected.Count > 0)
                Shell.CopyPath(string.Join(Environment.NewLine, selected));
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Geri Dönüşüm Kutusu'na taşı…", "", () =>
        {
            if (Shell.MoveToRecycleBin(paths(), Window.GetWindow(menu.PlacementTarget)) > 0)
                afterDelete?.Invoke();
        }));
        menu.Opened += (_, _) =>
        {
            var any = paths().Count > 0;
            foreach (var item in menu.Items.OfType<MenuItem>())
                item.IsEnabled = any;
        };
        return menu;
    }

    private static MenuItem Item(string header, string glyph, Action action, bool bold = false)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("Icon") },
        };
        if (bold)
            item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }
}
