using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RgbStudio.Core;
using Wpf.Ui.Controls;

namespace RgbStudio.Views;

public static class Brushes2
{
    public static SolidColorBrush Of(Rgb c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}

public sealed partial class DeviceItem : ObservableObject
{
    public DeviceItem(LightDevice device) => Device = device;

    public LightDevice Device { get; }
    public string Name => Device.Name;

    public string Subtitle =>
        $"{Device.Provider.Name}  ·  {(Device.PerLed ? $"{Device.LedCount} LED" : "tek bölge")}";

    public SymbolRegular Symbol => Device.Kind switch
    {
        DeviceKind.Ram => SymbolRegular.Ram20,
        DeviceKind.Gpu => SymbolRegular.DeveloperBoard24,
        DeviceKind.Motherboard => SymbolRegular.DeveloperBoard24,
        DeviceKind.Keyboard => SymbolRegular.Keyboard24,
        DeviceKind.Mouse => SymbolRegular.CursorClick24,
        DeviceKind.Cooler => SymbolRegular.Temperature24,
        DeviceKind.Headset => SymbolRegular.HeadphonesSoundWave24,
        DeviceKind.LedStrip => SymbolRegular.LineHorizontal120,
        _ => SymbolRegular.Lightbulb24,
    };

    [ObservableProperty] Brush _preview = Brushes.Transparent;
    [ObservableProperty] string _effectName = "";
}

public sealed partial class LedItem : ObservableObject
{
    public LedItem(LedInfo led) => Led = led;

    public LedInfo Led { get; }
    public int Index => Led.Index;
    public string Name => Led.Name;
    public string Label => (Led.Index + 1).ToString();

    [ObservableProperty] Brush _brush = Brushes.Black;
    [ObservableProperty] Brush _labelBrush = Brushes.White;
    [ObservableProperty] bool _isSelected;

    public void SetColor(Rgb c)
    {
        Brush = Brushes2.Of(c);
        // Algilanan parlaklik (Rec. 601): acik zeminde siyah, koyu zeminde beyaz numara.
        LabelBrush = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150 ? Brushes.Black : Brushes.White;
    }
}

public sealed record EffectOption(EffectType Type, string Name)
{
    public override string ToString() => Name;
}
