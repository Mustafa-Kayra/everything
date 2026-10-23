using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace Everything.App.Services;

/// <summary>
/// Bildirim alanı simgesi. Sağ tık menüsü, temayla uyumlu olsun diye WinForms yerine
/// WPF ContextMenu ile gösterilir.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu;

    public TrayIcon(Action showMain, Action showQuickSearch, Action rescan, Action exit, string hotkeyText)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = AppPaths.ProductName,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                showMain();
            else if (e.Button == Forms.MouseButtons.Right)
                ShowMenu();
        };

        _menu = new ContextMenu();
        _menu.Items.Add(Item("Aç", showMain, bold: true));
        _menu.Items.Add(Item(string.IsNullOrWhiteSpace(hotkeyText) ? "Hızlı arama" : $"Hızlı arama    {hotkeyText}", showQuickSearch));
        _menu.Items.Add(Item("Yeniden tara", rescan));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Çıkış", exit));
    }

    public void UpdateHotkeyText(string hotkeyText)
    {
        if (_menu.Items[1] is MenuItem item)
            item.Header = string.IsNullOrWhiteSpace(hotkeyText) ? "Hızlı arama" : $"Hızlı arama    {hotkeyText}";
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void ShowMenu()
    {
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
        // Menü dışına tıklanınca kapanabilmesi için menüyü ön plana al.
        if (PresentationSource.FromVisual(_menu) is System.Windows.Interop.HwndSource source)
            Native.Win32.SetForegroundWindow(source.Handle);
    }

    private static MenuItem Item(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header };
        if (bold)
            item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }
}
