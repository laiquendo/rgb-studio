using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using RgbStudio.Core;

namespace RgbStudio.Providers;

/// <summary>
/// MSI Mystic Light SDK DLL'i MSI'a aittir ve RGB Studio ile dagitilmaz. Kullanici onaylarsa
/// MSI'in kendi sunucusundan indirilir; yuklenmeden once her seferinde MSI imzasi dogrulanir
/// (uygulama yonetici olarak calistigi icin baska bir DLL'in araya sokulmasini engeller).
/// </summary>
public static class MsiSdk
{
    public const string DllName = "MysticLight_SDK_x64.dll";
    public const string DownloadUrl = "https://download.msi.com/uti_exe/Mystic_light_SDK.zip";
    const string ExpectedSigner = "MICRO-STAR INTERNATIONAL CO., LTD.";

    /// <summary>Indirilen kopya: %ProgramData%\RgbStudio\native (yonetici yazar).</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RgbStudio", "native");

    static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, DllName);
        yield return Path.Combine(InstallDir, DllName);
    }

    public static string? FindDll() => Candidates().FirstOrDefault(File.Exists);

    public static bool IsAvailable => FindDll() != null;

    /// <summary>Anakart MSI mi? (Indirme onerisini yalnizca MSI kartlarda gostermek icin.)</summary>
    public static bool IsMsiBoard()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var m = k?.GetValue("BaseBoardManufacturer") as string ?? "";
            return m.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || m.Contains("MSI", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    static int _registered;

    /// <summary>DllImport'larin DLL'i yalnizca dogrulanmis tam yoldan yuklemesini sagla.</summary>
    public static void RegisterResolver()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(MsiSdk).Assembly, Resolve);
    }

    static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (!name.Equals(DllName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero; // digerleri varsayilan arama
        foreach (var file in Candidates().Where(File.Exists))
        {
            try
            {
                // Dosyayi dogrulama ile yukleme arasinda degistirilemesin diye acik tut.
                using var guard = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!IsSignedByMsi(file, out var why))
                {
                    Log.Warn($"Mystic Light SDK reddedildi ({file}): {why}");
                    continue;
                }
                return NativeLibrary.Load(file);
            }
            catch (Exception ex) { Log.Warn($"Mystic Light SDK yüklenemedi ({file}): {ex.Message}"); }
        }
        throw new DllNotFoundException(DllName);
    }

    /// <summary>MSI SDK zip'ini indirir, DLL'i cikarir, imzasini dogrular ve yerine koyar.</summary>
    public static async Task DownloadAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RGBStudio/2.0");
        var bytes = await http.GetByteArrayAsync(DownloadUrl, ct);

        Directory.CreateDirectory(InstallDir);
        var tmp = Path.Combine(InstallDir, DllName + ".download");
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(DllName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("İndirilen pakette " + DllName + " bulunamadı.");
            entry.ExtractToFile(tmp, overwrite: true);
        }
        if (!IsSignedByMsi(tmp, out var why))
        {
            File.Delete(tmp);
            throw new InvalidDataException("İndirilen dosyanın MSI imzası doğrulanamadı: " + why);
        }
        File.Move(tmp, Path.Combine(InstallDir, DllName), overwrite: true);
        Log.Info("Mystic Light SDK MSI'dan indirildi: " + InstallDir);
    }

    // ---- Authenticode dogrulamasi ----

    public static bool IsSignedByMsi(string file, out string reason)
    {
        int r = VerifyTrust(file);
        if (r != 0) { reason = $"imza geçersiz (0x{r:X8})"; return false; }
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            var org = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.Equals(org, ExpectedSigner, StringComparison.OrdinalIgnoreCase))
            {
                reason = "imzalayan MSI değil: " + org;
                return false;
            }
        }
        catch (Exception ex) { reason = ex.Message; return false; }
        reason = "";
        return true;
    }

    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;          // 2 = WTD_UI_NONE
        public uint fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
        public uint dwUnionChoice;       // 1 = WTD_CHOICE_FILE
        public IntPtr pFile;
        public uint dwStateAction;       // 0 = ignore
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    static int VerifyTrust(string file)
    {
        var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fi, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = pFile,
                dwProvFlags = 0x00000040, // WTD_CACHE_ONLY_URL_RETRIEVAL: imza dogrulamasi ag beklemesin
            };
            return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
