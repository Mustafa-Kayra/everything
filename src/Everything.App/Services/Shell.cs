using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Everything.App.Native;

namespace Everything.App.Services;

/// <summary>Dosya açma, Gezgin'de gösterme, panoya kopyalama ve Geri Dönüşüm Kutusu.</summary>
internal static class Shell
{
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Açılamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Açılamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Kopyalanamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Onay alıp öğeleri Geri Dönüşüm Kutusu'na taşır. Geri Dönüşüm Kutusu olmayan sürücülerde
    /// (USB bellek, ağ) ya da kutuya sığmayan dosyalarda Windows kalıcı silme uyarısı gösterir.
    /// Taşınan/silinen öğe sayısını döndürür.
    /// </summary>
    public static int MoveToRecycleBin(IReadOnlyList<string> paths, Window? owner, bool confirm = true)
    {
        if (paths.Count == 0)
            return 0;
        if (confirm)
        {
            var permanent = paths.Any(p => !HasRecycleBin(p));
            var text = paths.Count == 1
                ? $"Bu öğe Geri Dönüşüm Kutusu'na taşınsın mı?\n\n{paths[0]}"
                : $"{paths.Count} öğe Geri Dönüşüm Kutusu'na taşınsın mı?\n\n" + string.Join("\n", paths.Take(8)) + (paths.Count > 8 ? "\n…" : string.Empty);
            if (permanent)
                text += "\n\nDikkat: Bazı öğeler Geri Dönüşüm Kutusu olmayan bir sürücüde (USB/ağ). Bunlar KALICI olarak silinir.";
            var answer = owner is null
                ? MessageBox.Show(text, "Sil", MessageBoxButton.YesNo, permanent ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(owner, text, "Sil", MessageBoxButton.YesNo, permanent ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return 0;
        }

        // Tek kabuk işlemi; FOF_WANTNUKEWARNING kalıcı silinecek dosyalar için ayrıca sorar.
        var operation = new Win32.ShFileOpStruct
        {
            Hwnd = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle,
            Func = Win32.FoDelete,
            From = string.Join("\0", paths) + "\0\0",
            Flags = (ushort)(Win32.FofAllowUndo | Win32.FofNoConfirmation | Win32.FofWantNukeWarning),
        };
        var result = Win32.SHFileOperation(ref operation);
        if (result != 0 && !operation.AnyOperationsAborted)
            MessageBox.Show($"Bazı öğeler silinemedi (hata kodu {result}).", "Silinemedi", MessageBoxButton.OK, MessageBoxImage.Warning);
        return paths.Count(p => !File.Exists(p) && !Directory.Exists(p));
    }

    private static bool HasRecycleBin(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return root is not null && root.Length <= 3 && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
