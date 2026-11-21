using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Win32;
using RgbStudio.Core;
using RgbStudio.Providers;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace RgbStudio;

public partial class App : Application
{
    Mutex? _singleInstance;
    EventWaitHandle? _showSignal;
    TaskbarIcon? _tray;
    MainWindow? _window;
    CancellationTokenSource? _resumeCts;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _rescanTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _rescanPending;

    public static LightingEngine Engine { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static new App Current => (App)Application.Current;

    /// <summary>Senaryo listesi degisti (kaydet/sil/ice aktar) — arayuz ve tepsi menusu yenilensin.</summary>
    public event Action? ScenesChanged;
    public event Action? StatusChanged;
    public string StatusText { get; private set; } = "Cihazlar aranıyor…";
    public bool IsScanning { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Guncellemeden sonra: eski surec tamamen kapanana kadar bekle (tek kopya kilidini o tutuyor).
        int ai = Array.IndexOf(e.Args, "--after-update");
        if (ai >= 0 && ai + 1 < e.Args.Length && int.TryParse(e.Args[ai + 1], out int oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(20000); } catch { /* zaten kapanmis */ }
        }

        // Tek kopya: ikinci acilis mevcut pencereyi one getirir.
        _singleInstance = new Mutex(true, "RgbStudio_SingleInstance", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "RgbStudio_Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Beklenmeyen hata", ex.Exception);
            ex.Handled = true;
        };

        AppPaths.Ensure();
        Log.Info($"RGB Studio {typeof(App).Assembly.GetName().Version} başladı");
        Settings = AppSettings.Load();
        Updater.CleanupOldFiles();
        if (ai >= 0) Log.Info($"Güncelleme tamamlandı: {Updater.Current}");
        if (!e.Args.Contains("--dry-run")) RetireRgbToggle();

        Engine = new LightingEngine(new ILightingProvider[]
        {
            new OpenRgbProvider(),
            new MysticLightProvider(),
            new DynamicLightingProvider(),
        });
        Engine.DryRun = e.Args.Contains("--dry-run");
        if (Engine.DryRun) Log.Info("Deneme modu: donanıma yazılmayacak.");
        if (!Settings.ApplyOnStartup) Engine.HoldWrites = true;
        var current = SceneStore.LoadCurrent();
        if (current != null) Engine.LoadScene(current, userAction: false);
        Engine.SetMasterOff(Settings.MasterOff, userAction: false);

        Engine.SceneEdited += () => Dispatcher.BeginInvoke(() => { _saveTimer.Stop(); _saveTimer.Start(); });
        // Donanim listesi degisti (OpenRGB yeniden taradi, klavye takildi...): kisa bir beklemeyle yeniden tara.
        Engine.DevicesInvalidated += () => Dispatcher.BeginInvoke(() => { _rescanTimer.Stop(); _rescanTimer.Start(); });
        _rescanTimer.Tick += async (_, _) => { _rescanTimer.Stop(); await ScanAsync(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SceneStore.SaveCurrent(Engine.Snapshot()); };

        ApplyTheme();
        CreateTray();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        bool minimized = e.Args.Contains("--minimized");
        _window = new MainWindow();
        if (!minimized) _window.Show();

        // Ilk denetim acilis yogunlugu gectikten sonra, sonra 12 saatte bir.
        _updateTimer.Tick += async (_, _) => { _updateTimer.Interval = TimeSpan.FromHours(12); await CheckForUpdateAsync(userAction: false); };
        _updateTimer.Start();

        await ScanAsync(applyOnStartup: true);
    }

    // ---- Guncelleme ----

    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    string? _notifiedTag;

    /// <summary>GitHub'da bulunan daha yeni surum (yoksa null).</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }
    public event Action? UpdateChanged;
    public bool IsUpdating { get; private set; }

    /// <summary>Yeni surum var mi bak. Kullanici istediyse sonuc ne olursa olsun mesaj dondurur.</summary>
    public async Task<string?> CheckForUpdateAsync(bool userAction)
    {
        if (!userAction && !Settings.CheckForUpdates) return null;
        try
        {
            AvailableUpdate = await Updater.CheckAsync();
            UpdateChanged?.Invoke();
            RebuildTrayMenu();
            if (AvailableUpdate is { } u)
            {
                Log.Info($"Yeni sürüm var: {u.Tag} (kurulu {Updater.Current})");
                if (_notifiedTag != u.Tag && _window?.IsVisible != true)
                {
                    _notifiedTag = u.Tag;
                    try { _tray?.ShowNotification("RGB Studio güncellemesi", $"{u.Tag} sürümü hazır. Simgeye sağ tıklayıp güncelleyebilirsiniz."); } catch { }
                }
                return $"Yeni sürüm var: {u.Tag}";
            }
            return $"RGB Studio güncel (sürüm {Updater.Current}).";
        }
        catch (Exception ex)
        {
            if (userAction) Log.Warn("Güncelleme denetlenemedi: " + ex.Message);
            return "Güncelleme denetlenemedi: " + ex.Message;
        }
    }

    /// <summary>Bulunan surumu indir, dogrula, dosyalari degistir ve yeniden baslat.</summary>
    public async Task<string?> InstallUpdateAsync(IProgress<double>? progress = null)
    {
        if (AvailableUpdate is not { } u || IsUpdating) return null;
        IsUpdating = true;
        UpdateChanged?.Invoke();
        try
        {
            Log.Info($"Güncelleme indiriliyor: {u.AssetName}");
            var staging = await Updater.DownloadAsync(u, progress);
            await Task.Run(() => Updater.Apply(staging));
            Log.Info($"Güncelleme dosyaları yerleştirildi ({u.Tag}); yeniden başlatılıyor.");
            try { Updater.Restart(minimized: _window?.IsVisible != true); }
            catch (Exception ex)
            {
                // Dosyalar yerinde; yalnizca otomatik yeniden acilis olmadi.
                Log.Error("Yeniden başlatılamadı", ex);
                AvailableUpdate = null;
                return $"{u.Tag} kuruldu. Lütfen RGB Studio'yu kapatıp yeniden açın.";
            }
            ExitApp();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Güncelleme kurulamadı", ex);
            return "Güncelleme kurulamadı: " + ex.Message;
        }
        finally
        {
            IsUpdating = false;
            UpdateChanged?.Invoke();
        }
    }

    /// <summary>
    /// RGB Studio, onceki "RGB Ac/Kapat" uygulamasinin yerini alir: ikisi birlikte calisirsa ayni
    /// isiklari yarisarak surer ve MSI SDK'si ikinci oturumda kilitlenir. Eskisini kapat, acilis gorevini devral.
    /// </summary>
    static void RetireRgbToggle()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("RgbToggle"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
                Log.Info($"Eski RGB Aç/Kapat kapatıldı (PID {p.Id}).");
            }
            catch (Exception ex) { Log.Warn("Eski RGB Aç/Kapat kapatılamadı: " + ex.Message); }
        }
        Core.Startup.MigrateLegacyTask();
    }

    public async Task ScanAsync(bool applyOnStartup = false)
    {
        if (IsScanning)
        {
            // Tarama surerken gelen degisiklik bildirimi kaybolmasin: bitince bir kez daha tara.
            _rescanPending = true;
            return;
        }
        IsScanning = true;
        _rescanPending = false;
        SetStatus("Cihazlar aranıyor…");
        try
        {
            // "Acilista son durumu uygula" kapaliysa kullanici bir sey degistirene kadar isiklara dokunma.
            if (applyOnStartup && !Settings.ApplyOnStartup) Engine.HoldWrites = true;
            await Engine.DetectAsync();
            SetStatus(string.Join("   ·   ", Engine.Providers.Select(p => $"{p.Name}: {Engine.StatusOf(p)}")));
        }
        catch (Exception ex)
        {
            Log.Error("Tarama hatası", ex);
            SetStatus("Tarama hatası: " + ex.Message);
        }
        finally
        {
            IsScanning = false;
            StatusChanged?.Invoke();
        }
        if (_rescanPending) { _rescanTimer.Stop(); _rescanTimer.Start(); }
    }

    void SetStatus(string s)
    {
        StatusText = s;
        StatusChanged?.Invoke();
    }

    // ---- Ana dugme ----

    public void SetMasterOff(bool off)
    {
        Engine.MasterOff = off;
        Settings.MasterOff = off;
        Settings.Save();
        UpdateTrayTooltip();
        StatusChanged?.Invoke();
    }

    public void ApplyScene(Scene scene)
    {
        Engine.LoadScene(scene);
        Settings.LastSceneName = scene.Name;
        if (Settings.MasterOff) SetMasterOff(false);
        Settings.Save();
        SceneStore.SaveCurrent(Engine.Snapshot());
        StatusChanged?.Invoke();
    }

    public void NotifyScenesChanged()
    {
        RebuildTrayMenu();
        ScenesChanged?.Invoke();
    }

    // ---- Tema ----

    public void ApplyTheme()
    {
        var theme = Settings.Theme switch
        {
            ThemeChoice.Dark => ApplicationTheme.Dark,
            ThemeChoice.Light => ApplicationTheme.Light,
            _ => SystemUsesLightTheme() ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
    }

    static bool SystemUsesLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    // ---- Uyku / uyanma ----

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || !Settings.ReapplyOnResume) return;
        _resumeCts?.Cancel();
        _resumeCts = new CancellationTokenSource();
        var token = _resumeCts.Token;
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Log.Info("Uykudan uyanıldı; ışıklar yeniden uygulanacak.");
                SetStatus("Uykudan uyanıldı, ışıklar yeniden ayarlanıyor…");
                // USB cihazlari ve MSI/ASUS servisleri uyanistan sonra gec hazir oluyor.
                await Task.Delay(TimeSpan.FromSeconds(8), token);
                await ScanAsync();
                foreach (var wait in new[] { 15, 30 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), token);
                    Engine.ForceFullWrite();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    // ---- Simge alani ----

    void CreateTray()
    {
        _tray = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseDown += (_, _) => ShowMainWindow();
        RebuildTrayMenu();
        UpdateTrayTooltip();
        _tray.ForceCreate(false); // false: Windows "verimlilik modu"na sokma (animasyonlar yavaslamasin)
    }

    void UpdateTrayTooltip()
    {
        if (_tray != null) _tray.ToolTipText = Settings.MasterOff ? "RGB Studio — ışıklar kapalı" : "RGB Studio — ışıklar açık";
    }

    public void RebuildTrayMenu()
    {
        if (_tray == null) return;
        var menu = new ContextMenu();

        if (AvailableUpdate is { } upd)
        {
            var item = new MenuItem { Header = $"Güncelle: {upd.Tag}", FontWeight = FontWeights.SemiBold };
            item.Click += async (_, _) =>
            {
                var err = await InstallUpdateAsync();
                if (err != null) System.Windows.MessageBox.Show(err, "RGB Studio");
            };
            menu.Items.Add(item);
            menu.Items.Add(new Separator());
        }

        var toggle = new MenuItem { Header = "Işıkları aç / kapat", FontWeight = FontWeights.SemiBold };
        toggle.Click += (_, _) => SetMasterOff(!Settings.MasterOff);
        menu.Items.Add(toggle);

        var scenes = new MenuItem { Header = "Senaryolar" };
        var list = SceneStore.LoadAll();
        if (list.Count == 0) scenes.Items.Add(new MenuItem { Header = "(kayıtlı senaryo yok)", IsEnabled = false });
        foreach (var f in list)
        {
            var item = new MenuItem { Header = f.Scene.Name };
            var captured = f.Scene;
            item.Click += (_, _) => ApplyScene(captured);
            scenes.Items.Add(item);
        }
        menu.Items.Add(scenes);

        menu.Items.Add(new Separator());
        var show = new MenuItem { Header = "RGB Studio'yu göster" };
        show.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(show);
        var exit = new MenuItem { Header = "Çıkış" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        _tray.ContextMenu = menu;
    }

    public void ShowMainWindow()
    {
        _window ??= new MainWindow();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApp()
    {
        try { SceneStore.SaveCurrent(Engine.Snapshot()); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Ikinci kopya yalnizca ilkini one getirip cikti; onun adina "kapandi" yazma.
        if (Engine == null) { base.OnExit(e); return; }
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _resumeCts?.Cancel();
        try { _tray?.Dispose(); } catch { }
        // Bir surucu cagrisi takili kalsa bile kapanis donmasin: temizligi sinirli surede dene.
        var cleanup = Task.Run(() => { try { Engine?.Dispose(); } catch { } });
        if (!cleanup.Wait(TimeSpan.FromSeconds(5))) Log.Warn("Kapanış temizliği 5 sn'de bitmedi; süreç zorla sonlandırılıyor.");
        Log.Info("RGB Studio kapandı");
        base.OnExit(e);
        if (!cleanup.IsCompleted) Environment.Exit(e.ApplicationExitCode);
    }
}
