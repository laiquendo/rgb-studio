using System.Diagnostics;
using RgbStudio.Effects;

namespace RgbStudio.Core;

/// <summary>
/// Tum saglayicilari yonetir ve tek bir arka plan is parcacigindan isiklari surer.
/// - Animasyonlu efektler her cihazin MaxFps sinirina gore yenilenir.
/// - Sabit renkler yalnizca degisince yazilir, ayrica belli araliklarla tazelenir
///   (baska bir program ya da uyku sonrasi donanim isiklari degistirirse geri almak icin).
/// </summary>
public sealed class LightingEngine : IDisposable
{
    const double KeepAliveSeconds = 10;

    readonly List<ILightingProvider> _providers;
    readonly object _sync = new();
    readonly List<Thread> _pumps = new();
    int _generation;
    readonly CancellationTokenSource _cts = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<string, DeviceState> _state = new();

    List<LightDevice> _devices = new();
    Scene _scene = new();
    bool _masterOff;
    bool _paused;
    Identify? _identify;

    sealed class DeviceState
    {
        public Rgb[] Last = Array.Empty<Rgb>();
        public double LastWrite = double.NegativeInfinity;
        public double LastFull = double.NegativeInfinity;
        public int Errors;
        // Yazma suresi istatistigi (gunluge bir kez yazilir; akicilik sorunlarini teshis icin).
        public double AvgWriteMs, MaxWriteMs;
        public long Writes;
    }

    sealed record Identify(string DeviceId, int LedIndex, double Until);

    public LightingEngine(IEnumerable<ILightingProvider> providers)
    {
        _providers = providers.ToList();
        foreach (var p in _providers) p.Invalidated += () => DevicesInvalidated?.Invoke();
    }

    /// <summary>
    /// Her cihaz icin ayri bir yazma is parcacigi baslat. Cihazlar birbirini beklemez: MSI servisi
    /// gibi yavas bir cihaz bir kareyi gec yazsa bile RAM, ekran karti ve klavye kendi hizinda akar.
    /// </summary>
    void StartPumpsLocked()
    {
        int gen = ++_generation;
        _pumps.RemoveAll(t => !t.IsAlive);
        foreach (var dev in _devices)
        {
            var t = new Thread(() => Pump(dev, gen))
            {
                IsBackground = true,
                Name = "RGB " + dev.Name,
                Priority = ThreadPriority.AboveNormal,
            };
            _pumps.Add(t);
            t.Start();
        }
    }

    public event Action? DevicesChanged;

    /// <summary>Bir saglayici donanim listesinin degistigini bildirdi; yeniden tarama yapilmali (herhangi bir is parcacigindan gelir).</summary>
    public event Action? DevicesInvalidated;

    readonly Dictionary<ILightingProvider, Task<IReadOnlyList<LightDevice>>> _scans = new();

    public IReadOnlyList<ILightingProvider> Providers => _providers;

    readonly HashSet<string> _timedOut = new();

    /// <summary>Saglayicinin durum metni; son taramada yanit vermediyse bunu belirtir.</summary>
    public string StatusOf(ILightingProvider p)
    {
        lock (_timedOut)
            return _timedOut.Contains(p.Name) ? "yanıt vermedi (servis kapalı ya da meşgul)" : p.Status;
    }

    public IReadOnlyList<LightDevice> Devices { get { lock (_sync) return _devices; } }

    public Scene Scene { get { lock (_sync) return _scene; } }

    public bool MasterOff
    {
        get { lock (_sync) return _masterOff; }
        set => SetMasterOff(value);
    }

    public void SetMasterOff(bool off, bool userAction = true)
    {
        lock (_sync)
        {
            _masterOff = off;
            if (userAction) _hold = false;
            InvalidateAllLocked();
        }
    }

    bool _hold;

    /// <summary>Deneme modu: her sey hesaplanir ama donanima hicbir sey yazilmaz (--dry-run).</summary>
    public bool DryRun { get; set; }

