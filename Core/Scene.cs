using System.Text.Json.Serialization;

namespace RgbStudio.Core;

public enum EffectType
{
    /// <summary>Isik kapali.</summary>
    Off,
    /// <summary>Her LED kendi sabit rengi.</summary>
    Static,
    /// <summary>Tek renk, yavasca yanip soner.</summary>
    Breathing,
    /// <summary>LED'ler boyunca akan gokkusagi.</summary>
    Rainbow,
    /// <summary>Tum cihaz birlikte renkten renge gecer.</summary>
    ColorCycle,
    /// <summary>Iki renk arasinda gidip gelen gecis.</summary>
    Gradient,
}

/// <summary>Bir cihazin senaryodaki ayari.</summary>
public sealed class DeviceScene
{
    public EffectType Effect { get; set; } = EffectType.Static;

    /// <summary>
    /// Static: LED basina renk (eksikse son renk tekrarlanir).
    /// Breathing: [0] kullanilir. Gradient: [0] ve [1].
    /// </summary>
    public List<Rgb> Colors { get; set; } = new() { Rgb.White };

    /// <summary>Efekt hizi carpani (0.1 - 5).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Parlaklik yuzdesi (0 - 100).</summary>
    public int Brightness { get; set; } = 100;

    /// <summary>Kaydedildigi andaki cihaz adi (yalnizca bilgi amacli, okumayi kolaylastirir).</summary>
    public string? DeviceName { get; set; }

    [JsonIgnore]
    public bool IsAnimated => Effect is EffectType.Breathing or EffectType.Rainbow or EffectType.ColorCycle or EffectType.Gradient;

    public DeviceScene Clone() => new()
    {
        Effect = Effect,
        Colors = new List<Rgb>(Colors),
        Speed = Speed,
        Brightness = Brightness,
        DeviceName = DeviceName,
    };

    public Rgb ColorAt(int index) =>
        Colors.Count == 0 ? Rgb.White : Colors[Math.Clamp(index, 0, Colors.Count - 1)];

    /// <summary>Colors listesini en az <paramref name="count"/> elemana tamamlar.</summary>
    public void EnsureColorCount(int count)
    {
        var last = Colors.Count > 0 ? Colors[^1] : Rgb.White;
        while (Colors.Count < count) Colors.Add(last);
    }
}

/// <summary>JSON olarak kaydedilen senaryo: hangi cihaz hangi efekt ve renklerle yanacak.</summary>
public sealed class Scene
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Yeni senaryo";
    public string? Description { get; set; }
    public DateTime Modified { get; set; } = DateTime.Now;

    /// <summary>Senaryoda kaydi olmayan (yeni takilan) cihazlara uygulanacak ayar.</summary>
    public DeviceScene Default { get; set; } = new();

    /// <summary>Cihaz kimligi -> ayar.</summary>
    public Dictionary<string, DeviceScene> Devices { get; set; } = new();

    public DeviceScene For(LightDevice device)
    {
        if (!Devices.TryGetValue(device.Id, out var ds))
        {
            ds = Default.Clone();
            ds.DeviceName = device.Name;
            Devices[device.Id] = ds;
        }
        return ds;
    }

    public Scene Clone() => new()
    {
        FormatVersion = FormatVersion,
        Name = Name,
        Description = Description,
        Modified = Modified,
        Default = Default.Clone(),
        Devices = Devices.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
    };
}
