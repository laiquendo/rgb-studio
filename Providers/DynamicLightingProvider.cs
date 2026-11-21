using RgbStudio.Core;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using WinColor = Windows.UI.Color;

namespace RgbStudio.Providers;

/// <summary>
/// Windows Dinamik Aydinlatma (LampArray / HID LampArray): orn. ROG Falchion klavye, tus tus kontrol.
/// Windows isik kontrolunu yalnizca on plandaki uygulamaya verir.
/// </summary>
public sealed class DynamicLightingProvider : ILightingProvider
{
    readonly object _io = new();
    readonly List<LampArray> _arrays = new();

    public event Action? Invalidated;
    DeviceWatcher? _watcher;

    public string Name => "Windows Dinamik Aydınlatma";

    /// <summary>Klavye takilip cikarilinca yeniden tarama iste.</summary>
    void EnsureWatcher()
    {
        if (_watcher != null) return;
        _watcher = DeviceInformation.CreateWatcher(LampArray.GetDeviceSelector());
        bool initial = true;
        _watcher.EnumerationCompleted += (_, _) => initial = false;
        _watcher.Added += (_, _) => { if (!initial) Invalidated?.Invoke(); };
        _watcher.Removed += (_, _) => Invalidated?.Invoke();
        _watcher.Start();
    }

    void DisposeArrays()
    {
        // LampArray projeksiyonda IDisposable degil; referanslari birakmak yeterli (CsWinRT sonlandiricisi kapatir).
        _arrays.Clear();
    }
    public string Status { get; private set; } = "Taranmadı";

    sealed record Handle(LampArray Array, int[] Indices);

    public async Task<IReadOnlyList<LightDevice>> DetectAsync(CancellationToken ct)
    {
        lock (_io) DisposeArrays();
        try { EnsureWatcher(); } catch (Exception ex) { Log.Warn("LampArray izleyici başlatılamadı: " + ex.Message); }
        var infos = await DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector());
        var result = new List<LightDevice>();
        foreach (var info in infos)
        {
            ct.ThrowIfCancellationRequested();
            LampArray? la;
            try { la = await LampArray.FromIdAsync(info.Id); }
            catch (Exception ex) { Log.Error($"LampArray açılamadı: {info.Name}", ex); continue; }
            if (la == null || la.LampCount == 0) continue;

            var leds = new List<LedInfo>(la.LampCount);
            for (int i = 0; i < la.LampCount; i++) leds.Add(new LedInfo(i, $"Işık {i + 1}"));

            var kind = la.LampArrayKind switch
            {
                LampArrayKind.Keyboard => DeviceKind.Keyboard,
                LampArrayKind.Mouse => DeviceKind.Mouse,
                LampArrayKind.Chassis => DeviceKind.LedStrip,
                _ => DeviceKind.Other,
            };

            var device = new LightDevice(this, "lamparray:" + info.Id, info.Name, kind, leds)
            {
                Handle = new Handle(la, Enumerable.Range(0, la.LampCount).ToArray()),
                PerLed = true,
                MaxFps = 30,
                Note = "Windows bu cihazın ışığını yalnızca RGB Studio penceresi öndeyken uygulamaya bırakır; arka planda üreticinin programı (örn. Armoury Crate) devralabilir.",
            };
            // Kontrol bize gecince (pencere one gelince) son kareyi hemen yeniden yaz.
            try { la.AvailabilityChanged += (s, _) => { if (s.IsAvailable) device.NeedsFullWrite = true; }; }
            catch (Exception ex) { Log.Warn("LampArray.AvailabilityChanged desteklenmiyor: " + ex.Message); }
            lock (_io) _arrays.Add(la);
            result.Add(device);
        }
        Status = result.Count == 0 ? "Cihaz yok" : $"{result.Count} cihaz";
        return result;
    }

    public void Write(LightDevice device, ReadOnlySpan<Rgb> colors, bool full)
    {
        if (device.Handle is not Handle h) return;
        int n = Math.Min(colors.Length, h.Indices.Length);
        var arr = new WinColor[n];
        for (int i = 0; i < n; i++) arr[i] = WinColor.FromArgb(255, colors[i].R, colors[i].G, colors[i].B);
        lock (_io)
        {
            if (full) h.Array.BrightnessLevel = 1.0;
            h.Array.SetColorsForIndices(arr, h.Indices.AsSpan(0, n).ToArray());
        }
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch { }
        lock (_io) DisposeArrays();
    }
}