    /// <summary>true iken donanima hic yazilmaz; kullanicinin ilk degisikliginde kendiliginden kalkar.</summary>
    public bool HoldWrites
    {
        get { lock (_sync) return _hold; }
        set { lock (_sync) { _hold = value; InvalidateAllLocked(); } }
    }

    /// <summary>Tum saglayicilarda cihazlari (yeniden) tarar. Yazmalar tarama boyunca durur.</summary>
    public async Task DetectAsync(CancellationToken ct = default)
    {
        lock (_sync) _paused = true;
        try
        {
            var tasks = _providers.Select(async p =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // Her saglayici kendi is parcaciginda: biri takilirsa digerlerini bekletmesin.
                    // Onceki (zaman asimina ugramis) taramasi hala suruyorsa ikincisini baslatma; onu bekle.
                    Task<IReadOnlyList<LightDevice>> work;
                    lock (_scans)
                    {
                        if (!_scans.TryGetValue(p, out work!) || work.IsCompleted)
                        {
                            Log.Info($"{p.Name}: tarama başladı");
                            work = Task.Run(() => p.DetectAsync(ct), ct);
                            _scans[p] = work;
                        }
                        else Log.Info($"{p.Name}: önceki tarama hâlâ sürüyor, onun sonucu bekleniyor");
                    }
                    var budget = p.DetectTimeout;
                    var done = await Task.WhenAny(work, Task.Delay(budget, ct));
                    lock (_timedOut) _timedOut.Remove(p.Name);
                    if (done != work)
                    {
                        lock (_timedOut) _timedOut.Add(p.Name);
                        Log.Warn($"{p.Name}: tarama {budget.TotalSeconds:0} sn içinde bitmedi, atlandı");
                        // Gec biterse sonucu kaybetme: yeniden tarama iste.
                        _ = work.ContinueWith(t => { if (t.IsCompletedSuccessfully && t.Result.Count > 0) DevicesInvalidated?.Invoke(); },
                            TaskScheduler.Default);
                        return (IReadOnlyList<LightDevice>)Array.Empty<LightDevice>();
                    }
                    var list = await work;
                    Log.Info($"{p.Name}: {list.Count} cihaz, {sw.ElapsedMilliseconds} ms ({p.Status})");
                    return list;
                }
                catch (Exception ex)
                {
                    Log.Error($"{p.Name} taraması başarısız", ex);
                    return (IReadOnlyList<LightDevice>)Array.Empty<LightDevice>();
                }
            }).ToList();
            var results = await Task.WhenAll(tasks);
            var all = results.SelectMany(r => r).ToList();
            lock (_sync)
            {
                _devices = all;
                _state.Clear();
                foreach (var d in all) _scene.For(d); // yeni cihazlar icin varsayilan ayar olustur
                StartPumpsLocked(); // eski cihazlarin is parcaciklari nesil degisince kendiliginden biter
            }
            Log.Info($"Tarama: {all.Count} cihaz — " + string.Join(", ", all.Select(d => $"{d.Name}[{d.LedCount}]")));
        }
        finally
        {
            lock (_sync) _paused = false;
        }
        DevicesChanged?.Invoke();
    }

    /// <summary>Etkin senaryoyu degistirir (kopyasi alinir).</summary>
    public void LoadScene(Scene scene, bool userAction = true)
    {
        lock (_sync)
        {
            if (userAction) _hold = false;
            _scene = scene.Clone();
            foreach (var d in _devices) _scene.For(d);
            InvalidateAllLocked();
        }
    }

    /// <summary>Etkin senaryoyu kilit altinda degistirir (arayuz bu yolla duzenler; render ile yarismaz).</summary>
    public void Edit(Action<Scene> edit)
    {
        lock (_sync)
        {
            _hold = false;
            edit(_scene);
            _scene.Modified = DateTime.Now;
            InvalidateAllLocked();
        }
        SceneEdited?.Invoke();
    }

    /// <summary>Etkin senaryodan kilit altinda okur.</summary>
    public T Read<T>(Func<Scene, T> read)
    {
        lock (_sync) return read(_scene);
    }

    /// <summary>Etkin senaryonun kopyasi (kaydetmek icin).</summary>
    public Scene Snapshot()
    {
        lock (_sync) return _scene.Clone();
    }

    public event Action? SceneEdited;

    /// <summary>Senaryo nesnesi disaridan (arayuzden) degistirildiginde cagrilir.</summary>
    public void Invalidate(string? deviceId = null)
    {
        lock (_sync)
        {
            if (deviceId == null) InvalidateAllLocked();
            else if (_state.TryGetValue(deviceId, out var st)) st.Last = Array.Empty<Rgb>();
        }
    }

    /// <summary>Uyanma sonrasi gibi durumlarda tum cihazlara mod dahil tam yazim yaptirir.</summary>
    public void ForceFullWrite()
    {
        lock (_sync)
        {
            foreach (var d in _devices) d.NeedsFullWrite = true;
            InvalidateAllLocked();
        }
    }

    /// <summary>Bir cihazi (ya da tek LED'ini) birkac saniye beyaz yakip sondurerek gosterir.</summary>
    public void IdentifyDevice(string deviceId, int ledIndex = -1, double seconds = 3)
    {
        lock (_sync)
        {
            _identify = new Identify(deviceId, ledIndex, _clock.Elapsed.TotalSeconds + seconds);
            // Kullanici eylemi: bekletmeyi kaldir; yoksa "Bul" bitince cihazlar son beyaz/siyah karede kalirdi.
            _hold = false;
            InvalidateAllLocked();
        }
    }

    void InvalidateAllLocked()
    {
        foreach (var st in _state.Values) st.Last = Array.Empty<Rgb>();
    }

    /// <summary>Tek bir cihazin yazma dongusu; cihaz listesi degisince (yeni nesil) ya da kapanista biter.</summary>
    void Pump(LightDevice dev, int gen)
    {
        var token = _cts.Token;
        var buffer = new Rgb[Math.Max(dev.LedCount, 1)];
        using var timer = new PreciseWait();
        while (!token.IsCancellationRequested)
        {
            double now = _clock.Elapsed.TotalSeconds;
            Scene scene;
            bool masterOff, skip;
            Identify? identify;
            lock (_sync)
            {
                if (gen != _generation) return;
                skip = _paused || (_hold && _identify == null);
                scene = _scene;
                masterOff = _masterOff;
                if (_identify != null && now > _identify.Until) { _identify = null; InvalidateAllLocked(); }
                identify = _identify;
            }

            double sleep = 0.05;
            if (!skip)
            {
                try { sleep = Tick(dev, scene, masterOff, identify, now, ref buffer); }
                catch (Exception ex) { Log.Error($"Render hatası: {dev}", ex); sleep = 0.5; }
            }
            // Bir sonraki kareye kadar bekle; ayar degisikliklerini kacirmamak icin en fazla 50 ms.
            double ms = Math.Clamp(sleep * 1000, 1, 50);
            if (timer.Wait(TimeSpan.FromMilliseconds(ms), token.WaitHandle)) return;
        }
    }

    /// <summary>Kareyi hesaplar, gerekiyorsa yazar; bir sonraki denemeye kadar beklenecek sureyi (sn) dondurur.</summary>
    double Tick(LightDevice dev, Scene scene, bool masterOff, Identify? identify, double now, ref Rgb[] buffer)
    {
        DeviceState st;
        DeviceScene ds;
        lock (_sync)
        {
            if (!_state.TryGetValue(dev.Id, out st!)) _state[dev.Id] = st = new DeviceState();
            ds = scene.For(dev);
        }

        int n = Math.Max(dev.LedCount, 1);
        if (buffer.Length < n) buffer = new Rgb[n];
        var frame = buffer.AsSpan(0, n);

        if (identify != null)
        {
            // Bul: hedef cihaz/LED yanip soner, digerleri kapali.
            bool on = (int)(now * 4) % 2 == 0;
            frame.Fill(Rgb.Black);
            if (identify.DeviceId == dev.Id)
            {
                if (identify.LedIndex >= 0 && identify.LedIndex < n) frame[identify.LedIndex] = on ? Rgb.White : Rgb.Black;
                else frame.Fill(on ? Rgb.White : Rgb.Black);
            }
        }
        else if (masterOff)
        {
            frame.Fill(Rgb.Black);
        }
        else
        {
            lock (_sync) EffectRenderer.Render(ds, now, frame);
        }

        // Tek renkli (bolgesiz) cihazlarda ortalama yerine ilk LED'i kullan.
        if (!dev.PerLed && n > 1) frame[1..].Fill(frame[0]);

        double minInterval = 1.0 / Math.Max(dev.MaxFps, 1);
        if (now - st.LastWrite < minInterval) return minInterval - (now - st.LastWrite);

        // Hata veren cihazi yavaslat (saniyede bir dene).
        if (st.Errors > 3 && now - st.LastWrite < 1.0) return 1.0 - (now - st.LastWrite);

        // Mod/stil yalnizca gerektiginde yeniden ayarlanir (ilk yazim, uyanma, cihaz kontrolu geri aldiginda).
        // Periyodik tazeleme yalnizca renkleri yeniden gonderir: modu her 10 sn'de bir yeniden yazmak
        // RAM SMBus'ini gereksiz mesgul eder ve MSI'da gozle gorulur kirpismaya yol acar.
        bool modeWrite = dev.NeedsFullWrite;
        bool refresh = now - st.LastFull >= KeepAliveSeconds;
        bool changed = st.Last.Length != n || !frame.SequenceEqual(st.Last);
        if (!changed && !modeWrite && !refresh) return minInterval;

        st.LastWrite = now;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            if (!DryRun) dev.Provider.Write(dev, frame, modeWrite);
        }
        catch (Exception ex)
        {
            // Basarisiz yazimi "yazildi" sayma: bir sonraki uygun karede (hata sayisina gore yavaslayarak) tekrar dene.
            if (st.Errors++ == 0) Log.Error($"Yazma hatası: {dev}", ex);
            st.Last = Array.Empty<Rgb>();
            return minInterval;
        }
        double took = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        RecordWrite(dev, st, took, minInterval);
        if (st.Errors > 0) Log.Info($"Yazma yeniden çalışıyor: {dev}");
        st.Errors = 0;
        st.LastFull = now;
        if (modeWrite) dev.NeedsFullWrite = false;
        st.Last = frame.ToArray();
        return minInterval - took / 1000.0;
    }

    static void RecordWrite(LightDevice dev, DeviceState st, double ms, double minInterval)
    {
        st.Writes++;
        st.AvgWriteMs = st.Writes == 1 ? ms : st.AvgWriteMs * 0.95 + ms * 0.05;
        st.MaxWriteMs = Math.Max(st.MaxWriteMs, ms);
        if (st.Writes == 150)
        {
            double fps = 1000.0 / Math.Max(st.AvgWriteMs, 1000.0 * minInterval);
            Log.Info($"Yazma süresi: {dev.Name}: ort. {st.AvgWriteMs:0.0} ms, en çok {st.MaxWriteMs:0} ms → ~{fps:0} kare/sn (hedef {dev.MaxFps})");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Thread[] pumps;
        lock (_sync) pumps = _pumps.ToArray();
        var deadline = Stopwatch.StartNew();
        foreach (var t in pumps)
            t.Join(TimeSpan.FromMilliseconds(Math.Max(0, 1000 - deadline.ElapsedMilliseconds)));
        foreach (var p in _providers)
        {
            try { p.Dispose(); } catch { }
        }
    }
}
