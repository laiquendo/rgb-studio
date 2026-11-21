namespace RgbStudio.Core;

public static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RgbStudio");
    public static readonly string Scenes = Path.Combine(Root, "Scenes");
    public static readonly string Settings = Path.Combine(Root, "settings.json");
    public static readonly string CurrentScene = Path.Combine(Root, "current.json");
    public static readonly string LogFile = Path.Combine(Root, "log.txt");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Scenes);
    }
}

/// <summary>Basit, is parcacigi guvenli dosya gunlugu (%APPDATA%\RgbStudio\log.txt, ~1 MB'de doner).</summary>
public static class Log
{
    static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.Ensure();
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* gunluk yazilamazsa uygulama durmamali */ }
    }
}
