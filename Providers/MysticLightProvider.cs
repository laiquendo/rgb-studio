using System.Runtime.InteropServices;
using RgbStudio.Core;

namespace RgbStudio.Providers;

/// <summary>
/// MSI Mystic Light SDK (resmi, MSI imzali DLL). Anakart ve ARGB girislerine bagli
/// fanlar, LED seritler, sivi sogutucu (orn. EK-Nucleus). MSI Center + Mystic Light gerekir.
/// </summary>
public sealed class MysticLightProvider : ILightingProvider
{
    const string Dll = MsiSdk.DllName;

    public MysticLightProvider() => MsiSdk.RegisterResolver();

    /// <summary>SDK DLL'i henuz MSI'dan indirilmedi (ya da imzasi dogrulanamadi).</summary>
    public bool SdkMissing { get; private set; }
    const int OK = 0;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int MLAPI_Initialize();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int MLAPI_Release();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int MLAPI_GetErrorMessage(int code, [MarshalAs(UnmanagedType.BStr)] out string desc);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int MLAPI_GetDeviceInfo(
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] types,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] counts);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int MLAPI_GetDeviceNameEx([MarshalAs(UnmanagedType.BStr)] string type, int index,
        [MarshalAs(UnmanagedType.BStr)] out string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int MLAPI_GetLedInfo([MarshalAs(UnmanagedType.BStr)] string type, int index,
        [MarshalAs(UnmanagedType.BStr)] out string name,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] styles);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int MLAPI_SetLedStyle([MarshalAs(UnmanagedType.BStr)] string type, int index,
        [MarshalAs(UnmanagedType.BStr)] string style);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int MLAPI_SetLedColor([MarshalAs(UnmanagedType.BStr)] string type, int index, int r, int g, int b);

    readonly object _io = new();
    bool _ready;

    sealed class Handle
    {
        public required string Type;
        public required int Index;
        public required string[] Styles;
        public string? CurrentStyle;
        public Rgb? LastColor;
    }

    public string Name => "MSI Mystic Light";
    public string Status { get; private set; } = "Bağlanmadı";

    static string Err(int code)
    {
        try { return MLAPI_GetErrorMessage(code, out var d) == OK ? $"{code} ({d})" : code.ToString(); }
        catch { return code.ToString(); }
    }

    /// <summary>MSI SDK'nin Initialize cagrisi bir kez geri donmediyse true; o surecte bir daha denenmez.</summary>
    volatile bool _initHung;

    /// <summary>MSI sureleri: tek Initialize denemesi icin ust sinir.</summary>
    static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(15);

    public TimeSpan DetectTimeout => TimeSpan.FromSeconds(90);

#pragma warning disable CS0067 // MSI SDK'sinda liste degisikligi bildirimi yok
    public event Action? Invalidated;
