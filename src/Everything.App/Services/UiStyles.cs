using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Everything.App.Services;

/// <summary>
/// Fluent temasının öğe stillerini koruyarak küçük ayarlar ekler
/// (XAML'de BasedOn ile tema stiline statik bağlanmak güvenilir değil).
/// </summary>
internal static class UiStyles
{
    /// <summary>Sıkı satırlar ve sağa hizalanabilen hücreler.</summary>
    public static void CompactList(ListView list, double rowHeight = 30)
    {
        var baseStyle = list.TryFindResource(GridView.GridViewItemContainerStyleKey) as Style
                        ?? list.TryFindResource(typeof(ListViewItem)) as Style;
        var style = new Style(typeof(ListViewItem), baseStyle);
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, rowHeight));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, rowHeight));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
        list.ItemContainerStyle = style;
    }

    /// <summary>Ağaç öğelerinin IsExpanded/IsSelected durumunu görünüm modeline bağlar.</summary>
    public static void BindTreeItems(TreeView tree)
    {
        var style = new Style(typeof(TreeViewItem), tree.TryFindResource(typeof(TreeViewItem)) as Style);
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding("IsSelected") { Mode = BindingMode.TwoWay }));
        tree.ItemContainerStyle = style;
    }
}
