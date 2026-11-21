using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RgbStudio.Core;
using RgbStudio.Effects;

namespace RgbStudio.Views;

public partial class DevicesView : UserControl
{
    readonly ObservableCollection<DeviceItem> _devices = new();
    readonly ObservableCollection<LedItem> _leds = new();
    DeviceItem? _current;
    bool _loading;
    int _lastClickedLed = -1;

    static readonly EffectOption[] Effects = Enum.GetValues<EffectType>()
        .Select(e => new EffectOption(e, EffectRenderer.DisplayName(e))).ToArray();

    public DevicesView()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        LedGrid.ItemsSource = _leds;
        EffectBox.ItemsSource = Effects;
        Picker.ColorChanged += OnPickerColorChanged;
        foreach (var led in _leds) led.PropertyChanged += Led_PropertyChanged;

        App.Engine.DevicesChanged += () => Dispatcher.BeginInvoke(RebuildDevices);
        // Senaryo disaridan degisirse (senaryo uygulandi vb.) gorunumu tazele.
        App.Current.StatusChanged += () => Dispatcher.BeginInvoke(() => { if (!_loading) { RefreshPreviews(); ReloadDetail(); } });
        RebuildDevices();
    }

    LightDevice? Device => _current?.Device;

    // ---- Cihaz listesi ----

    void RebuildDevices()
    {
        string? selectedId = _current?.Device.Id;
        _devices.Clear();
        foreach (var d in App.Engine.Devices) _devices.Add(new DeviceItem(d));
        EmptyText.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreviews();
        DeviceList.SelectedItem = _devices.FirstOrDefault(d => d.Device.Id == selectedId) ?? _devices.FirstOrDefault();
        if (_devices.Count == 0) { _current = null; Detail.Visibility = Visibility.Collapsed; }
    }

    void RefreshPreviews()
    {
        foreach (var item in _devices)
        {
            var (c, e) = App.Engine.Read(s =>
            {
                var ds = s.For(item.Device);
                return (ds.ColorAt(0), ds.Effect);
            });
            item.Preview = e switch
            {
                EffectType.Off => Brushes2.Of(Rgb.Black),
                EffectType.Rainbow or EffectType.ColorCycle => RainbowBrush,
                _ => Brushes2.Of(c),
            };
            item.EffectName = EffectRenderer.DisplayName(e);
        }
    }

    static readonly System.Windows.Media.Brush RainbowBrush = CreateRainbow();

    static System.Windows.Media.Brush CreateRainbow()
    {
        var b = new System.Windows.Media.LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i <= 6; i++)
        {
            var c = Rgb.FromHsv(i * 60, 1, 1);
            b.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B), i / 6.0));
        }
        b.Freeze();
        return b;
    }

    void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = DeviceList.SelectedItem as DeviceItem;
        _lastClickedLed = -1;
        LoadLeds();
        ReloadDetail();
    }

    void LoadLeds()
    {
        foreach (var l in _leds) l.PropertyChanged -= Led_PropertyChanged;
        _leds.Clear();
        if (Device == null) return;
        foreach (var led in Device.Leds)
        {
            var item = new LedItem(led);
            item.PropertyChanged += Led_PropertyChanged;
            _leds.Add(item);
        }
    }

    // ---- Ayrinti paneli ----

    void ReloadDetail()
    {
        var dev = Device;
        if (dev == null) { Detail.Visibility = Visibility.Collapsed; return; }
        Detail.Visibility = Visibility.Visible;

        _loading = true;
        try
        {
            var ds = App.Engine.Read(s => s.For(dev).Clone());
            DeviceTitle.Text = dev.Name;
            DeviceSub.Text = $"{dev.Provider.Name}  ·  {(dev.PerLed ? $"{dev.LedCount} ayrı LED" : "tek renk bölgesi")}";
            DeviceSub.ToolTip = $"Efektlerde en fazla {dev.MaxFps} kare/sn yenilenir.";

            NoteBar.IsOpen = dev.Note != null;
            NoteBar.Message = dev.Note ?? "";

            EffectBox.SelectedItem = Effects.First(o => o.Type == ds.Effect);
            SpeedSlider.Value = ds.Speed;
            SpeedText.Text = $"{ds.Speed:0.0}×";
            BrightnessSlider.Value = ds.Brightness;
            BrightnessText.Text = $"%{ds.Brightness}";

            UpdateVisibility(ds.Effect);
            Picker.SetColor(CurrentEditColor(ds));
            RefreshLedBrushes(ds);
        }
        finally { _loading = false; }
    }

    void UpdateVisibility(EffectType e)
    {
        var dev = Device!;
        bool hasColor = e is EffectType.Static or EffectType.Breathing or EffectType.Gradient;
        ColorCard.Visibility = hasColor ? Visibility.Visible : Visibility.Collapsed;
        SlotPanel.Visibility = e == EffectType.Gradient ? Visibility.Visible : Visibility.Collapsed;
        SpeedRow.Visibility = e is EffectType.Breathing or EffectType.Rainbow or EffectType.ColorCycle or EffectType.Gradient
            ? Visibility.Visible : Visibility.Collapsed;
        LedCard.Visibility = dev.PerLed && dev.LedCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        LedTitle.Text = $"LED'ler ({dev.LedCount})";

        ColorHint.Text = e switch
        {
            EffectType.Static when dev.PerLed && dev.LedCount > 1 =>
                SelectedIndices().Count is var n && n > 0 ? $"Renk seçili {n} LED'e uygulanıyor." : "Renk bu cihazın bütün LED'lerine uygulanıyor.",
            EffectType.Breathing => "Nefes efekti bu rengi yakıp söndürür.",
            EffectType.Gradient => "Işıklar iki renk arasında yumuşakça gidip gelir.",
            _ => "",
        };
    }

    Rgb CurrentEditColor(DeviceScene ds) => ds.Effect switch
    {
        EffectType.Gradient when Slot2.IsChecked == true => ds.Colors.Count > 1 ? ds.Colors[1] : Rgb.Black,
        EffectType.Static => ds.ColorAt(SelectedIndices().FirstOrDefault()),
        _ => ds.ColorAt(0),
    };

    void RefreshLedBrushes(DeviceScene? ds = null)
    {
        var dev = Device;
        if (dev == null) return;
        ds ??= App.Engine.Read(s => s.For(dev).Clone());
        var frame = new Rgb[Math.Max(dev.LedCount, 1)];
        if (ds.Effect is EffectType.Static or EffectType.Off) EffectRenderer.Render(ds, 0, frame);
        else for (int i = 0; i < frame.Length; i++) frame[i] = ds.ColorAt(i);
        foreach (var led in _leds) led.SetColor(frame[Math.Min(led.Index, frame.Length - 1)]);
        RefreshPreviews();
    }

    List<int> SelectedIndices() => _leds.Where(l => l.IsSelected).Select(l => l.Index).ToList();

    // ---- Duzenleme ----

    void Edit(Action<DeviceScene> change)
    {
        var dev = Device;
        if (dev == null || _loading) return;
        App.Engine.Edit(s => change(s.For(dev)));
        // Isiklar kapaliyken renk secmek hicbir sey gostermezdi; duzenleme isiklari acar.
        if (App.Settings.MasterOff) App.Current.SetMasterOff(false);
        RefreshLedBrushes();
    }

    void EffectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _loading || EffectBox.SelectedItem is not EffectOption opt) return;
        Edit(ds =>
        {
            ds.Effect = opt.Type;
            if (opt.Type == EffectType.Gradient && ds.Colors.Count < 2)
                ds.Colors.Add(Rgb.FromHsv(ds.ColorAt(0).ToHsv().H + 180, 1, 1));
        });
        ReloadDetail();
    }

    void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return; // InitializeComponent sirasinda tetiklenir
        SpeedText.Text = $"{SpeedSlider.Value:0.0}×";
        Edit(ds => ds.Speed = Math.Round(SpeedSlider.Value, 1));
    }

    void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        BrightnessText.Text = $"%{(int)BrightnessSlider.Value}";
        Edit(ds => ds.Brightness = (int)BrightnessSlider.Value);
    }

    void Slot_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _loading || Device == null) return;
        var ds = App.Engine.Read(s => s.For(Device).Clone());
        _loading = true;
        Picker.SetColor(CurrentEditColor(ds));
        _loading = false;
    }

    void OnPickerColorChanged(Rgb c)
    {
        var dev = Device;
        if (dev == null || _loading) return;
        var selected = SelectedIndices();
        bool slot2 = Slot2.IsChecked == true;
        Edit(ds =>
        {
            switch (ds.Effect)
            {
                case EffectType.Static:
                    if (!dev.PerLed || dev.LedCount <= 1 || selected.Count == 0 || selected.Count == dev.LedCount)
                    {
                        // Tum cihaz tek renk: listeyi sade tut (JSON okunur kalir).
                        ds.Colors = new List<Rgb> { c };
                    }
                    else
                    {
                        // LED basina renk: once mevcut gorunumu acikca listeye yaz, sonra secilenleri boya.
                        var expanded = Enumerable.Range(0, dev.LedCount).Select(ds.ColorAt).ToList();
                        foreach (var i in selected) expanded[i] = c;
                        ds.Colors = expanded;
                    }
                    break;
                case EffectType.Gradient:
                    while (ds.Colors.Count < 2) ds.Colors.Add(Rgb.Black);
                    ds.Colors[slot2 ? 1 : 0] = c;
                    break;
                default:
                    if (ds.Colors.Count == 0) ds.Colors.Add(c);
                    else ds.Colors[0] = c;
                    break;
            }
        });
    }

    // ---- LED secimi ----

    void Led_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LedItem led }) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift) && _lastClickedLed >= 0)
        {
            int a = Math.Min(_lastClickedLed, led.Index), b = Math.Max(_lastClickedLed, led.Index);
            foreach (var l in _leds) l.IsSelected = l.Index >= a && l.Index <= b;
            e.Handled = true;
        }
        else if (!mods.HasFlag(ModifierKeys.Control))
        {
            // Tek tik: yalnizca bunu sec (zaten tek secili ise secimi kaldir).
            bool only = led.IsSelected && _leds.Count(l => l.IsSelected) == 1;
            foreach (var l in _leds) l.IsSelected = false;
            led.IsSelected = !only;
            e.Handled = true;
        }
        _lastClickedLed = led.Index;
    }

    void Led_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LedItem.IsSelected) || _loading || Device == null) return;
        // Secim degisince renk seciciyi ilk secili LED'in rengine getir ve ipucunu guncelle.
        Dispatcher.BeginInvoke(() =>
        {
            var ds = App.Engine.Read(s => s.For(Device).Clone());
            _loading = true;
            Picker.SetColor(CurrentEditColor(ds));
            UpdateVisibility(ds.Effect);
            _loading = false;
        });
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var l in _leds) l.IsSelected = true;
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var l in _leds) l.IsSelected = false;
    }

    // ---- Eylemler ----

    void Identify_Click(object sender, RoutedEventArgs e)
    {
        if (Device != null) App.Engine.IdentifyDevice(Device.Id);
    }

    void IdentifyLed_Click(object sender, RoutedEventArgs e)
    {
        if (Device == null) return;
        var sel = SelectedIndices();
        App.Engine.IdentifyDevice(Device.Id, sel.Count > 0 ? sel[0] : -1);
    }

    void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        var dev = Device;
        if (dev == null) return;
        App.Engine.Edit(s =>
        {
            var src = s.For(dev).Clone();
            foreach (var other in App.Engine.Devices)
            {
                if (other.Id == dev.Id) continue;
                var copy = src.Clone();
                copy.DeviceName = other.Name;
                // LED sayilari farkli olabilir: LED basina desen yerine ilk rengi kullan.
                if (copy.Effect == EffectType.Static && copy.Colors.Distinct().Count() > 1 && other.LedCount != dev.LedCount)
                    copy.Colors = new List<Rgb> { copy.ColorAt(0) };
                s.Devices[other.Id] = copy;
            }
            s.Default = src.Clone();
        });
        RefreshPreviews();
    }
}
