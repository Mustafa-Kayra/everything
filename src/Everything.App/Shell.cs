using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;

namespace Everything.App;

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

    /// <summary>Onay alıp öğeyi Geri Dönüşüm Kutusu'na taşır. Başarılıysa true.</summary>
    public static bool MoveToRecycleBin(string path, Window owner)
    {
        var answer = MessageBox.Show(owner,
            $"Bu öğe Geri Dönüşüm Kutusu'na taşınsın mı?\n\n{path}",
            "Sil",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return false;

        try
        {
            if (Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Silinemedi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
