using RgbStudio.Core;

namespace RgbStudio.Effects;

/// <summary>Bir DeviceScene'i verilen zamanda LED renklerine cevirir. Saf fonksiyon, donanima dokunmaz.</summary>
public static class EffectRenderer
{
    public static void Render(DeviceScene s, double t, Span<Rgb> output)
    {
        int n = output.Length;
        double speed = Math.Clamp(s.Speed, 0.1, 5);

        switch (s.Effect)
        {
            case EffectType.Off:
                output.Fill(Rgb.Black);
                return;

            case EffectType.Static:
                for (int i = 0; i < n; i++) output[i] = s.ColorAt(i);
                break;

            case EffectType.Breathing:
            {
                // 4 saniyelik dongu (hiz 1'de); yumusak kosinus egrisi.
                double phase = t * speed / 4.0 * 2 * Math.PI;
                double f = 0.5 - 0.5 * Math.Cos(phase);
                output.Fill(s.ColorAt(0).Scale(f));
                break;
            }

            case EffectType.Rainbow:
            {
                double baseHue = t * speed * 60;
                int span = Math.Max(n, 8);
                for (int i = 0; i < n; i++)
                    output[i] = Rgb.FromHsv(baseHue + i * 360.0 / span, 1, 1);
                break;
            }

            case EffectType.ColorCycle:
                output.Fill(Rgb.FromHsv(t * speed * 40, 1, 1));
                break;

            case EffectType.Gradient:
            {
                var a = s.ColorAt(0);
                var b = s.Colors.Count > 1 ? s.ColorAt(1) : Rgb.Black;
                double phase = t * speed / 6.0 * 2 * Math.PI;
                for (int i = 0; i < n; i++)
                {
                    double offset = n > 1 ? (double)i / n * Math.PI : 0;
                    output[i] = Rgb.Lerp(a, b, 0.5 - 0.5 * Math.Cos(phase + offset));
                }
                break;
            }
        }

        if (s.Brightness < 100)
        {
            double f = Math.Clamp(s.Brightness, 0, 100) / 100.0;
            for (int i = 0; i < n; i++) output[i] = output[i].Scale(f);
        }
    }

    public static string DisplayName(EffectType e) => e switch
    {
        EffectType.Off => "Kapalı",
        EffectType.Static => "Sabit renk",
        EffectType.Breathing => "Nefes",
        EffectType.Rainbow => "Gökkuşağı",
        EffectType.ColorCycle => "Renk döngüsü",
        EffectType.Gradient => "İki renk geçişi",
        _ => e.ToString(),
    };
}
