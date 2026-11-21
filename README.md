# RGB Studio

Bilgisayardaki tüm RGB ışıklarını tek yerden yöneten, açık kaynak bir Windows uygulaması.
RAM, ekran kartı, anakart, kasa fanları, LED şeritler, sıvı soğutucu ve klavye ışıklarını birlikte ya da LED LED kontrol eder.

*An open-source Windows app to control all RGB lighting in one place — see [English](#english) below.*

## Özellikler

- **Tek düğmeyle tüm ışıkları aç / kapat** (simge alanından da)
- **Cihaz cihaz, LED LED renk**: her cihaz ayrı listelenir; tek LED, Ctrl ile çoklu, Shift ile aralık seçimi
- **Efektler**: sabit renk, nefes, gökkuşağı, renk döngüsü, geçiş (hız ve parlaklık ayarlı)
- **Senaryolar**: tüm ayarlar JSON olarak kaydedilir; dışa / içe aktarılabilir, elle düzenlenebilir
- **Uykudan uyanınca** ışıkları yeniden uygular (birçok cihaz uyanınca varsayılan efektine döner)
- **Windows açılışında** izin sormadan simge alanında başlar (Görev Zamanlayıcı)
- Windows 11 görünümü (Mica, açık/koyu tema)

## Desteklenen donanım

RGB Studio ışıklara üç katman üzerinden ulaşır:

| Katman | Ne kontrol eder | Gereken |
|---|---|---|
| [OpenRGB](https://openrgb.org) SDK sunucusu | RAM, ekran kartı, birçok anakart (ASUS Aura, Gigabyte RGB Fusion, ASRock…), USB cihazlar | OpenRGB kurulu olmalı. RAM/SMBus erişimi için OpenRGB'nin [PawnIO](https://pawnio.eu) sürücüsü |
| MSI Mystic Light SDK | MSI anakart ve ARGB girişleri (fanlar, şeritler, soğutucu) | MSI Center (Mystic Light). SDK dosyası uygulama içinden MSI'ın sunucusundan indirilir |
| Windows Dinamik Aydınlatma | Tuş tuş klavye ve uyumlu çevre birimleri | Windows 11 |

OpenRGB'nin desteklediği cihazların listesi: <https://openrgb.org/devices.html>

> Klavyeler (Dinamik Aydınlatma): Windows ışık kontrolünü yalnızca öndeki uygulamaya verir; RGB Studio penceresi arka plandayken üreticinin programı devralabilir.

## Kurulum

1. [.NET 9 Masaüstü Çalışma Zamanı](https://dotnet.microsoft.com/download/dotnet/9.0) (x64, "Desktop Runtime") kurulu değilse kurun.
2. RAM, ekran kartı ve ASUS/Gigabyte anakartlar için [OpenRGB](https://openrgb.org)'yi kurun ve bir kez açıp cihazlarınızı gördüğünden emin olun. RGB Studio, OpenRGB'yi gerektiğinde kendisi arka planda başlatır.
3. [Releases](../../releases) sayfasından son `RGBStudio-*.zip` dosyasını indirip bir klasöre açın, `RgbStudio.exe`'yi çalıştırın.
4. MSI anakartınız varsa: **Ayarlar → MSI Mystic Light SDK'yı indir**.

Uygulama yönetici izni ister: RAM ve anakart ışıklarına erişen sürücüler bunu gerektiriyor.

## Senaryo dosyaları

Senaryolar `%APPDATA%\RgbStudio\Scenes\*.json` içinde durur. Örnek:

```json
{
  "formatVersion": 1,
  "name": "Mor-turkuaz geçiş",
  "default": { "effect": "gradient", "colors": ["#8A2BE2", "#00E5C8"], "speed": 1, "brightness": 100 },
  "devices": {
    "openrgb:RAM 1 (...)|...": { "effect": "static", "colors": ["#FF0000"], "brightness": 60 }
  }
}
```

`default`, senaryoda ayrıca tanımlanmamış her cihaza uygulanır. Efektler: `off`, `static`, `breathing`, `rainbow`, `colorCycle`, `gradient`. `static` için `colors` LED başına renk listesi olabilir.

## Derleme

```bash
dotnet build -c Release
dotnet publish -c Release -o publish
```

Gereken: .NET 9 SDK, Windows 10 2004+ / Windows 11.

## Güvenlik notları

- MSI'ın SDK dosyası bu depoda **yoktur**; yalnızca kullanıcı onaylarsa `download.msi.com` adresinden indirilir. Her yüklemeden önce dosyanın MSI tarafından imzalandığı (Authenticode) doğrulanır.
- RGB Studio kendi başına çekirdek sürücüsü kurmaz; donanım erişimi OpenRGB/PawnIO, MSI ve Windows üzerinden yapılır.

## Krediler

- [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) — Adam Honse (CalcProgrammer1) ve katkıcılar, GPL-2.0. RGB Studio, OpenRGB'ye ağ protokolü (SDK sunucusu) üzerinden bağlanır; OpenRGB koduyla birlikte dağıtılmaz.
- [PawnIO](https://pawnio.eu) — namazso
- [OpenRGB.NET](https://github.com/diogotr7/OpenRGB.NET) — Diogo Trindade, MIT
- MSI Mystic Light SDK — Micro-Star International
- [Windows.Devices.Lights.LampArray](https://learn.microsoft.com/uwp/api/windows.devices.lights.lamparray) — Microsoft
- [WPF UI](https://github.com/lepoco/wpfui) — lepo.co, MIT
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) — HavenDV, MIT
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — Microsoft, MIT

Bu proje MSI, ASUS, Gigabyte ya da Microsoft ile bağlantılı değildir; markalar sahiplerine aittir.

## Lisans

[MIT](LICENSE)

---

## English

**RGB Studio** is an open-source Windows app (WPF, .NET 9) that controls all RGB lighting in a PC from one place: one master on/off switch, per-device and per-LED colours, effects (static, breathing, rainbow, colour cycle, gradient), scenes saved as JSON, re-apply after sleep, and start with Windows.

Hardware is reached through three layers:

- **OpenRGB SDK server** for RAM, GPUs and many motherboards (ASUS Aura, Gigabyte RGB Fusion…). OpenRGB must be installed; it uses PawnIO for SMBus access.
- **MSI Mystic Light SDK** for MSI boards and their ARGB headers. The SDK DLL is not redistributed; the app downloads it from MSI on request and verifies MSI's Authenticode signature before every load.
- **Windows Dynamic Lighting (LampArray)** for per-key keyboards.

Requirements: Windows 10 2004+/11, .NET 9 Desktop Runtime (x64). Runs elevated. The UI is currently Turkish only — translations are welcome.

Build: `dotnet build -c Release`. Licence: MIT.
