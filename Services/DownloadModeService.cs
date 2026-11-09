using System.Runtime.InteropServices;
using System.Net.NetworkInformation;

namespace GameLocker.Services;

/// <summary>
/// 下载模式：屏幕压到最暗、整机静音，并用全屏纯黑遮罩只保留剩余下载时间 / 当前网速 / 当前时间。
/// - 进入时记住原亮度、原静音与音量，以及前台的下载页面窗口；退出或"查看下载进度"时原样恢复
/// - 下载期间用 SetThreadExecutionState 阻止系统自动睡眠，避免下载中断
/// - 计时到点后由调用方按用户选择执行睡眠 / 休眠 / 关机（PowerService）
/// 亮度（WMI）与音量（CoreAudio）都必须在后台线程调用。
/// </summary>
internal static class DownloadModeService
{
    /// <summary>是否处于下载模式（遮罩打开期间为 true）</summary>
    public static bool IsActive { get; private set; }

    /// <summary>预计结束时间</summary>
    public static DateTime EndAt { get; private set; }

    /// <summary>下载完成后要执行的电源操作</summary>
    public static PowerAction AfterAction { get; private set; } = PowerAction.Sleep;

    /// <summary>进入时设定的下载时长（分钟）</summary>
    public static int Minutes { get; private set; }

    /// <summary>剩余时间（未激活时为 0）</summary>
    public static TimeSpan Remaining => IsActive ? EndAt - DateTime.Now : TimeSpan.Zero;

    /// <summary>进入下载模式前处于前台的窗口（即用户所说的"下载页面"）</summary>
    public static IntPtr DownloadWindow { get; private set; }

    private static uint? _savedBrightness;
    private static (bool Mute, float Volume)? _savedAudio;
    private static PerformanceLevel? _savedPowerMode;
    private static bool _peeking;
    private static long _lastBytes = -1;
    private static long _lastTick;

    /// <summary>进入下载模式：录下亮度/音量与下载页面窗口，熄屏 + 静音 + 阻止睡眠 + 清内存 + 切能效（后台线程调用）</summary>
    public static void Enter(int minutes, PowerAction after)
    {
        DownloadWindow = GetForegroundWindow();
        _savedBrightness = SystemPanelService.DimScreen();
        _savedAudio = SystemPanelService.MuteOutput();

        // 下载期间没有前台交互，把各进程工作集还给系统，并临时切到最佳能效
        _savedPowerMode = PerformanceService.GetState() is { Available: true } state
            ? state.Level
            : null;
        CleanWorkingSets();
        if (_savedPowerMode is not null and not PerformanceLevel.Eco)
        {
            PerformanceService.ApplyLevel(PerformanceLevel.Eco);
            _ = PerformanceService.RefreshCacheAsync();
        }

        Minutes = Math.Clamp(minutes, 1, 24 * 60);
        EndAt = DateTime.Now.AddMinutes(Minutes);
        AfterAction = after;
        _peeking = false;
        IsActive = true;
        KeepAwake(true);

        _lastBytes = -1;      // 重新建立网速采样基线，避免把进入前的流量算进来
        SampleBytesPerSecond();
    }

    /// <summary>退出下载模式：恢复电源模式、亮度与声音，允许系统睡眠（后台线程调用）</summary>
    public static void Exit()
    {
        IsActive = false;
        _peeking = false;
        KeepAwake(false);
        if (_savedPowerMode is { } powerMode)
        {
            PerformanceService.ApplyLevel(powerMode);
            _ = PerformanceService.RefreshCacheAsync();
            _savedPowerMode = null;
        }
        if (_savedBrightness is { } brightness) SystemPanelService.RestoreScreen(brightness);
        if (_savedAudio is { } audio) SystemPanelService.RestoreOutput(audio);
        _savedBrightness = null;
        _savedAudio = null;
    }

    /// <summary>把本机各进程的工作集清空（EmptyWorkingSet），把闲置物理内存还给系统</summary>
    private static void CleanWorkingSets()
    {
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (process.HasExited) continue;
                EmptyWorkingSet(process.Handle);
            }
            catch
            {
                // 受保护/系统进程拿不到句柄，跳过
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>查看下载进度：先恢复亮度，并把进入前的前台窗口切回前台（窗口的最小化/还原由调用方做）</summary>
    public static void PeekBegin()
    {
        if (!IsActive) return;
        _peeking = true;
        if (_savedBrightness is { } brightness) SystemPanelService.RestoreScreen(brightness);
        if (DownloadWindow != IntPtr.Zero && IsWindow(DownloadWindow))
            SetForegroundWindow(DownloadWindow);
    }

    /// <summary>查看结束：重新熄屏</summary>
    public static void PeekEnd()
    {
        if (!_peeking) return;
        _peeking = false;
        if (IsActive) SystemPanelService.DimScreen();
    }

    /// <summary>采样一次网速（字节/秒）：累加所有在线网卡的接收字节增量，每秒调用一次</summary>
    public static double SampleBytesPerSecond()
    {
        long total = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try { total += ni.GetIPStatistics().BytesReceived; } catch { }
            }
        }
        catch
        {
            // 网卡枚举失败时保持 0，不打断下载模式
        }

        var now = Environment.TickCount64;
        var bps = _lastBytes < 0 ? 0 : (total - _lastBytes) * 1000.0 / Math.Max(1, now - _lastTick);
        _lastBytes = total;
        _lastTick = now;
        return Math.Max(0, bps);
    }

    /// <summary>字节/秒 → 便于阅读的网速文本</summary>
    public static string FormatSpeed(double bytesPerSecond) => bytesPerSecond >= 1048576
        ? $"{bytesPerSecond / 1048576:F1} MB/s"
        : $"{bytesPerSecond / 1024:F0} KB/s";

    // ---- P/Invoke ----

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    /// <summary>保持唤醒的专用线程：SetThreadExecutionState 是线程级的，必须由这个常驻线程持有，
    /// 否则 Task 线程结束后防护就失效了；每 15 秒重申一次，防止被电源计划覆盖</summary>
    private static CancellationTokenSource? _awakeCts;

    private static void KeepAwake(bool on)
    {
        if (on)
        {
            if (_awakeCts is not null) return;
            var cts = new CancellationTokenSource();
            _awakeCts = cts;
            var token = cts.Token;
            new Thread(() =>
            {
                const uint flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;
                try
                {
                    SetThreadExecutionState(flags);
                    while (!token.WaitHandle.WaitOne(15000))
                        SetThreadExecutionState(flags);
                }
                catch
                {
                    // 唤醒防护失败不影响下载流程
                }
                finally
                {
                    SetThreadExecutionState(ES_CONTINUOUS);
                }
            })
            {
                IsBackground = true,
                Name = "DownloadModeKeepAwake"
            }.Start();
            return;
        }

        var ctsToCancel = _awakeCts;
        _awakeCts = null;
        ctsToCancel?.Cancel();
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
