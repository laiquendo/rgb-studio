using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Win32;
using OpenRGB.NET;
using RgbStudio.Core;
using ORColor = OpenRGB.NET.Color;

namespace RgbStudio.Providers;

/// <summary>
/// OpenRGB SDK sunucusu uzerinden donanim erisimi. OpenRGB, RAM ve anakart SMBus'ina
/// PawnIO imzali surucusuyle erisir; bu yuzden RAM'e dogrudan yazmiyoruz (SPD'ye zarar verme riski yok).
/// </summary>
public sealed class OpenRgbProvider : ILightingProvider
{
    const int Port = 6742;
    readonly object _io = new();
    OpenRgbClient? _client;
    readonly Dictionary<int, int> _modeForDevice = new();

    /// <summary>
    /// Sunucu kapaliysa OpenRGB'yi biz baslatalim mi? Yonetici olmadan baslatilan OpenRGB RAM'i goremez
    /// ve portu tuttugu icin sonraki yonetici oturumunu da korler; bu yuzden yalnizca yonetici iken baslatiriz.
    /// </summary>
    public bool AllowStartServer { get; init; } = IsAdmin();

    static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public string Name => "OpenRGB";
    public string Status { get; private set; } = "Bağlanmadı";

    /// <summary>Soguk acilista OpenRGB sunucusunun kalkmasi + cihaz algilamasi bir dakikayi asabilir.</summary>
    public TimeSpan DetectTimeout => TimeSpan.FromSeconds(150);

    public event Action? Invalidated;

    /// <summary>
    /// true iken yazma yapilmaz: cihazlar liste sirasiyla adreslendigi icin OpenRGB listesi degistiyse
    /// (yeniden tarama, tak-cikar) eski indeksler yanlis cihaza gidebilir. Yeni tarama bitince temizlenir.
    /// </summary>
    volatile bool _stale = true;

    sealed record Handle(int Index, string Key);

