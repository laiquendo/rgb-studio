using System.Text.Json;

namespace RgbStudio.Core;

public enum ThemeChoice { System, Dark, Light }

public sealed class AppSettings
{
    /// <summary>Ana dugme: tum isiklar kapali.</summary>
    public bool MasterOff { get; set; }
    public bool ReapplyOnResume { get; set; } = true;
    public bool ApplyOnStartup { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public string? LastSceneName { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), SceneStore.Json) ?? new();
            return MigrateFromRgbToggle();
        }
        catch (Exception ex) { Log.Warn("Ayarlar okunamadı: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            // Once gecici dosyaya yaz, sonra degistir: yazim yarida kesilirse ayarlar sifirlanmasin.
            var tmp = AppPaths.Settings + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, SceneStore.Json));
            File.Move(tmp, AppPaths.Settings, overwrite: true);
        }
        catch (Exception ex) { Log.Warn("Ayarlar yazılamadı: " + ex.Message); }
    }

    /// <summary>Onceki "RGB Ac/Kapat" uygulamasinin acik/kapali durumunu ve rengini devral.</summary>
    static AppSettings MigrateFromRgbToggle()
    {
        var s = new AppSettings();
        try
        {
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RgbToggle", "settings.json");
            if (!File.Exists(old)) return s;
            using var doc = JsonDocument.Parse(File.ReadAllText(old));
            var root = doc.RootElement;
            if (root.TryGetProperty("IsOn", out var on)) s.MasterOff = !on.GetBoolean();
            if (root.TryGetProperty("ReapplyOnResume", out var rr)) s.ReapplyOnResume = rr.GetBoolean();
            if (root.TryGetProperty("ColorArgb", out var argb) && SceneStore.LoadCurrent() == null)
            {
                int v = argb.GetInt32();
                var c = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
                if (c.IsBlack)
                {
                    // Eski uygulamada siyah renk = isiklar fiilen kapali. Bunu ana dugmeye cevir,
                    // rengi beyaz birak ki "ac" denince isiklar gercekten yansin.
                    s.MasterOff = true;
                    c = Rgb.White;
                }
                var scene = new Scene { Name = "Son durum", Default = new DeviceScene { Colors = new() { c } } };
                SceneStore.SaveCurrent(scene);
            }
            Log.Info("RGB Aç/Kapat ayarları devralındı.");
        }
        catch (Exception ex) { Log.Warn("Eski ayarlar devralınamadı: " + ex.Message); }
        s.Save();
        return s;
    }
}
