using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RgbStudio.Core;

/// <summary>
/// Milisaniye hassasiyetinde bekleme. Windows'un varsayilan zamanlayicisi ~15,6 ms adimlarla calisir;
/// bu, 30 kare/sn'lik bir animasyonu 21 kareye, 15'lik birini 12'ye dusurur ve gecisler takilir gorunur.
/// Yuksek cozunurluklu bekletilebilir zamanlayici (Windows 10 1803+) sistem geneli ayari degistirmeden bunu cozer.
/// </summary>
public sealed class PreciseWait : IDisposable
{
    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    const uint TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    readonly SafeWaitHandle? _timer;
    readonly WaitHandle? _handle;

    public PreciseWait()
    {
        try
        {
            var h = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (!h.IsInvalid) { _timer = h; _handle = new TimerHandle(h); }
        }
        catch { /* eski Windows: normal beklemeye dus */ }
    }

    sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle h) { SafeWaitHandle = h; }
    }

    /// <summary>Verilen sure kadar bekler; iptal sinyali gelirse true dondurur.</summary>
    public bool Wait(TimeSpan duration, WaitHandle cancel)
    {
        if (duration <= TimeSpan.Zero) return cancel.WaitOne(0);
        if (_timer == null || _handle == null) return cancel.WaitOne(duration);
        long due = -duration.Ticks; // negatif = goreli, 100 ns biriminde
        if (!SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) return cancel.WaitOne(duration);
        return WaitHandle.WaitAny(new[] { cancel, _handle }) == 0;
    }

    public void Dispose()
    {
        _handle?.Dispose();
    }
}