    public static string? FindExe()
    {
        var candidates = new List<string>
        {
            @"C:\Program Files\OpenRGB\OpenRGB.exe",
            @"C:\Program Files (x86)\OpenRGB\OpenRGB.exe",
        };
        try
        {
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var k = Registry.LocalMachine.OpenSubKey(root);
                if (k == null) continue;
                foreach (var sub in k.GetSubKeyNames())
                {
                    using var s = k.OpenSubKey(sub);
                    if (s == null || (s.GetValue("DisplayName") as string)?.Contains("OpenRGB", StringComparison.OrdinalIgnoreCase) != true) continue;
                    if (s.GetValue("InstallLocation") is string loc && loc.Length > 0)
                        candidates.Insert(0, Path.Combine(loc, "OpenRGB.exe"));
                }
            }
        }
        catch { }
        return candidates.FirstOrDefault(File.Exists);
    }

    static bool PortOpen()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", Port).Wait(400) && c.Connected;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<LightDevice>> DetectAsync(CancellationToken ct)
    {
        if (!PortOpen())
        {
            var exe = FindExe();
            if (exe == null) { Status = "OpenRGB kurulu değil"; return Array.Empty<LightDevice>(); }
            if (!AllowStartServer) { Status = "OpenRGB çalışmıyor (yönetici izni olmadan başlatılmadı)"; return Array.Empty<LightDevice>(); }
            Log.Info($"OpenRGB sunucusu başlatılıyor: {exe}");
            Process.Start(new ProcessStartInfo(exe, $"--server --server-port {Port} --startminimized")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            for (int i = 0; i < 60 && !PortOpen(); i++) await Task.Delay(500, ct);
            if (!PortOpen()) { Status = "OpenRGB sunucusu açılmadı"; return Array.Empty<LightDevice>(); }
        }

        Device[] data;
        _stale = true;
        lock (_io)
        {
            try { _client?.Dispose(); } catch { }
            // 3 sn zaman asimi: sunucu takilirsa render is parcacigi suresiz beklemesin.
            var client = new OpenRgbClient("127.0.0.1", Port, "RGB Studio", true, 3000);
            client.DeviceListUpdated += (_, _) =>
            {
                Log.Info("OpenRGB cihaz listesi değişti; yeniden taranacak.");
                _stale = true;
                Invalidated?.Invoke();
            };
            _client = client;
            _modeForDevice.Clear();
        }

        // OpenRGB acilista cihazlari birkac saniyede bulur; sayi sabitlenene kadar bekle.
        int last = -1, stable = 0;
        for (int i = 0; i < 30 && stable < 3; i++)
        {
            int n;
            lock (_io) n = _client!.GetControllerCount();
            stable = n == last ? stable + 1 : 0;
            last = n;
            await Task.Delay(800, ct);
        }
        lock (_io) data = _client!.GetAllControllerData();

        var devices = new List<LightDevice>();
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < data.Length; i++)
        {
            var d = data[i];
            // Ayni adli iki RAM gibi cihazlari konumla ayir; kimlik yeniden taramalarda sabit kalir.
            string key = $"{d.Name}|{d.Location}";
            seen[key] = seen.GetValueOrDefault(key) + 1;
            if (seen[key] > 1) key += $"#{seen[key]}";

            var leds = d.Leds.Select((l, idx) => new LedInfo(idx, string.IsNullOrWhiteSpace(l.Name) ? $"LED {idx + 1}" : l.Name)).ToList();
            if (leds.Count == 0) continue;

            var kind = d.Type switch
            {
                DeviceType.Motherboard => DeviceKind.Motherboard,
                DeviceType.Dram => DeviceKind.Ram,
                DeviceType.Gpu => DeviceKind.Gpu,
                DeviceType.Cooler => DeviceKind.Cooler,
                DeviceType.Ledstrip => DeviceKind.LedStrip,
                DeviceType.Keyboard => DeviceKind.Keyboard,
                DeviceType.Mouse => DeviceKind.Mouse,
                DeviceType.Headset => DeviceKind.Headset,
                _ => DeviceKind.Other,
            };
            string name = d.Name;
            if (kind == DeviceKind.Ram)
            {
                int ramNo = devices.Count(x => x.Kind == DeviceKind.Ram) + 1;
                name = $"RAM {ramNo} ({d.Name})";
            }

            devices.Add(new LightDevice(this, "openrgb:" + key, name, kind, leds)
            {
                Handle = new Handle(i, key),
                PerLed = true,
                // SMBus (RAM) ve GPU I2C yavas; animasyonlarda veriyolunu bogmamak icin sinirla.
                MaxFps = kind is DeviceKind.Ram or DeviceKind.Gpu or DeviceKind.Motherboard ? 15 : 30,
            });

            // "Static" bu RAM ve ekran kartinda guvenilir calisiyor; "Direct" RAM'de siyahi uygulamiyordu.
            var mode = d.Modes.FirstOrDefault(m => m.Name.Equals("Static", StringComparison.OrdinalIgnoreCase))
                    ?? d.Modes.FirstOrDefault(m => m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase));
            if (mode != null) lock (_io) _modeForDevice[i] = mode.Index;
        }

        Status = devices.Count == 0 ? "Cihaz bulunamadı" : $"{devices.Count} cihaz";
        _stale = false;
        return devices;
    }

    public void Write(LightDevice device, ReadOnlySpan<Rgb> colors, bool full)
    {
        if (device.Handle is not Handle h || _stale) return;
        var arr = new ORColor[colors.Length];
        for (int i = 0; i < colors.Length; i++) arr[i] = new ORColor(colors[i].R, colors[i].G, colors[i].B);

        lock (_io)
        {
            if (_client == null) return;
            if (full)
            {
                if (_modeForDevice.TryGetValue(h.Index, out int mode)) _client.UpdateMode(h.Index, mode);
                else _client.SetCustomMode(h.Index);
            }
            _client.UpdateLeds(h.Index, arr);
        }
    }

    public void Dispose()
    {
        lock (_io)
        {
            try { _client?.Dispose(); } catch { }
            _client = null;
        }
    }
}
