using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RgbStudio.Core;
using RgbStudio.Providers;

namespace RgbStudio.Views;

public partial class SettingsView : UserControl
{
    bool _loading;

    public sealed record ProviderRow(string Name, string Description, string Status);

    public SettingsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) Load(); };
        App.Current.StatusChanged += () => Dispatcher.BeginInvoke(LoadProviders);
        Load();
    }

    void Load()
    {
        _loading = true;
        var s = App.Settings;
        ResumeSwitch.IsChecked = s.ReapplyOnResume;
        StartupApplySwitch.IsChecked = s.ApplyOnStartup;
        TraySwitch.IsChecked = s.MinimizeToTray;
        UpdateSwitch.IsChecked = s.CheckForUpdates;
        ThemeBox.SelectedIndex = (int)s.Theme;
        try { AutostartSwitch.IsChecked = Startup.IsEnabled(); } catch { AutostartSwitch.IsChecked = false; }
        _loading = false;
        LoadProviders();
    }

    void LoadProviders()
    {
        string Describe(ILightingProvider p) => p switch
        {
            OpenRgbProvider => "RAM, ekran kartı ve desteklenen cihazlar. Donanıma PawnIO sürücüsüyle erişir.",
            MysticLightProvider => "MSI anakart ve ARGB girişleri: kasa fanları, LED şeritler, sıvı soğutucu.",
            DynamicLightingProvider => "Windows 11 Dinamik Aydınlatma: tuş tuş klavye ve uyumlu çevre birimleri.",
            _ => "",
        };
        ProviderList.ItemsSource = App.Engine.Providers.Select(p => new ProviderRow(p.Name, Describe(p), App.Engine.StatusOf(p))).ToList();
        bool needMsi = App.Engine.Providers.OfType<MysticLightProvider>().Any(p => p.SdkMissing) && MsiSdk.IsMsiBoard();
        if (MsiSdkButton.IsEnabled) MsiSdkCard.Visibility = needMsi ? Visibility.Visible : Visibility.Collapsed;
    }

    async void MsiSdk_Click(object sender, RoutedEventArgs e)
    {
        MsiSdkButton.IsEnabled = false;
        MsiSdkHint.Text = "İndiriliyor…";
        try
        {
            await MsiSdk.DownloadAsync();
            MsiSdkHint.Text = "İndirildi ve MSI imzası doğrulandı. Cihazlar yeniden taranıyor…";
            MsiSdkCard.Visibility = Visibility.Collapsed;
            await App.Current.ScanAsync();
        }
        catch (Exception ex)
        {
            Log.Error("MSI SDK indirilemedi", ex);
            MsiSdkHint.Text = "İndirilemedi: " + ex.Message;
        }
        finally { MsiSdkButton.IsEnabled = true; }
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        s.ReapplyOnResume = ResumeSwitch.IsChecked == true;
        s.ApplyOnStartup = StartupApplySwitch.IsChecked == true;
        s.MinimizeToTray = TraySwitch.IsChecked == true;
        s.CheckForUpdates = UpdateSwitch.IsChecked == true;
        s.Save();
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool want = AutostartSwitch.IsChecked == true;
        string? err = want ? Startup.Enable() : Startup.Disable();
        if (err != null)
        {
            AutostartHint.Text = "Ayarlanamadı: " + err;
            _loading = true;
            AutostartSwitch.IsChecked = !want;
            _loading = false;
        }
        else
        {
            AutostartHint.Text = want
                ? "Açık: oturum açtıktan ~15 sn sonra simge alanında başlar."
                : "Kapalı: Windows açılışında başlamaz.";
        }
    }

    void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _loading || ThemeBox.SelectedIndex < 0) return;
        App.Settings.Theme = (ThemeChoice)ThemeBox.SelectedIndex;
        App.Settings.Save();
        App.Current.ApplyTheme();
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AppPaths.LogFile))
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.Ensure();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
    }
}
