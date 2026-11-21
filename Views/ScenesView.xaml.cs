using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RgbStudio.Core;
using MessageBox = System.Windows.MessageBox;

namespace RgbStudio.Views;

public partial class ScenesView : UserControl
{
    public sealed record SceneRow(SceneStore.SceneFile File)
    {
        public Scene Scene => File.Scene;
        public string Name => Scene.Name;
        public string Details => $"{Scene.Devices.Count} cihaz  ·  son değişiklik {Scene.Modified:dd.MM.yyyy HH:mm}  ·  {Path.GetFileName(File.Path)}";
    }

    public ScenesView()
    {
        InitializeComponent();
        App.Current.ScenesChanged += () => Dispatcher.BeginInvoke(Reload);
        IsVisibleChanged += (_, _) => { if (IsVisible) Reload(); };
        Reload();
    }

    void Reload()
    {
        var rows = SceneStore.LoadAll().Select(s => new SceneRow(s)).ToList();
        SceneList.ItemsSource = rows;
        NoScenes.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = App.Settings.LastSceneName ?? "";
    }

    void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Save_Click(sender, e);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { SaveResult.Text = "Önce bir ad yazın."; return; }

        var path = SceneStore.FileNameFor(name);
        if (File.Exists(path) &&
            MessageBox.Show($"\"{name}\" adlı senaryo zaten var. Üzerine yazılsın mı?", "RGB Studio",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var scene = App.Engine.Snapshot();
        scene.Name = name;
        foreach (var d in App.Engine.Devices)
            if (scene.Devices.TryGetValue(d.Id, out var ds)) ds.DeviceName = d.Name;
        // Su an taranamayan cihazlarin (orn. MSI o an yanit vermediyse) eski ayarlari kaybolmasin.
        SceneStore.MergeMissingDevices(scene, path);

        try
        {
            SceneStore.Save(scene);
            App.Engine.Edit(s => s.Name = name);
            App.Settings.LastSceneName = name;
            App.Settings.Save();
            SaveResult.Text = $"Kaydedildi: {Path.GetFileName(path)}";
            App.Current.NotifyScenesChanged();
        }
        catch (Exception ex)
        {
            SaveResult.Text = "Kaydedilemedi: " + ex.Message;
        }
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SceneRow row }) App.Current.ApplyScene(row.Scene);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SceneRow row }) return;
        if (MessageBox.Show($"\"{row.Name}\" senaryosu ({Path.GetFileName(row.File.Path)}) Geri Dönüşüm Kutusu'na taşınsın mı?", "RGB Studio",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { SceneStore.Delete(row.File); }
        catch (Exception ex) { MessageBox.Show("Silinemedi: " + ex.Message, "RGB Studio"); }
        App.Current.NotifyScenesChanged();
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SceneRow row }) return;
        var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileName(SceneStore.FileNameFor(row.Name)),
            Filter = "RGB Studio senaryosu (*.json)|*.json",
        };
        if (dlg.ShowDialog() == true) SceneStore.Save(row.Scene.Clone(), dlg.FileName);
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "RGB Studio senaryosu (*.json)|*.json", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        var errors = new List<string>();
        foreach (var f in dlg.FileNames)
        {
            try
            {
                var (scene, target) = SceneStore.PrepareImport(f);
                if (File.Exists(target) &&
                    MessageBox.Show($"\"{scene.Name}\" adlı senaryo zaten var ({Path.GetFileName(target)}). Üzerine yazılsın mı?",
                        "RGB Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    continue;
                SceneStore.Save(scene, target);
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        App.Current.NotifyScenesChanged();
        if (errors.Count > 0)
            MessageBox.Show("Bazı dosyalar alınamadı:\n\n" + string.Join("\n", errors), "RGB Studio",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.Ensure();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Scenes}\"") { UseShellExecute = true });
    }

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        DeviceScene ds = key switch
        {
            "white" or "warm" => new() { Effect = EffectType.Static },
            "rainbow" => new() { Effect = EffectType.Rainbow, Speed = 1 },
            "cycle" => new() { Effect = EffectType.ColorCycle, Speed = 1 },
            "breath" => new() { Effect = EffectType.Breathing, Speed = 1 },
            "gradient" => new() { Effect = EffectType.Gradient, Speed = 1 },
            "night" => new() { Effect = EffectType.Static, Brightness = 25 },
            _ => new() { Effect = EffectType.Off },
        };
        ds.Colors = key switch
        {
            "warm" => new() { Rgb.Parse("#FFB46B") },
            "breath" => new() { Rgb.Parse("#0078FF") },
            "gradient" => new() { Rgb.Parse("#8A2BE2"), Rgb.Parse("#00E5C8") },
            "night" => new() { Rgb.Parse("#FF1A00") },
            _ => new() { Rgb.White },
        };

        var label = ((ContentControl)sender).Content?.ToString() ?? key;
        var scene = new Scene { Name = label, Default = ds };
        foreach (var d in App.Engine.Devices)
        {
            var copy = ds.Clone();
            copy.DeviceName = d.Name;
            scene.Devices[d.Id] = copy;
        }
        App.Current.ApplyScene(scene);
    }
}
