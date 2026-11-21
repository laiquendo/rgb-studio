using System.Text.Json;
using System.Text.Json.Serialization;

namespace RgbStudio.Core;

/// <summary>Senaryolari %APPDATA%\RgbStudio\Scenes\*.json olarak saklar.</summary>
public static class SceneStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Turkce karakterler okunur kalsin
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string FileNameFor(string sceneName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(sceneName.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (clean.Length == 0) clean = "senaryo";
        return Path.Combine(AppPaths.Scenes, clean + ".json");
    }

    /// <summary>Diskteki bir senaryo ve dosyasi (silme/disa aktarma dosya uzerinden yapilir, ad uzerinden degil).</summary>
    public sealed record SceneFile(Scene Scene, string Path);

    public static IReadOnlyList<SceneFile> LoadAll()
    {
        AppPaths.Ensure();
        var list = new List<SceneFile>();
        foreach (var f in Directory.EnumerateFiles(AppPaths.Scenes, "*.json"))
        {
            try { list.Add(new SceneFile(Load(f), f)); }
            catch (Exception ex) { Log.Warn($"Senaryo okunamadı {f}: {ex.Message}"); }
        }
        return list.OrderBy(s => s.Scene.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static Scene Load(string path)
    {
        var scene = JsonSerializer.Deserialize<Scene>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Boş senaryo dosyası");
        Validate(scene);
        return scene;
    }

    /// <summary>Elle duzenlenmis/disaridan gelmis dosyalarda degerleri guvenli araliga ceker.</summary>
    public static void Validate(Scene s)
    {
        s.Name = string.IsNullOrWhiteSpace(s.Name) ? "Adsız senaryo" : s.Name.Trim();
        s.Default ??= new DeviceScene();
        s.Devices ??= new();
        foreach (var ds in s.Devices.Values.Append(s.Default))
        {
            ds.Colors ??= new();
            if (ds.Colors.Count == 0) ds.Colors.Add(Rgb.White);
            if (ds.Colors.Count > 4096) ds.Colors.RemoveRange(4096, ds.Colors.Count - 4096);
            ds.Speed = Math.Clamp(double.IsFinite(ds.Speed) ? ds.Speed : 1, 0.1, 5);
            ds.Brightness = Math.Clamp(ds.Brightness, 0, 100);
            if (!Enum.IsDefined(ds.Effect)) ds.Effect = EffectType.Static;
        }
    }

    public static string Save(Scene scene, string? path = null)
    {
        AppPaths.Ensure();
        path ??= FileNameFor(scene.Name);
        scene.Modified = DateTime.Now;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(scene, Json));
        File.Move(tmp, path, overwrite: true); // yarim yazilmis dosya kalmasin
        return path;
    }

    /// <summary>Senaryo dosyasini Geri Donusum Kutusu'na tasir (kalici silmez).</summary>
    public static void Delete(SceneFile file)
    {
        if (!File.Exists(file.Path)) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.Path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    /// <summary>Disaridan bir JSON okur ve dogrular; kaydetmez (ayni adda dosya varsa once kullaniciya sorulur).</summary>
    public static (Scene Scene, string Target) PrepareImport(string path)
    {
        var scene = Load(path);
        return (scene, FileNameFor(scene.Name));
    }

    /// <summary>
    /// Mevcut bir dosyanin uzerine yazarken, su an taranamayan (gecici olarak eksik) cihazlarin
    /// ayarlarini eski dosyadan koru; yoksa MSI gibi bir katman o an yanit vermediyse ayarlari kaybolurdu.
    /// </summary>
    public static void MergeMissingDevices(Scene scene, string existingPath)
    {
        try
        {
            if (!File.Exists(existingPath)) return;
            var old = Load(existingPath);
            foreach (var (id, ds) in old.Devices)
                scene.Devices.TryAdd(id, ds);
        }
        catch (Exception ex) { Log.Warn("Eski senaryo birleştirilemedi: " + ex.Message); }
    }

    public static Scene? LoadCurrent()
    {
        try { return File.Exists(AppPaths.CurrentScene) ? Load(AppPaths.CurrentScene) : null; }
        catch (Exception ex) { Log.Warn("Son durum okunamadı: " + ex.Message); return null; }
    }

    public static void SaveCurrent(Scene scene)
    {
        try { Save(scene, AppPaths.CurrentScene); }
        catch (Exception ex) { Log.Warn("Son durum yazılamadı: " + ex.Message); }
    }
}