#pragma warning restore CS0067

    public async Task<IReadOnlyList<LightDevice>> DetectAsync(CancellationToken ct)
    {
        SdkMissing = !MsiSdk.IsAvailable;
        if (SdkMissing)
        {
            Status = MsiSdk.IsMsiBoard() ? "MSI SDK indirilmedi (Ayarlar'dan indirin)" : "MSI anakart değil";
            return Array.Empty<LightDevice>();
        }
        if (_initHung)
        {
            // Takili kalan cagri hala SDK'nin icinde; ustune yeni cagri eklemek onu da kilitler.
            Status = "MSI servisi yanıt vermiyor — RGB Studio'yu yeniden başlatın";
            return Array.Empty<LightDevice>();
        }

        // _io'yu yalnizca kisa sureli tut: takilan bir SDK cagrisi kapanisi ve yeniden taramayi kilitlemesin.
        if (!Monitor.TryEnter(_io, TimeSpan.FromSeconds(5)))
        {
            Status = "MSI meşgul, daha sonra tekrar taranacak";
            return Array.Empty<LightDevice>();
        }
        try { if (_ready) { try { MLAPI_Release(); } catch { } _ready = false; } }
        finally { Monitor.Exit(_io); }

        int r = -999;
        try
        {
            // MSI servisi (ozellikle uyanistan sonra) gec acilabiliyor.
            for (int i = 0; i < 4; i++)
            {
                // Kilit tutmadan, kendi is parcaciginda; takilirsa terk edilir.
                var call = Task.Factory.StartNew(MLAPI_Initialize, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                if (await Task.WhenAny(call, Task.Delay(InitTimeout, ct)) != call)
                {
                    _initHung = true;
                    Status = "MSI servisi yanıt vermiyor — RGB Studio'yu yeniden başlatın";
                    Log.Warn("Mystic Light: Initialize 15 sn içinde dönmedi; bu oturumda tekrar denenmeyecek.");
                    return Array.Empty<LightDevice>();
                }
                r = await call;
                if (r == OK) break;
                await Task.Delay(2000, ct);
            }
        }
        catch (DllNotFoundException) { SdkMissing = true; Status = "MSI SDK bulunamadı ya da imzası doğrulanamadı"; return Array.Empty<LightDevice>(); }
        catch (BadImageFormatException) { Status = "SDK dosyası uyumsuz"; return Array.Empty<LightDevice>(); }

        if (r != OK)
        {
            Status = r == -3 ? "MSI Center / Mystic Light kurulu değil" : $"Bağlanamadı: {Err(r)}";
            Log.Warn($"Mystic Light: {Status}");
            return Array.Empty<LightDevice>();
        }

        var result = new List<LightDevice>();
        lock (_io)
        {
            _ready = true;
            if (MLAPI_GetDeviceInfo(out var types, out var counts) != OK || types == null)
            {
                Status = "Cihaz bulunamadı";
                return result;
            }
            for (int d = 0; d < types.Length; d++)
            {
                int n = int.TryParse(counts[d], out var x) ? x : 0;
                string devName = MLAPI_GetDeviceNameEx(types[d], 0, out var dn) == OK && !string.IsNullOrWhiteSpace(dn) ? dn : types[d];
                for (int i = 0; i < n; i++)
                {
                    MLAPI_GetLedInfo(types[d], i, out var ledName, out var styles);
                    styles ??= Array.Empty<string>();
                    Log.Info($"Mystic Light: {types[d]}[{i}] {ledName}: {string.Join(", ", styles)}");

                    // "SELECT ALL" = anakart + tum JRGB/JARGB girisleri birlikte (fanlar, serit, sogutucu).
                    bool all = string.Equals(ledName, "SELECT ALL", StringComparison.OrdinalIgnoreCase);
                    string title = all ? "Anakart + fanlar + şeritler + soğutucu" : (ledName ?? $"Bölge {i + 1}");
                    var kind = types[d].StartsWith("MSI_MB", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Motherboard : DeviceKind.Other;

                    result.Add(new LightDevice(this, $"msi:{types[d]}:{i}", $"{title} ({devName})", kind,
                        new[] { new LedInfo(0, ledName ?? "Tümü") })
                    {
                        Handle = new Handle { Type = types[d], Index = i, Styles = styles },
                        PerLed = false,
                        // MSI SDK her cagrida servise gidip geliyor; hizli animasyon kaldirmiyor.
                        // MSI SDK her cagrida servise gidip geliyor. Kendi is parcaciginda yazildigi icin
                        // yavaslarsa yalnizca kendisi kare atlar; gercek hiz gunlukteki "Yazma süresi" satirinda.
                        MaxFps = 12,
                        Note = all
                            ? "MSI bu anakartta ARGB girişlerini tek bölge olarak veriyor: fanlar, şeritler ve soğutucu birlikte değişir."
                            : null,
                    });
                }
            }
        }
        Status = $"{result.Count} bölge";
        return result;
    }

    static string? Pick(string[] styles, params string[] wanted) =>
        wanted.Select(w => styles.FirstOrDefault(s => s.Equals(w, StringComparison.OrdinalIgnoreCase)))
              .FirstOrDefault(s => s != null);

    public void Write(LightDevice device, ReadOnlySpan<Rgb> colors, bool full)
    {
        if (device.Handle is not Handle h || colors.Length == 0) return;
        var c = colors[0];
        // Render is parcacigi burada asla suresiz beklememeli (digger cihazlar da donar).
        if (!Monitor.TryEnter(_io, 200)) throw new TimeoutException("Mystic Light meşgul");
        try
        {
            if (!_ready) return;
            if (c.IsBlack)
            {
                var off = Pick(h.Styles, "Off");
                if (off != null)
                {
                    if (full || h.CurrentStyle != off)
                    {
                        Check(MLAPI_SetLedStyle(h.Type, h.Index, off), "SetLedStyle Off");
                        h.CurrentStyle = off;
                    }
                    h.LastColor = c;
                    return;
                }
            }

            var steady = Pick(h.Styles, "Steady", "Static", "Always On");
            if (steady != null && (full || h.CurrentStyle != steady))
            {
                Check(MLAPI_SetLedStyle(h.Type, h.Index, steady), "SetLedStyle Steady");
                h.CurrentStyle = steady;
                h.LastColor = null;
            }
            if (full || h.LastColor != c)
            {
                Check(MLAPI_SetLedColor(h.Type, h.Index, c.R, c.G, c.B), "SetLedColor");
                h.LastColor = c;
            }
        }
        finally { Monitor.Exit(_io); }
    }

    static void Check(int code, string what)
    {
        if (code != OK) throw new InvalidOperationException($"Mystic Light {what}: {Err(code)}");
    }

    public void Dispose()
    {
        // Takili bir SDK cagrisi kilidi tutuyorsa kapanisi bekletme; surec cikisinda zaten birakilir.
        if (!Monitor.TryEnter(_io, TimeSpan.FromSeconds(2))) return;
        try
        {
            if (_ready && !_initHung) try { MLAPI_Release(); } catch { }
            _ready = false;
        }
        finally { Monitor.Exit(_io); }
    }
}
