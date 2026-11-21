namespace RgbStudio.Core;

public enum DeviceKind { Unknown, Motherboard, Ram, Gpu, Cooler, LedStrip, Keyboard, Mouse, Headset, Other }

public sealed record LedInfo(int Index, string Name);

/// <summary>Bir saglayicinin buldugu, isigi kontrol edilebilen tek cihaz.</summary>
public sealed class LightDevice
{
    public LightDevice(ILightingProvider provider, string id, string name, DeviceKind kind, IReadOnlyList<LedInfo> leds)
    {
        Provider = provider;
        Id = id;
        Name = name;
        Kind = kind;
        Leds = leds;
    }

    public ILightingProvider Provider { get; }

    /// <summary>Kalici kimlik: senaryolar cihazlari bu anahtarla esler (yeniden taramada degismez).</summary>
    public string Id { get; }
    public string Name { get; }
    public DeviceKind Kind { get; }
    public IReadOnlyList<LedInfo> Leds { get; }
    public int LedCount => Leds.Count;

    /// <summary>Her LED ayri renk alabiliyor mu (false ise tum LED'ler ayni renk olur).</summary>
    public bool PerLed { get; init; } = true;

    /// <summary>Donanimin kaldirabilecegi en yuksek yenileme hizi.</summary>
    public int MaxFps { get; init; } = 30;

    /// <summary>Kullaniciya gosterilecek kisitlama notu (orn. "yalnizca on plandayken").</summary>
    public string? Note { get; init; }

    /// <summary>Saglayiciya ozel nesne (OpenRGB indeksi, LampArray, MSI tipi...).</summary>
    public object? Handle { get; init; }

    /// <summary>Saglayici, cihazin durumunu kaybettigini fark ederse true yapar; motor bir sonraki karede tam yazar.</summary>
    public volatile bool NeedsFullWrite = true;

    public override string ToString() => $"{Name} ({Provider.Name})";
}

/// <summary>Bir donanim erisim katmani (OpenRGB, MSI Mystic Light, Windows Dinamik Aydinlatma...).</summary>
public interface ILightingProvider : IDisposable
{
    string Name { get; }

    /// <summary>Son tarama/baglanti sonucunun kisa aciklamasi.</summary>
    string Status { get; }

    /// <summary>Taramanin en fazla ne kadar surebilecegi (sonra atlanir, diger saglayicilar beklemez).</summary>
    TimeSpan DetectTimeout => TimeSpan.FromSeconds(45);

    /// <summary>Donanim listesi disaridan degisti (orn. OpenRGB yeniden taradi); yeniden tarama gerekli.</summary>
    event Action? Invalidated;

    /// <summary>Baglanir (gerekirse yeniden) ve cihazlari bulur. Her cagrida bastan tarar.</summary>
    Task<IReadOnlyList<LightDevice>> DetectAsync(CancellationToken ct);

    /// <summary>
    /// Cihaza bir kare yazar. Motor tarafindan tek bir arka plan is parcacigindan cagrilir.
    /// <paramref name="full"/> true ise mod/stil da yeniden ayarlanmali (ilk yazim, uyanma, periyodik tazeleme).
    /// </summary>
    void Write(LightDevice device, ReadOnlySpan<Rgb> colors, bool full);
}
