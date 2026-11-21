using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace RgbStudio.Views;

public partial class AboutView : UserControl
{
    public sealed record Credit(string Name, string Author, string License, string Role, Uri Url);

    static readonly Credit[] All =
    {
        new("OpenRGB", "Adam Honse (CalcProgrammer1) ve katkıda bulunanlar", "GPL-2.0",
            "RAM, ekran kartı ve pek çok RGB cihazına üreticiden bağımsız erişim. RGB Studio, OpenRGB'nin SDK sunucusuna ağ protokolüyle bağlanır; OpenRGB'yi içermez.",
            new Uri("https://openrgb.org")),
        new("PawnIO", "namazso", "Proje sayfasına bakın",
            "İmzalı, modüler çekirdek sürücüsü. OpenRGB, RAM ve anakart SMBus'ına bu sürücü üzerinden güvenli şekilde erişir.",
            new Uri("https://github.com/namazso/PawnIO")),
        new("OpenRGB.NET", "Diogo Trindade", "MIT",
            "OpenRGB SDK protokolünün .NET istemcisi.",
            new Uri("https://github.com/diogotr7/OpenRGB.NET")),
        new("MSI Mystic Light SDK", "Micro-Star International Co., Ltd.", "MSI",
            "MSI anakart ve ARGB girişlerine (fanlar, şeritler, sıvı soğutucu) resmi erişim. MSI Center ve Mystic Light modülünü gerektirir.",
            new Uri("https://www.msi.com/Landing/mystic-light-rgb-gaming-pc/download")),
        new("Windows Dinamik Aydınlatma", "Microsoft", "Windows API",
            "LampArray API ile Windows 11 uyumlu klavye ve çevre birimlerini tuş tuş kontrol.",
            new Uri("https://learn.microsoft.com/uwp/api/windows.devices.lights.lamparray")),
        new("WPF UI", "lepo.co (Leszek Pomianowski) ve katkıda bulunanlar", "MIT",
            "Windows 11 Fluent tasarımlı arayüz bileşenleri ve Mica pencere.",
            new Uri("https://github.com/lepoco/wpfui")),
        new("H.NotifyIcon", "HavenDV", "MIT",
            "Simge alanı (sistem tepsisi) simgesi ve menüsü.",
            new Uri("https://github.com/HavenDV/H.NotifyIcon")),
        new("CommunityToolkit.Mvvm", "Microsoft / .NET Foundation", "MIT",
            "Arayüz veri bağlama yardımcıları.",
            new Uri("https://github.com/CommunityToolkit/dotnet")),
    };

    public AboutView()
    {
        InitializeComponent();
        var v = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"Sürüm {v?.ToString(3)}  ·  .NET {Environment.Version}";
        Credits.ItemsSource = All;
    }

    async void CheckUpdate_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateResult.Text = "Denetleniyor…";
        UpdateResult.Text = await App.Current.CheckForUpdateAsync(userAction: true) ?? "";
        CheckUpdateButton.IsEnabled = true;
    }

    void GitHub_Click(object sender, System.Windows.RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"https://github.com/{Core.Updater.Repo}") { UseShellExecute = true });

    void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
