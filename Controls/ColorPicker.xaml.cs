using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RgbStudio.Core;

namespace RgbStudio.Controls;

/// <summary>HSV renk secici. Kullanici degistirdiginde <see cref="ColorChanged"/> tetiklenir (kod ile SetColor tetiklemez).</summary>
public partial class ColorPicker : UserControl
{
    double _h, _s = 1, _v = 1;
    bool _dragSv, _dragHue;

    public static readonly string[] Presets =
    {
        "#FFFFFF", "#FF0000", "#FF6A00", "#FFD800", "#00FF21", "#00FFC8",
        "#00B7FF", "#0026FF", "#7A00FF", "#FF00DC", "#FF6EB4", "#FFB86B",
    };

    public sealed record Swatch(string Hex, Brush Brush);

    public ColorPicker()
    {
        InitializeComponent();
        Swatches.ItemsSource = Presets.Select(h =>
        {
            var c = Rgb.Parse(h);
            var b = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            b.Freeze();
            return new Swatch(h, b);
        }).ToList();
        SizeChanged += (_, _) => UpdateVisuals();
        Loaded += (_, _) => UpdateVisuals();
    }

    public event Action<Rgb>? ColorChanged;

    public Rgb Color => Rgb.FromHsv(_h, _s, _v);

    /// <summary>Rengi programla ayarlar; ColorChanged tetiklenmez.</summary>
    public void SetColor(Rgb c)
    {
        var (h, s, v) = c.ToHsv();
        // Gri tonlarda ton bilgisi yok; kullanicinin son tonunu koru.
        if (s > 0.001 && v > 0.001) _h = h;
        _s = s;
        _v = v;
        UpdateVisuals();
    }

    void Raise()
    {
        UpdateVisuals();
        ColorChanged?.Invoke(Color);
    }

    void UpdateVisuals()
    {
        var hueColor = Rgb.FromHsv(_h, 1, 1);
        SvArea.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(hueColor.R, hueColor.G, hueColor.B));
        var c = Color;
        Preview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        if (!HexBox.IsKeyboardFocused) HexBox.Text = c.Hex;

        double w = SvArea.ActualWidth, hgt = SvArea.ActualHeight;
        Canvas.SetLeft(SvThumb, _s * w - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _v) * hgt - SvThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _h / 360 * HueBorder.ActualWidth - HueThumb.Width / 2);
        Canvas.SetTop(HueThumb, 0);
    }

    void Sv_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragSv = true;
        ((UIElement)sender).CaptureMouse();
        UpdateSv(e.GetPosition(SvArea));
    }

    void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSv) UpdateSv(e.GetPosition(SvArea));
    }

    void UpdateSv(Point p)
    {
        _s = Math.Clamp(p.X / Math.Max(SvArea.ActualWidth, 1), 0, 1);
        _v = Math.Clamp(1 - p.Y / Math.Max(SvArea.ActualHeight, 1), 0, 1);
        Raise();
    }

    void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragHue = true;
        ((UIElement)sender).CaptureMouse();
        UpdateHue(e.GetPosition(HueBorder));
    }

    void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragHue) UpdateHue(e.GetPosition(HueBorder));
    }

    void UpdateHue(Point p)
    {
        _h = Math.Clamp(p.X / Math.Max(HueBorder.ActualWidth, 1), 0, 1) * 359.9;
        if (_s < 0.05) _s = 1;   // griden ton secince renk gorunsun
        if (_v < 0.05) _v = 1;
        Raise();
    }

    void Any_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragSv = _dragHue = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex } && Rgb.TryParse(hex, out var c))
        {
            SetColor(c);
            Raise();
        }
    }

    /// <summary>Tam 6 haneli bir HEX yazilinca beklemeden uygula (Enter'a gerek yok).</summary>
    void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var t = HexBox.Text.Trim().TrimStart('#');
        if (t.Length == 6 && Rgb.TryParse(t, out var c) && c != Color)
        {
            SetColor(c);
            Raise();
        }
    }

    void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) HexBox_Commit(sender, e);
    }

    void HexBox_Commit(object sender, RoutedEventArgs e)
    {
        if (Rgb.TryParse(HexBox.Text, out var c))
        {
            if (c == Color) return;
            SetColor(c);
            Raise();
        }
        else HexBox.Text = Color.Hex;
    }
}
