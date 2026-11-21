using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace RgbStudio.Core;

/// <summary>
/// Windows acilisinda (oturum acilinca) yonetici olarak, UAC sormadan baslatma.
/// Gorev Zamanlayicisi'nda "en yuksek ayricaliklarla" calisan bir gorev olusturur.
/// </summary>
public static class Startup
{
    const string TaskName = "RGB Studio";
    const string LegacyTaskName = "RGB Ac-Kapat"; // onceki uygulamanin gorevi

    static (int code, string output) Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, o.Trim());
    }

    public static bool IsEnabled() => Schtasks($"/Query /TN \"{TaskName}\"").code == 0;

    public static bool LegacyTaskExists() => Schtasks($"/Query /TN \"{LegacyTaskName}\"").code == 0;

    /// <returns>Hata metni; basariliysa null.</returns>
    public static string? Enable()
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RgbStudio.exe");
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>RGB Studio: isiklari son senaryoya getirir.</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <Delay>PT15S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{SecurityElement.Escape(exe)}</Command>
      <Arguments>--minimized</Arguments>
      <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe)!)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
        string tmp = Path.Combine(Path.GetTempPath(), "rgbstudio_task.xml");
        File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
        try
        {
            var (code, output) = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            if (code != 0) return output;
            // Eski uygulama da acilista baslarsa ayni isiklar icin iki program yarisir.
            if (LegacyTaskExists()) Schtasks($"/Delete /TN \"{LegacyTaskName}\" /F");
            return null;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>
    /// Onceki "RGB Ac/Kapat" acilista baslatiliyorsa, ayni ayari RGB Studio'ya tasir
    /// (iki program ayni isiklari yarisarak surmesin). Yonetici olarak cagrilmali.
    /// </summary>
    public static void MigrateLegacyTask()
    {
        try
        {
            if (!LegacyTaskExists()) return;
            var err = Enable(); // basariliysa eski gorevi de siler
            Log.Info(err == null ? "Eski 'RGB Ac-Kapat' açılış görevi RGB Studio'ya taşındı." : "Açılış görevi taşınamadı: " + err);
        }
        catch (Exception ex) { Log.Warn("Açılış görevi taşınamadı: " + ex.Message); }
    }

    public static string? Disable()
    {
        var (code, output) = Schtasks($"/Delete /TN \"{TaskName}\" /F");
        return code == 0 ? null : output;
    }
}
