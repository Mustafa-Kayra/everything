using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;

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

    /// <summary>Onay alıp öğeleri Geri Dönüşüm Kutusu'na taşır. Taşınanların sayısını döndürür.</summary>
    public static int MoveToRecycleBin(IReadOnlyList<string> paths, Window? owner, bool confirm = true)
    {
        if (paths.Count == 0)
            return 0;
        if (confirm)
        {
            var text = paths.Count == 1
                ? $"Bu öğe Geri Dönüşüm Kutusu'na taşınsın mı?\n\n{paths[0]}"
                : $"{paths.Count} öğe Geri Dönüşüm Kutusu'na taşınsın mı?\n\n" + string.Join("\n", paths.Take(8)) + (paths.Count > 8 ? "\n…" : string.Empty);
            var answer = owner is null
                ? MessageBox.Show(text, "Sil", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(owner, text, "Sil", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return 0;
        }

        var moved = 0;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                moved++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message + "\n\n" + path, "Silinemedi", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        return moved;
    }
}
