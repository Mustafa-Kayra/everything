using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace Everything.App.Services;

/// <summary>
/// "Windows ile başlat" ayarı. Yönetici olarak çalışırken (veya kurulumda) en yüksek yetkiyle
/// çalışan bir Görev Zamanlayıcı görevi kullanılır; böylece her oturum açılışında UAC sorulmadan
/// hızlı NTFS modu etkin olur. Aksi halde kullanıcının Run kaydı kullanılır.
/// </summary>
internal static class StartupManager
{
    public const string TaskName = "Everything Arama";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "EverythingArama";

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static bool IsEnabled => HasRunEntry() || HasScheduledTask();

    public static bool IsElevatedTask => HasScheduledTask();

    /// <summary>Başlangıçta çalıştırmayı açar/kapatır. Başarısızsa hata mesajı döner.</summary>
    public static string? SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                RemoveRunEntry();
                if (HasScheduledTask())
                {
                    var error = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", elevate: !IsElevated);
                    if (error is not null)
                        return error;
                }
                return null;
            }

            if (IsElevated)
            {
                RemoveRunEntry();
                // XML ile oluştur: pille çalışırken de başlasın, süre sınırı olmasın.
                var xmlFile = Path.Combine(Path.GetTempPath(), "everything-arama-task.xml");
                File.WriteAllText(xmlFile, TaskXml(), System.Text.Encoding.Unicode);
                try
                {
                    return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlFile}\" /F", elevate: false);
                }
                finally
                {
                    File.Delete(xmlFile);
                }
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(RunValue, $"\"{AppPaths.ExePath}\" --tray");
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Herhangi bir kullanıcı oturum açınca, o kullanıcının oturumunda "mümkün olan en yüksek" yetkiyle
    /// çalışır: yöneticiler için yükseltilmiş (hızlı NTFS modu), standart kullanıcılar için normal.
    /// Kurulumu başka bir yönetici hesabıyla onaylayan standart kullanıcılar da böylece kapsanır.
    /// </summary>
    private static string TaskXml()
    {
        var exe = System.Security.SecurityElement.Escape(AppPaths.ExePath);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Everything Arama'yı oturum açılışında yönetici olarak başlatır (hızlı NTFS modu).</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><GroupId>S-1-5-32-545</GroupId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
    }

    private static bool HasRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    private static void RemoveRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static bool HasScheduledTask() => RunSchtasks($"/Query /TN \"{TaskName}\"", elevate: false) is null;

    private static string? RunSchtasks(string arguments, bool elevate)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = elevate,
            RedirectStandardError = !elevate,
            RedirectStandardOutput = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevate)
            info.Verb = "runas";
        using var process = Process.Start(info);
        if (process is null)
            return "schtasks başlatılamadı";
        var error = elevate ? string.Empty : process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return process.ExitCode == 0 ? null : string.IsNullOrWhiteSpace(error) ? $"schtasks hata kodu {process.ExitCode}" : error.Trim();
    }
}
