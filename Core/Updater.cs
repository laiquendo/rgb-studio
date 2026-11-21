using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace RgbStudio.Core;

/// <summary>GitHub'daki son (on surum olmayan) yayin.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string AssetUrl, string AssetName, long Size, string? Sha256);

/// <summary>
/// GitHub Releases uzerinden guncelleme: son yayini denetler, zip'i indirir, SHA-256'sini GitHub'in
/// bildirdigi ozetle karsilastirir ve dosyalari yerinde degistirir. Calisan exe/dll'ler silinemez ama
/// yeniden adlandirilabilir: eskiler "*.old-update" olur, yeni surum acilinca temizlenir.
/// </summary>
public static class Updater
{
    public const string Repo = "laiquendo/rgb-studio";
    const string OldSuffix = ".old-update";

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RGBStudio/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>"v2.1.0" / "2.1.0-beta.1" gibi etiketlerden surum; anlasilmazsa null.</summary>
    public static Version? ParseTag(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>Daha yeni bir yayin varsa bilgisini, yoksa null dondurur.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version? current = null, CancellationToken ct = default)
    {
        current ??= Current;
        using var http = NewClient();
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // henuz yayin yok
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag);
        if (version == null || version <= current) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = null;
            if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
            {
                var s = dg.GetString() ?? "";
                if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = s[7..];
            }
            return new UpdateInfo(version, tag,
                root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases",
                a.GetProperty("browser_download_url").GetString()!, name, a.GetProperty("size").GetInt64(), sha);
        }
        Log.Warn($"Güncelleme {tag} bulundu ama içinde win-x64 zip yok.");
        return null;
    }

    /// <summary>Zip'i indirir ve dogrular; gecici bir klasore acip o klasoru dondurur.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!info.AssetUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Beklenmeyen indirme adresi: " + info.AssetUrl);
        if (info.Sha256 == null)
            throw new InvalidDataException("GitHub bu dosya için SHA-256 özeti vermedi; güvenlik için indirilmedi.");

        var work = Path.Combine(Path.GetTempPath(), "RgbStudio-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "update.zip");

        using (var http = NewClient())
        using (var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
            }
        }

        string actual;
        using (var fs = File.OpenRead(zipPath)) actual = Convert.ToHexString(SHA256.HashData(fs));
        if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("İndirilen dosyanın SHA-256 özeti GitHub'dakiyle eşleşmiyor.");

        var staging = Path.Combine(work, "files");
        ExtractSafely(zipPath, staging);
        if (!File.Exists(Path.Combine(staging, "RgbStudio.exe")))
            throw new InvalidDataException("Güncelleme paketinde RgbStudio.exe yok.");
        return staging;
    }

    /// <summary>Zip icindeki yollarin hedef klasorun disina cikmasina izin verme.</summary>
    static void ExtractSafely(string zipPath, string dest)
    {
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, e.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Güncelleme paketinde geçersiz yol: " + e.FullName);
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Gecici klasordeki dosyalari uygulama klasorune koyar. Bir adim basarisiz olursa
    /// degistirilen her seyi geri alir; uygulama eski haliyle calismaya devam eder.
    /// </summary>
    public static void Apply(string staging, string? appDir = null)
    {
        appDir ??= AppContext.BaseDirectory;
        var moved = new List<(string Target, string Old)>();
        var added = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staging, file);
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var old = target + OldSuffix;
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                    moved.Add((target, old));
                }
                else added.Add(target);
                File.Copy(file, target);
            }
        }
        catch
        {
            foreach (var t in added) try { File.Delete(t); } catch { }
            foreach (var (t, old) in moved)
                try { if (File.Exists(t)) File.Delete(t); File.Move(old, t); } catch { }
            throw;
        }
        try { Directory.Delete(Path.GetDirectoryName(staging)!, true); } catch { }
    }

    /// <summary>Onceki guncellemeden kalan "*.old-update" dosyalarini sil (yeni surum acilinca).</summary>
    public static void CleanupOldFiles(string? appDir = null)
    {
        appDir ??= AppContext.BaseDirectory;
        try
        {
            foreach (var f in Directory.EnumerateFiles(appDir, "*" + OldSuffix, SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* hala kilitliyse sonraki acilista */ }
        }
        catch { }
    }

    /// <summary>Yeni exe'yi baslat; yeni kopya bu surecin bitmesini bekleyip oyle acilir.</summary>
    public static void Restart(bool minimized)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RgbStudio.exe");
        var args = $"--after-update {Environment.ProcessId}" + (minimized ? " --minimized" : "");
        // ShellExecute: yonetici surecten yonetici exe'yi sormadan baslatir.
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
    }
}
