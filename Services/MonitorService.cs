using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>一次采样得到的监控数据（NaN / 0 表示该项不可用，界面显示"—"）</summary>
public sealed class MonitorSnapshot
{
    /// <summary>帧率（0=不可用）</summary>
    public double Fps;
    /// <summary>有明确原生帧标记时的 FPS；NaN 表示无法识别。</summary>
    public double OriginalFps = double.NaN;
    /// <summary>观察到生成帧或活动的外部缩放输出，不能再把提交率当作输出率。</summary>
    public bool FrameGenerationDetected;
    /// <summary>经过呈现链路确认的输出 FPS；尚无可靠来源时为 NaN。</summary>
    public double OutputFps = double.NaN;
    /// <summary>平均帧时间（毫秒，0=不可用）</summary>
    public double FrameMs;
    /// <summary>帧时间序列（毫秒，旧→新），用于绘制帧波动线</summary>
    public double[] FrameHistory = [];
    /// <summary>系统 CPU 总占用率（%）</summary>
    public double CpuUsage = double.NaN;
    /// <summary>CPU 当前平均频率（MHz，NaN=不可用）</summary>
    public double CpuMhz = double.NaN;
    /// <summary>CPU 温度（℃，NaN=不可用）</summary>
    public double CpuTemp = double.NaN;
    /// <summary>CPU 封装功耗（W，NaN=不可用；来自 RAPL 性能计数器）</summary>
    public double CpuPower = double.NaN;
    /// <summary>所选 GPU 最忙物理引擎占用率（%，NaN=不可用）</summary>
    public double GpuUsage = double.NaN;
    /// <summary>GPU 频率（MHz，NaN=不可用；来自 NVML，无 NVIDIA 显卡时为 NaN）</summary>
    public double GpuMhz = double.NaN;
    /// <summary>GPU 功耗（W，NaN=不可用；来自 NVML）</summary>
    public double GpuPower = double.NaN;
    /// <summary>GPU 温度（℃，NaN=不可用；来自 NVML / ADL / IGCL）</summary>
    public double GpuTemp = double.NaN;
    /// <summary>所选 GPU 显存用量（GB；Intel 核显含共享内存，NaN=不可用，0 为有效读数）</summary>
    public double GpuVram = double.NaN;
    /// <summary>电池当前电量（%，NaN=不可用）</summary>
    public double BatteryPercent = double.NaN;
    /// <summary>电池功率（W，充电时为充电功率、放电时为放电功率，NaN=不可用）</summary>
    public double BatteryPower = double.NaN;
    /// <summary>预计可用/充满时间（分钟，NaN=不可用）</summary>
    public double BatteryMinutes = double.NaN;
    /// <summary>是否正在充电</summary>
    public bool Charging;
    /// <summary>是否已接外接电源</summary>
    public bool OnAc;
    /// <summary>物理内存占用率（%）</summary>
    public double MemPercent = double.NaN;
}

/// <summary>
/// 性能监控：前台进程帧率（ETW/DXGI Present）+ 系统 CPU/GPU/电池/内存采样，
/// 驱动一个置顶、点击穿透的 Win32 悬浮条（屏幕上方居中）。全部数据来自 Windows 接口。
/// 帧率依赖 ETW 实时会话，必须管理员权限：本进程已提权则本地采集，
/// 否则以 runas 拉起 <see cref="MonitorHelperHost"/> 采集，其余指标始终本地采样。
/// </summary>
public static class MonitorService
{
    private static readonly object Gate = new();
    private static MonitorSnapshot _snapshot = new();
    private static Timer? _timer;
    private static MonitorOverlayWindow? _overlay;
    private static PresentEtwListener? _etw;
    private static LosslessOutputListener? _losslessOutput;
    private static int _frameTargetPid;
    private static int _lastOutputPid;
    private static double _lastOutputFps = double.NaN;
    private static long _lastOutputTick;
    private static bool _lastOutputGenerated;
    private static int _ticking;
    private static int _tickCount;
    /// <summary>上次刷新睿频缓存的时间（悬浮条勾选"CPU 睿频"时每 5 秒刷一次 powercfg）</summary>
    private static long _lastCpuSettingsRefresh;

    /// <summary>主窗口当前是否处于前台（Activated 且非 Deactivated）。MainWindow 在 Activated/Deactivated 里驱动。
    /// 后台时降低采样/重绘频率以省资源，但 ETW 帧率会话保持全速（后台玩游戏仍要显示真实帧数据）</summary>
    private static int _appIsForeground = 1;
    private static int _backgroundTick;

    /// <summary>主窗口切换前台/后台；由 MainWindow 在 Activated/Deactivated 时调用</summary>
    public static void SetAppForeground(bool foreground)
    {
        var prev = Interlocked.Exchange(ref _appIsForeground, foreground ? 1 : 0);
        if (prev == (foreground ? 1 : 0)) return;
        if (foreground)
        {
            _backgroundTick = 0;
            // 重新切到前台立刻全功率采样一次，避免回来后看到的还是后台低频的旧数据
            _timer?.Change(200, 1000);
        }
    }

    private static ulong _lastIdle, _lastKernel, _lastUser;

    private static double _gpuUsage = double.NaN;
    private static double _gpuVram = double.NaN;
    private static double _cpuTemp = double.NaN;
    /// <summary>提权助手通过 PawnIO 读到的 CPU 封装温度（NaN=不可用）</summary>
    private static double _helperCpuTemp = double.NaN;
    private static double _helperGpuTemp = double.NaN;
    private static string? _gpuAdapterKey;
    private static bool _gpuSensorsAligned;
    private static double _cpuPower = double.NaN;
    private static double _cpuMhzFallback;
    private static double _acpiCpuTemp = double.NaN;
    private static long _acpiCpuTempTick;
    private static bool _statusHelperTried;

    private static (double Discharge, double Charge) _batteryRate = (double.NaN, double.NaN);

    // 提权助手通道
    private static MonitorShared? _shared;
    private static Process? _helper;
    private static EventWaitHandle? _stopEvent;
    private static bool _helperAlive;

    /// <summary>监控是否处于开启状态</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>帧率是否可用（本地 ETW 成功，或提权助手已启动）</summary>
    public static bool FpsAvailable => _etw is { Available: true } || _helperAlive;

    /// <summary>最近一次开启时的提示（提权被取消、暂无数据等），无异常时为空串</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>开关状态变化（UI 线程订阅，用于刷新磁贴文字）</summary>
    public static event Action? StateChanged;

    /// <summary>最近一次采样快照（供磁贴显示摘要）</summary>
    public static MonitorSnapshot Current
    {
        get { lock (Gate) return _snapshot; }
    }

    /// <summary>性能面板独立读取设备状态；与监控采样互斥。</summary>
    public static MonitorSnapshot? ReadDeviceStatus()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return null;
        try
        {
            var s = new MonitorSnapshot { CpuPower = ReadCpuPower(), CpuMhz = ReadCpuMhz() };
            if (!double.IsFinite(s.CpuMhz)) s.CpuMhz = ReadCpuMhzWmi();
            _batteryRate = ReadBatteryRate();
            ReadBattery(s);
            if (IsElevated())
            {
                PawnIoMsr.EnsureReady();
                s.CpuTemp = PawnIoMsr.ReadTemperature();
            }
            else
            {
                // 和性能监控同源：未提权时用提权助手回传的 MSR 温度；首次进面板时补拉起助手
                EnsureStatusHelper();
                if (_shared is not null && TryReadHelperTemps())
                {
                    s.CpuTemp = _helperCpuTemp;
                    if (IsRunning) s.CpuTemp = Current.CpuTemp;
                }
                else if (IsRunning)
                {
                    s.CpuTemp = Current.CpuTemp;
                }
            }
            // 仍拿不到（未授权/助手未就绪）时退回 ACPI 热区，至少给出可参考的 CPU 温度
            if (!double.IsFinite(s.CpuTemp)) s.CpuTemp = ReadAcpiCpuTemp();
            ReadGpuUsage(); // 先确认适配器身份，再读对应厂商传感器。
            s.GpuTemp = ReadGpuTemp();
            s.GpuPower = ReadGpuPower();
            s.GpuMhz = ReadGpuClock();
            return s;
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>性能面板首次需要真实温度时，若未提权且助手未运行，拉起一次提权助手（与监控同一通道）</summary>
    private static void EnsureStatusHelper()
    {
        if (_statusHelperTried) return;
        _statusHelperTried = true;
        if (IsElevated() || _helper is not null || _shared is not null) return;
        StartHelper();
    }

    /// <summary>更新助手回传的 CPU/GPU 温度，返回是否读到有效通道数据</summary>
    private static bool TryReadHelperTemps()
    {
        if (_shared is null) return false;
        _shared.SetTargetPid(ForegroundPid());
        _shared.SetOutputTargetPid(0);
        if (!_shared.TryRead(out _, out _, out _, out var helperTemp, out var helperGpuTemp, out _, out _))
        {
            _helperAlive = false;
            _helperCpuTemp = _helperGpuTemp = double.NaN;
            return false;
        }
        _helperAlive = true;
        _helperCpuTemp = helperTemp is > 0 and < 130 ? helperTemp : double.NaN;
        _helperGpuTemp = helperGpuTemp is > 0 and < 130 ? helperGpuTemp : double.NaN;
        return double.IsFinite(_helperCpuTemp);
    }

    /// <summary>ACPI 热区温度（℃）：无管理员权限时 CPU 温度的兜底来源，缓存 3 秒</summary>
    private static double ReadAcpiCpuTemp()
    {
        var now = Environment.TickCount64;
        if (now - _acpiCpuTempTick < 3000) return _acpiCpuTemp;
        _acpiCpuTempTick = now;
        var result = double.NaN;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            foreach (var o in searcher.Get())
            {
                if (o["Temperature"] is not { } v) continue;
                var celsius = Convert.ToDouble(v) - 273.15;
                if (celsius is < 20 or > 120) continue;
                var name = o["Name"]?.ToString() ?? "";
                // 优先 CPU 热区（THRM/TZ00），其余热区只在没有更好来源时保留第一个合理值
                if (name.Contains("THRM", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("TZ00", StringComparison.OrdinalIgnoreCase))
                {
                    result = celsius;
                    break;
                }
                if (!double.IsFinite(result)) result = celsius;
            }
        }
        catch { }
        _acpiCpuTemp = result;
        return result;
    }

    /// <summary>A/点击：开启或关闭监控（必须在 UI 线程调用）</summary>
    public static void Toggle()
    {
        if (IsRunning) Stop();
        else Start();
    }

    /// <summary>开启监控：创建悬浮条 + 启动帧率采集与采样定时器</summary>
    public static void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _tickCount = 0;
        _frameTargetPid = _lastOutputPid = 0;
        _lastOutputFps = double.NaN;
        _lastOutputTick = 0;
        _lastOutputGenerated = false;
        _lastIdle = _lastKernel = _lastUser = 0;
        LastError = "";
        _gpuUsage = _gpuVram = _cpuTemp = _cpuPower = _cpuMhzFallback = double.NaN;
        _helperCpuTemp = _helperGpuTemp = double.NaN;
        _helperAlive = false;
        _gpuAdapterKey = null;
        _gpuSensorsAligned = false;
        _batteryRate = (double.NaN, double.NaN);
        _backgroundTick = 0;
        if (_kmtAdapter == 0) _kmtTries = 0;
        lock (Gate) _snapshot = new MonitorSnapshot();

        var item = FindMonitorItem();
        _metrics = NormalizeMetrics(item?.MonitorMetrics);
        // CPU 温度也需要初始化 MSR 或提权助手，不显示帧率时不启动本地 ETW。
        if (NeedsFrameStats() || Want(MonitorMetric.CpuTemp | MonitorMetric.GpuTemp)) StartFrameSource();

        try
        {
            _overlay = new MonitorOverlayWindow(
                item?.MonitorPos ?? MonitorPosition.TopCenter,
                item?.MonitorScale ?? 1.0,
                _metrics,
                item?.MonitorColor ?? MonitorColorTheme.Default,
                item?.MonitorAlwaysOn ?? false);
            if (!_overlay.Create())
            {
                _overlay.Dispose();
                _overlay = null;
            }
        }
        catch { _overlay = null; }

        _timer = new Timer(Tick, null, 250, 1000);
        StateChanged?.Invoke();
    }

    /// <summary>当前勾选显示的指标（由监控磁贴设置驱动，用于跳过未显示的采集）</summary>
    private static MonitorMetric _metrics = MonitorMetric.All;

    private static bool Want(MonitorMetric m) => (_metrics & m) != 0;

    private static MonitorMetric NormalizeMetrics(MonitorMetric? m)
    {
        var v = m ?? MonitorMetric.All;
        if ((v & MonitorMetric.OriginalFps) != 0)
            v = (v & ~MonitorMetric.OriginalFps) | MonitorMetric.Fps;
        return v == MonitorMetric.None ? MonitorMetric.All : v;
    }

    private static bool NeedsFrameStats() =>
        Want(MonitorMetric.Fps | MonitorMetric.FrameTime | MonitorMetric.FrameGraph);

    /// <summary>按需启动帧率来源：已提权 → 本地 ETW；未提权 → 提权助手（UAC 取消则降级为无帧率）</summary>
    private static void StartFrameSource()
    {
        var elevated = IsElevated();
        if (elevated && Want(MonitorMetric.CpuTemp))
            _ = Task.Run(() => PawnIoMsr.EnsureReady(installIfMissing: true));
        if (_etw is not null || _shared is not null || _helper is not null) return;
        if (elevated)
        {
            if (!NeedsFrameStats()) return;
            try
            {
                _etw = new PresentEtwListener();
                if (!_etw.Start())
                {
                    LastError = $"帧率会话启动失败（{_etw.StartError}）";
                    _etw.Dispose();
                    _etw = null;
                }
            }
            catch (Exception ex)
            {
                LastError = "帧率初始化失败：" + ex.Message;
                _etw = null;
            }
        }
        else
        {
            StartHelper();
        }
    }

    /// <summary>关闭监控：停止助手 + 销毁悬浮条 + 停止 ETW 会话（必须在 UI 线程调用）</summary>
    public static void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _timer?.Dispose();
        _timer = null;
        _etw?.Dispose();
        _etw = null;
        _losslessOutput?.Dispose();
        _losslessOutput = null;
        _frameTargetPid = _lastOutputPid = 0;
        _lastOutputFps = double.NaN;
        _lastOutputTick = 0;
        _lastOutputGenerated = false;

        try { _stopEvent?.Set(); } catch { }

        var helper = _helper;
        _helper = null;
        _helperAlive = false;
        if (helper is not null)
        {
            try
            {
                if (!helper.WaitForExit(1500)) helper.Kill();
            }
            catch { }
            helper.Dispose();
        }
        _stopEvent?.Dispose();
        _stopEvent = null;
        _shared?.Dispose();
        _shared = null;

        _overlay?.Dispose();
        _overlay = null;

        lock (Gate) _snapshot = new MonitorSnapshot();
        StateChanged?.Invoke();
    }

    /// <summary>把监控磁贴里保存的悬浮条设置（位置 / 大小 / 配色 / 常开）实时应用；未开启时忽略</summary>
    public static void ApplySettings()
    {
        if (!IsRunning) return;
        var item = FindMonitorItem();
        _metrics = NormalizeMetrics(item?.MonitorMetrics);
        // 运行中才勾上帧率指标：按需补开 ETW；全部取消则关掉本地 ETW（助手仍保留温度采集）
        if (NeedsFrameStats() || Want(MonitorMetric.CpuTemp | MonitorMetric.GpuTemp)) StartFrameSource();
        if (!NeedsFrameStats())
        {
            _etw?.Dispose();
            _etw = null;
        }
        _overlay?.ApplySettings(
            item?.MonitorPos ?? MonitorPosition.TopCenter,
            item?.MonitorScale ?? 1.0,
            _metrics,
            item?.MonitorColor ?? MonitorColorTheme.Default,
            item?.MonitorAlwaysOn ?? false);
    }

    /// <summary>从配置里找性能监控磁贴（可能被放进抽屉，故连同抽屉一起找）</summary>
    private static LaunchItem? FindMonitorItem()
    {
        try
        {
            return DrawerService.FindAnywhere(ConfigService.Load(),
                i => i.Kind == LaunchKind.Widget && i.WidgetKind == WidgetKind.Monitor);
        }
        catch { return null; }
    }

    /// <summary>拉起提权助手采集帧率；失败/取消 UAC 时降级为不显示帧率</summary>
    private static void StartHelper()
    {
        _shared = MonitorShared.CreateWriter();
        if (_shared is null)
        {
            LastError = "帧率需要管理员权限";
            return;
        }

        try
        {
            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, MonitorShared.StopEventName);
        }
        catch { _stopEvent = null; }

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                LastError = "帧率需要管理员权限";
                return;
            }
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--monitor-helper {Environment.ProcessId}",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            _helper = Process.Start(psi);
            _helperAlive = _helper is not null;
            if (!_helperAlive) LastError = "帧率需要管理员权限";
        }
        catch (Win32Exception ex)
        {
            _helper = null;
            _helperAlive = false;
            LastError = ex.NativeErrorCode == 1223
                ? "已取消管理员授权，帧率不可用"
                : "监控助手启动失败：" + ex.Message;
        }
        catch (Exception ex)
        {
            _helper = null;
            _helperAlive = false;
            LastError = "监控助手启动失败：" + ex.Message;
        }
    }

    private static void Tick(object? state)
    {
        if (!IsRunning) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            // 悬浮条显示策略：主窗口前台时永远显示；主窗口后台时只有全屏游戏才显示（避免白白消耗 GPU）；
            // 用户勾选"常开"时后台也一直显示并保持 1 秒刷新
            var appFg = Volatile.Read(ref _appIsForeground) == 1;
            var fullscreen = FullscreenDetector.IsForegroundFullscreen();
            var alwaysOn = _overlay?.AlwaysOn == true;
            _overlay?.SetForeground(appFg, fullscreen);

            if (!appFg && !fullscreen && !alwaysOn)
            {
                // 后台每 5 秒采样并重绘一次（Tick 每 1s 到，所以隔 5 轮真正采样一次）
                var bg = Interlocked.Increment(ref _backgroundTick);
                if (bg % 5 != 0) return;
            }
            else
            {
                _backgroundTick = 0;
            }

            var snapshot = Sample();
            lock (Gate) _snapshot = snapshot;
            if (_tickCount == 1) _ = Task.Run(WriteGpuDiagnostics);

            _overlay?.Update(snapshot);
            // 睿频状态来自 powercfg，悬浮条开启该项时低频刷新缓存（不阻塞采样线程）
            if (_overlay?.NeedsCpuSettings == true
                && Environment.TickCount64 - Interlocked.Read(ref _lastCpuSettingsRefresh) >= 5000)
            {
                Interlocked.Exchange(ref _lastCpuSettingsRefresh, Environment.TickCount64);
                _ = PerformanceService.RefreshCpuCacheAsync();
            }
            if (fullscreen || (alwaysOn && !appFg)) _overlay?.ReassertTopmost();
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private static MonitorSnapshot Sample()
    {
        var n = ++_tickCount;
        var s = new MonitorSnapshot();
        var targetPid = NeedsFrameStats() ? ForegroundPid() : 0;
        var losslessPid = NeedsFrameStats() ? LosslessScalingService.ActiveOutputPid() : 0;
        var outputPid = losslessPid > 0 ? losslessPid : targetPid;
        s.FrameGenerationDetected = losslessPid > 0;
        if (outputPid > 0 && IsElevated() && _losslessOutput is null)
        {
            _losslessOutput = new LosslessOutputListener();
            _losslessOutput.Start();
        }
        var frameTargetChanged = _frameTargetPid != targetPid;
        _frameTargetPid = targetPid;
        _losslessOutput?.SetTargets(targetPid, outputPid);
        // 助手温度必须先读取，失效时不能继续使用上次的温度。
        if (_shared is not null)
        {
            _shared.SetTargetPid(targetPid);
            _shared.SetOutputTargetPid(outputPid);
            if (_shared.TryRead(out var fps, out var frameMs, out var history, out var helperTemp, out var helperGpuTemp, out var originalFps, out var frameGenerationDetected))
            {
                _helperAlive = true;
                s.Fps = fps;
                s.FrameMs = frameMs;
                s.FrameHistory = history;
                s.OriginalFps = originalFps;
                s.FrameGenerationDetected |= frameGenerationDetected;
                _helperGpuTemp = helperGpuTemp is > 0 and < 130 ? helperGpuTemp : double.NaN;
                _helperCpuTemp = helperTemp is > 0 and < 130 ? helperTemp : double.NaN;
                if (double.IsNaN(_helperCpuTemp)) _cpuTemp = double.NaN;
            }
            else
            {
                _helperAlive = false;
                _helperCpuTemp = _helperGpuTemp = _cpuTemp = double.NaN;
            }
        }

        // 身份发现先于显存及厂商传感器；仅勾选传感器时也需要发现。
        if (n == 1 || n % 2 == 0)
        {
            if (Want(MonitorMetric.GpuUsage | MonitorMetric.GpuVram | MonitorMetric.GpuFreq
                | MonitorMetric.GpuPower | MonitorMetric.GpuTemp)) _gpuUsage = ReadGpuUsage();
            if (Want(MonitorMetric.GpuVram)) _gpuVram = ReadGpuVram();
            if (Want(MonitorMetric.Battery)) _batteryRate = ReadBatteryRate();
        }
        var wantCpuUsage = Want(MonitorMetric.CpuUsage);
        s.CpuUsage = wantCpuUsage ? ReadCpuUsage() : double.NaN;
        s.CpuMhz = Want(MonitorMetric.CpuFreq) ? ReadCpuMhz() : double.NaN;
        s.MemPercent = Want(MonitorMetric.Memory) ? ReadMemoryPercent() : double.NaN;
        s.GpuMhz = Want(MonitorMetric.GpuFreq) ? ReadGpuClock() : double.NaN;
        s.GpuPower = Want(MonitorMetric.GpuPower) ? ReadGpuPower() : double.NaN;
        s.GpuTemp = Want(MonitorMetric.GpuTemp) ? ReadGpuTemp() : double.NaN;

        if (n == 1 || n % 5 == 0)
        {
            if (Want(MonitorMetric.CpuTemp))
            {
                // 优先用 PawnIO/MSR 的真实封装温度：本进程已提权直接读，否则用提权助手回传的值
                var msrTemp = IsElevated() ? PawnIoMsr.ReadTemperature() : _helperCpuTemp;
                _cpuTemp = msrTemp is > 0 and < 130 ? Math.Round(msrTemp * 10) / 10 : double.NaN;
            }
            if (Want(MonitorMetric.CpuPower)) _cpuPower = ReadCpuPower();
            if (Want(MonitorMetric.CpuFreq)) _cpuMhzFallback = ReadCpuMhzWmi();
        }
        s.GpuUsage = _gpuUsage;
        s.GpuVram = _gpuVram;
        s.CpuTemp = _cpuTemp;
        s.CpuPower = _cpuPower;
        if (Want(MonitorMetric.CpuFreq) && (double.IsNaN(s.CpuMhz) || s.CpuMhz <= 0))
            s.CpuMhz = _cpuMhzFallback;

        if (Want(MonitorMetric.Battery) || Want(MonitorMetric.BatteryPercent)) ReadBattery(s);

        // 帧率：本地 ETW 优先，否则读提权助手发布的共享内存
        if (_etw is { Available: true })
        {
            _etw.TargetPid = targetPid;
            var (fps, frameMs, history) = _etw.GetStats();
            s.Fps = fps;
            s.FrameMs = frameMs;
            s.FrameHistory = history;
            s.OriginalFps = _etw.GetOriginalFps();
            s.FrameGenerationDetected |= _etw.FrameGenerationDetected;
        }

        s.FrameGenerationDetected |= _losslessOutput?.HasGeneratedFrames(targetPid) ?? false;
        if (outputPid > 0 && !frameTargetChanged)
        {
            // 游戏允许合成显示；小黄鸭仍只读取独立显示链，不按提交率或倍率推算。
            s.OutputFps = _losslessOutput?.GetFps(outputPid)
                ?? _shared?.ReadOutputFps(outputPid) ?? double.NaN;
        }
        var outputTick = Environment.TickCount64;
        if (frameTargetChanged || _lastOutputPid != outputPid || outputPid <= 0)
        {
            _lastOutputPid = outputPid;
            _lastOutputFps = double.NaN;
            _lastOutputTick = 0;
            _lastOutputGenerated = false;
        }
        if (double.IsFinite(s.OutputFps) && s.OutputFps > 0)
        {
            _lastOutputFps = s.OutputFps;
            _lastOutputTick = outputTick;
            _lastOutputGenerated = s.FrameGenerationDetected;
        }
        else if (outputPid > 0 && !frameTargetChanged && outputTick - _lastOutputTick <= 5000
            && double.IsFinite(_lastOutputFps))
        {
            // 仅跨越短暂采集空窗；不能跨进程沿用，也不能用缓存续期。
            s.OutputFps = _lastOutputFps;
            s.FrameGenerationDetected |= _lastOutputGenerated;
        }
        if (losslessPid > 0)
        {
            // 外部插帧输入是游戏的 ETW 原始帧率；内置插帧则保留 ETW 的原始帧分类结果。
            s.OriginalFps = targetPid != losslessPid && s.Fps > 0 ? s.Fps : double.NaN;
        }
        return s;
    }

    // ---- 前台进程 ----

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static int ForegroundPid()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out var pid);
            var v = (int)pid;
            // 启动器自身/桌面不算游戏
            if (v <= 4 || v == Environment.ProcessId) return 0;
            return v;
        }
        catch { return 0; }
    }

    // ---- 当前进程是否已提权 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out TOKEN_ELEVATION info,
        int infoLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>当前进程是否以管理员身份运行（ETW 帧率的前提）</summary>
    private static bool IsElevated()
    {
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out token)) return false;
            if (!GetTokenInformation(token, 20 /* TokenElevation */, out var info,
                    Marshal.SizeOf<TOKEN_ELEVATION>(), out _)) return false;
            return info.TokenIsElevated != 0;
        }
        catch { return false; }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    // ---- CPU 占用 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    private static ulong ToValue(FILETIME t) => ((ulong)t.High << 32) | t.Low;

    private static double ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return double.NaN;
        var i = ToValue(idle);
        var k = ToValue(kernel);
        var u = ToValue(user);
        if (_lastKernel == 0)
        {
            _lastIdle = i; _lastKernel = k; _lastUser = u;
            return double.NaN;
        }
        var di = i - _lastIdle;
        var dk = k - _lastKernel;
        var du = u - _lastUser;
        _lastIdle = i; _lastKernel = k; _lastUser = u;
        var total = dk + du; // kernel 已含 idle
        if (total == 0) return double.NaN;
        return Math.Clamp((1.0 - (double)di / total) * 100.0, 0, 100);
    }

    // ---- CPU 频率 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer,
        uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize);

    private static double ReadCpuMhz()
    {
        try
        {
            var count = Math.Max(1, Environment.ProcessorCount);
            var size = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size * count);
            try
            {
                // ProcessorInformation = 11
                if (CallNtPowerInformation(11, IntPtr.Zero, 0, ptr, (uint)(size * count)) != 0)
                    return double.NaN;
                var sum = 0UL;
                for (var i = 0; i < count; i++)
                    sum += Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(ptr + i * size).CurrentMhz;
                return sum > 0 ? (double)sum / count : double.NaN;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { return double.NaN; }
    }

    /// <summary>回退：WMI 报告的当前主频（CallNtPowerInformation 在部分设备返回 0）</summary>
    private static double ReadCpuMhzWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT CurrentClockSpeed FROM Win32_Processor");
            var max = 0.0;
            foreach (var o in searcher.Get())
            {
                var v = o["CurrentClockSpeed"];
                if (v is null) continue;
                max = Math.Max(max, Convert.ToDouble(v));
            }
            return max > 0 ? max : double.NaN;
        }
        catch { return double.NaN; }
    }

    // ---- 内存占用 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    private static double ReadMemoryPercent()
    {
        try
        {
            var mem = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref mem)) return double.NaN;
            return mem.MemoryLoad;
        }
        catch { return double.NaN; }
    }

    // ---- 电池 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatus;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private static void ReadBattery(MonitorSnapshot s)
    {
        if (!GetSystemPowerStatus(out var p)) return;
        s.OnAc = p.ACLineStatus == 1;
        s.Charging = (p.BatteryFlag & 0x08) != 0; // 8 = 正在充电
        if (p.BatteryLifeTime > 0 && p.BatteryLifeTime != -1)
            s.BatteryMinutes = p.BatteryLifeTime / 60.0;
        // 255 = 未知（无电池 / 读取失败）
        if (p.BatteryLifePercent != 255)
            s.BatteryPercent = p.BatteryLifePercent;

        // 充电时用充电功率，放电时用放电功率；接电源且电池已满时两者都为 0（无功率可示）
        if (s.Charging) s.BatteryPower = _batteryRate.Charge;
        else if (!s.OnAc) s.BatteryPower = _batteryRate.Discharge;
    }

    /// <summary>从 root\WMI BatteryStatus 取放电/充电功率（mW）</summary>
    private static (double Discharge, double Charge) ReadBatteryRate()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI",
                "SELECT DischargeRate, ChargeRate FROM BatteryStatus");
            double discharge = 0, charge = 0;
            foreach (var o in searcher.Get())
            {
                if (o["DischargeRate"] is { } d) discharge += Convert.ToDouble(d);
                if (o["ChargeRate"] is { } c) charge += Convert.ToDouble(c);
            }
            return (discharge > 0 ? discharge / 1000.0 : double.NaN,
                    charge > 0 ? charge / 1000.0 : double.NaN);
        }
        catch { return (double.NaN, double.NaN); }
    }

    // ---- GPU 占用 ----

    private static string _gpuEngineStatus = "未采样";
    private static string _gpuMemoryIdentityStatus = "未采样";
    private static string _gpuVramStatus = "未采样";
    private static string _gpuDxgiCandidates = "未枚举";
    private static string _gpuAlignmentStatus = "未对齐";
    private static string _gpuNvmlFallbackStatus = "未尝试";
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    private static string? GpuAdapterKey(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name,
            @"(?:^|_)luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_(\d+)(?:_|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success
            || !uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var low)
            || !uint.TryParse(match.Groups[3].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var phys)) return null;
        return FormattableString.Invariant($"luid_0x{high:x8}_0x{low:x8}_phys_{phys}");
    }

    /// <summary>同物理引擎跨进程求和，适配器取最忙引擎，再选择最忙适配器。</summary>
    private static double ReadGpuUsage()
    {
        _gpuAdapterKey = null;
        _gpuSensorsAligned = false;
        var engines = new Dictionary<(string Adapter, string Engine), double>();
        var adapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            foreach (var o in searcher.Get())
            {
                var name = o["Name"] as string ?? "";
                var adapter = GpuAdapterKey(name);
                if (adapter is null) continue;
                adapters.Add(adapter);
                var engine = System.Text.RegularExpressions.Regex.Match(name, @"(?:^|_)eng_(\d+)(?:_|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!engine.Success || o["UtilizationPercentage"] is not { } v) continue;
                var usage = Convert.ToDouble(v);
                if (!double.IsFinite(usage) || usage < 0) continue;
                var key = (adapter, engine.Groups[1].Value);
                engines.TryGetValue(key, out var sum);
                engines[key] = sum + usage;
            }
            _gpuEngineStatus = $"成功，适配器={adapters.Count}，引擎={engines.Count}";
        }
        catch (Exception ex)
        {
            engines.Clear();
            _gpuEngineStatus = $"错误 0x{ex.HResult:X8}: {ex.Message}";
        }

        var memoryAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var memory = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
            foreach (var o in memory.Get())
                if (GpuAdapterKey(o["Name"] as string ?? "") is { } adapter) memoryAdapters.Add(adapter);
            _gpuMemoryIdentityStatus = $"成功，适配器={memoryAdapters.Count}";
        }
        catch (Exception ex)
        {
            _gpuMemoryIdentityStatus = $"错误 0x{ex.HResult:X8}: {ex.Message}";
        }
        adapters.UnionWith(memoryAdapters);

        var busiest = double.NaN;
        foreach (var entry in engines)
        {
            if (!double.IsNaN(busiest) && entry.Value <= busiest) continue;
            busiest = entry.Value;
            _gpuAdapterKey = entry.Key.Adapter;
        }
        if (_gpuAdapterKey is null && adapters.Count == 1) _gpuAdapterKey = adapters.First();
        // 厂商传感器身份由完整 DXGI 硬件拓扑确认，不依赖 WMI 是否可用。
        AlignSingleGpuSensors(engines, adapters);
        _gpuEngineStatus += $"；选中引擎匹配={engines.Keys.Any(k => k.Adapter == _gpuAdapterKey)}";
        _gpuMemoryIdentityStatus += $"；选中身份匹配={(_gpuAdapterKey is not null && memoryAdapters.Contains(_gpuAdapterKey))}";
        var selectedEngines = engines.Where(e => e.Key.Adapter == _gpuAdapterKey).Select(e => e.Value).ToArray();
        return selectedEngines.Length == 0 ? double.NaN : Math.Clamp(selectedEngines.Max(), 0, 100);
    }

    private static void AlignSingleGpuSensors(
        Dictionary<(string Adapter, string Engine), double> engines, HashSet<string> adapters)
    {
        _gpuSensorsAligned = false;
        _gpuDxgiCandidates = "无";
        _gpuAlignmentStatus = "未对齐";
        var candidates = new List<string>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            var result = CreateDXGIFactory1(ref iid, out factory);
            if (result < 0)
            {
                _gpuAlignmentStatus = $"拒绝：创建 DXGI 工厂失败 0x{result:X8}";
                return;
            }
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdaptersDelegate>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            var hardware = new List<GpuAdapterDesc>();
            for (uint i = 0; ; i++)
            {
                result = enumerate(factory, i, out var adapter);
                if (result == DxgiErrorNotFound) break;
                if (result < 0)
                {
                    _gpuAlignmentStatus = $"拒绝：DXGI 枚举不完整，index={i} HRESULT=0x{result:X8}";
                    return;
                }
                try
                {
                    var getDesc = Marshal.GetDelegateForFunctionPointer<GetAdapterDescDelegate>(
                        Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 8 * IntPtr.Size));
                    result = getDesc(adapter, out var desc);
                    if (result < 0)
                    {
                        _gpuAlignmentStatus = $"拒绝：DXGI GetDesc 失败，index={i} HRESULT=0x{result:X8}";
                        return;
                    }
                    candidates.Add(FormattableString.Invariant($"{desc.Description} vendor=0x{desc.VendorId:x4} luid_0x{desc.LuidHigh:x8}_0x{desc.LuidLow:x8}_phys_0"));
                    if (desc.VendorId is 0x8086 or 0x1002 or 0x10de) hardware.Add(desc);
                }
                finally { Marshal.Release(adapter); }
            }
            var intelKey = IntelGpuTemp.AdapterKey;
            if (intelKey is not null && adapters.Contains(intelKey))
            {
                var selectedBusy = engines.Any(e => e.Key.Adapter == _gpuAdapterKey && e.Value > 0);
                if (_gpuAdapterKey == intelKey || !selectedBusy)
                {
                    _gpuAdapterKey = intelKey;
                    _gpuAlignmentStatus = "Intel IGCL LUID 与 WMI 精确匹配；" + IntelGpuTemp.IdentityStatus;
                    return;
                }
            }
            if (hardware.Count != 1)
            {
                _gpuAlignmentStatus = $"拒绝：完整 DXGI 拓扑中受支持硬件数={hardware.Count}，要求单卡";
                return;
            }
            var selected = hardware[0];
            var key = FormattableString.Invariant($"luid_0x{selected.LuidHigh:x8}_0x{selected.LuidLow:x8}_phys_0");
            if (_gpuAdapterKey is not null && !string.Equals(_gpuAdapterKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _gpuAlignmentStatus = $"拒绝：选中身份 {_gpuAdapterKey} 与 DXGI 单卡 {key} 不符";
                return;
            }
            _gpuAdapterKey = key;
            _gpuSensorsAligned = selected.VendorId is 0x8086 or 0x1002;
            _gpuAlignmentStatus = _gpuSensorsAligned ? "单卡身份匹配，允许 ADL/IGCL/KMT" : "单卡身份匹配，NVIDIA 须经 NVML 匹配";
        }
        catch (Exception ex) { _gpuAlignmentStatus = $"拒绝：DXGI 异常 0x{ex.HResult:X8}: {ex.Message}"; }
        finally
        {
            _gpuDxgiCandidates = candidates.Count == 0 ? "无" : string.Join("；", candidates);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    /// <summary>所选适配器显存用量（GB）；Intel 核显统计专用+共享，独显仅专用，0 是有效读数。</summary>
    private static double ReadGpuVram()
    {
        var key = _gpuAdapterKey;
        if (key is null)
        {
            _gpuVramStatus = "未查询：无选中适配器";
            return double.NaN;
        }
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name, DedicatedUsage, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
            var matched = false;
            foreach (var o in searcher.Get())
            {
                if (!string.Equals(GpuAdapterKey(o["Name"] as string ?? ""), key, StringComparison.OrdinalIgnoreCase)) continue;
                matched = true;
                if (o["DedicatedUsage"] is not { } d) continue;
                var bytes = Convert.ToDouble(d);
                var integrated = key == IntelGpuTemp.AdapterKey && IntelGpuTemp.IsIntegrated;
                var shared = integrated && o["SharedUsage"] is { } s ? Convert.ToDouble(s) : double.NaN;
                var valid = double.IsFinite(bytes) && bytes >= 0;
                if (integrated)
                {
                    valid &= double.IsFinite(shared) && shared >= 0;
                    bytes += shared;
                }
                _gpuVramStatus = $"匹配 {key}，口径={(integrated ? "DedicatedUsage+SharedUsage（核显）" : "DedicatedUsage")}，有效={valid}，bytes={bytes}";
                return valid ? bytes / 1024d / 1024d / 1024d : double.NaN;
            }
            _gpuVramStatus = matched ? $"匹配 {key}，缺少 DedicatedUsage" : $"无匹配实例：{key}";
            return double.NaN;
        }
        catch (Exception ex)
        {
            _gpuVramStatus = $"错误 0x{ex.HResult:X8}: {ex.Message}";
            return double.NaN;
        }
    }

    // ---- GPU 频率 / 功耗（NVML，需 NVIDIA 驱动自带的 nvml.dll）----

    private static bool _nvmlTried;
    private static readonly List<IntPtr> _nvmlDevices = [];

    private static void EnsureNvml()
    {
        if (_nvmlTried) return;
        _nvmlTried = true;
        try
        {
            if (nvmlInit_v2() != 0) return;
            if (nvmlDeviceGetCount_v2(out var count) != 0) return;
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != 0) continue;
                _nvmlDevices.Add(device);
            }
        }
        catch
        {
            // 无 nvml.dll（AMD/Intel 机器）：保持 NaN，界面显示"—"
        }
    }

    private static IntPtr SelectNvmlDevice()
    {
        EnsureNvml();
        var key = _gpuAdapterKey;
        if (key is null) return IntPtr.Zero;
        var parts = key.Split('_');
        if (parts.Length != 5
            || !uint.TryParse(parts[1].AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(parts[2].AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var low)
            || !uint.TryParse(parts[4], out var phys)) return IntPtr.Zero;
        try
        {
            foreach (var device in _nvmlDevices)
            {
                var luid = new byte[8];
                if (nvmlDeviceGetLuid(device, luid, out var nodeMask) != 0) continue;
                // Windows LUID 原始布局为 LowPart 后接 HighPart，均为小端整数。
                if (BitConverter.ToUInt32(luid, 0) != low || BitConverter.ToUInt32(luid, 4) != high) continue;
                if (nodeMask != 0 && (phys >= 32 || (nodeMask & (1u << (int)phys)) == 0)) continue;
                return device;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // 旧版驱动没有 LUID 接口，仅在 DXGI 和 NVML 都唯一识别 NVIDIA 时绑定。
            if (_nvmlDevices.Count == 1 && phys == 0 && IsUniqueNvidiaAdapter(high, low))
                return _nvmlDevices[0];
        }
        catch { }
        return IntPtr.Zero;
    }

    private static bool IsUniqueNvidiaAdapter(uint high, uint low)
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            var result = CreateDXGIFactory1(ref iid, out factory);
            if (result < 0)
            {
                _gpuNvmlFallbackStatus = $"拒绝：创建 DXGI 工厂失败 0x{result:X8}";
                return false;
            }
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdaptersDelegate>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            var count = 0;
            var matched = false;
            for (uint i = 0; ; i++)
            {
                result = enumerate(factory, i, out var adapter);
                if (result == DxgiErrorNotFound) break;
                if (result < 0)
                {
                    _gpuNvmlFallbackStatus = $"拒绝：DXGI 枚举不完整，index={i} HRESULT=0x{result:X8}";
                    return false;
                }
                try
                {
                    var getDesc = Marshal.GetDelegateForFunctionPointer<GetAdapterDescDelegate>(
                        Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 8 * IntPtr.Size));
                    result = getDesc(adapter, out var desc);
                    if (result < 0)
                    {
                        _gpuNvmlFallbackStatus = $"拒绝：DXGI GetDesc 失败，index={i} HRESULT=0x{result:X8}";
                        return false;
                    }
                    if (desc.VendorId != 0x10de) continue;
                    count++;
                    if (desc.LuidHigh == high && desc.LuidLow == low) matched = true;
                }
                finally { Marshal.Release(adapter); }
            }
            _gpuNvmlFallbackStatus = count == 1 && matched
                ? "完整拓扑中唯一 NVIDIA 且 LUID 匹配"
                : $"拒绝：NVIDIA 数={count}，LUID 匹配={matched}";
            return count == 1 && matched;
        }
        catch (Exception ex)
        {
            _gpuNvmlFallbackStatus = $"拒绝：DXGI 异常 0x{ex.HResult:X8}: {ex.Message}";
            return false;
        }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdaptersDelegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDescDelegate(IntPtr adapter, out GpuAdapterDesc desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct GpuAdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow, LuidHigh;
    }

    private static double ReadGpuPower()
    {
        // 驱动偶发返回异常值（部分 AMD APU 的 ADL 功耗接口会给出 590 W 这类数字），
        // 统一在这里做合理性过滤，任何来源超过 500 W 都视为不可用
        var watts = ReadGpuPowerRaw();
        if (!double.IsFinite(watts) || watts is < 0 or > 500) return double.NaN;
        // Intel 核显空闲时可能真的接近 0 W（四舍五入到 0.0），保留 0 而不是当成读取失败
        var intelIntegrated = _gpuAdapterKey is not null && _gpuAdapterKey == IntelGpuTemp.AdapterKey;
        if (watts > 0 || intelIntegrated) return watts;
        return double.NaN;
    }

    private static double ReadGpuPowerRaw()
    {
        var device = SelectNvmlDevice();
        if (device != IntPtr.Zero)
        {
            try
            {
                // 大于 500 W 的读数必然是驱动异常值，直接丢弃
                if (nvmlDeviceGetPowerUsage(device, out var milliwatts) == 0
                    && milliwatts is > 0 and < 500_000)
                    return milliwatts / 1000.0;
            }
            catch { }
            return double.NaN;
        }
        if (_gpuAdapterKey is not null && _gpuAdapterKey == IntelGpuTemp.AdapterKey)
            return IntelGpuTemp.ReadPower();
        if (!_gpuSensorsAligned) return double.NaN;
        var adlPower = ReadAdlPower();
        if (!double.IsNaN(adlPower)) return adlPower;
        // Intel 核显：IGCL 功率域（多数核显驱动不提供，此时仍为 NaN）
        var igclPower = IntelGpuTemp.ReadPower();
        if (!double.IsNaN(igclPower)) return igclPower;
        return double.NaN;
    }

    private static double ReadGpuClock()
    {
        var device = SelectNvmlDevice();
        if (device != IntPtr.Zero)
        {
            try
            {
                // NVML_CLOCK_GRAPHICS = 0
                if (nvmlDeviceGetClockInfo(device, 0, out var mhz) == 0 && mhz > 0)
                    return mhz;
            }
            catch { }
            return double.NaN;
        }
        if (_gpuAdapterKey is not null && _gpuAdapterKey == IntelGpuTemp.AdapterKey)
            return IntelGpuTemp.ReadClock();
        if (!_gpuSensorsAligned) return double.NaN;
        var adl = ReadAdlClock();
        if (!double.IsNaN(adl)) return adl;
        // Intel 核显：IGCL 频率域（WDDM 的 MaxFrequency 在掌机上常为 0，读不到时钟）
        var igcl = IntelGpuTemp.ReadClock();
        if (!double.IsNaN(igcl)) return igcl;
        return ReadKmtClock();
    }

    /// <summary>
    /// GPU 温度（℃）：匹配适配器的 NVML/ADL/IGCL；Intel 核显回退到 MSI 固件参考温度或 HWiNFO CPU GT。
    /// </summary>
    private static double ReadGpuTemp()
    {
        var device = SelectNvmlDevice();
        if (device != IntPtr.Zero)
        {
            try
            {
                // NVML_TEMPERATURE_GPU = 0
                if (nvmlDeviceGetTemperature(device, 0, out var celsius) == 0 && celsius > 0)
                    return celsius;
            }
            catch { }
            return double.NaN;
        }
        if (_gpuAdapterKey is not null && _gpuAdapterKey == IntelGpuTemp.AdapterKey)
        {
            var temperature = IntelGpuTemp.Read();
            if (!double.IsNaN(temperature)) return temperature;
            if (!IntelGpuTemp.IsIntegrated) return double.NaN;
            // 恢复旧版 MSI 固件参考温度；此热区未经确认为独立 GT 核心温度。
            if (_helperGpuTemp is > 0 and < 130) return _helperGpuTemp;
            var vendor = MsiWmiProbe.ReadGpuTemperature();
            return vendor is > 0 and < 130 ? vendor : HwInfoGtTemperature.Read();
        }
        if (!_gpuSensorsAligned) return double.NaN;
        var adl = ReadAdlTemp();
        if (!double.IsNaN(adl)) return adl;

        var igcl = IntelGpuTemp.Read();
        if (!double.IsNaN(igcl)) return igcl;

        return double.NaN;
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int nvmlDeviceGetCount_v2(out uint count);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetLuid", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetLuid(IntPtr device,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 8)] byte[] luid, out uint deviceNodeMask);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
    private static extern int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint celsius);

    // ---- GPU 频率 / 功耗（AMD：ADL，atiadlxx.dll 由 AMD 显卡驱动提供）----

    private static bool _adlTried;
    private static bool _adlReady;
    private static int _adlCreateResult = int.MinValue;
    private static int _adlAdapter = -1;
    private static int _adlActivitySize = 44;
    private static AdlAlloc? _adlAlloc;
    private static readonly System.Text.StringBuilder _adlProbeLog = new();

    private delegate IntPtr AdlAlloc(int size);

    // ---- GPU 频率兜底：WDDM 节点性能数据（D3DKMT）
    //      Intel 核显没有 NVML/ADL，但 WDDM 会通过节点性能数据给出 GPU 引擎时钟 ----

    private static int _kmtTries;
    private static uint _kmtAdapter;
    private static uint _kmtNode;
    private static string _kmtDiag = "";

    private static void EnsureKmt()
    {
        if (_kmtAdapter != 0 || _kmtTries >= 3) return;
        _kmtTries++;
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if (!dd.DeviceString.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    continue;
                }
                var hdc = CreateDC(null, dd.DeviceName, null, IntPtr.Zero);
                if (hdc == IntPtr.Zero)
                {
                    _kmtDiag += $"CreateDC({dd.DeviceName})=0 ";
                    dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    continue;
                }
                var open = new KMT_OPEN_FROM_HDC { hDc = hdc };
                var st = D3DKMTOpenAdapterFromHdc(ref open);
                DeleteDC(hdc);
                if (st != 0)
                {
                    _kmtDiag += $"Open={st} ";
                    dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    continue;
                }

                // 各节点的 Frequency 是同一个 GT 时钟，取 MaxFrequency 最大的节点作为读数来源
                ulong bestMax = 0;
                uint bestNode = 0;
                var foundNode = false;
                for (uint node = 0; node < 8; node++)
                {
                    var (ret, perf) = QueryNodePerf(open.hAdapter, node);
                    if (ret != 0) continue;
                    if (!foundNode || perf.MaxFrequency > bestMax)
                    {
                        foundNode = true;
                        bestMax = perf.MaxFrequency;
                        bestNode = node;
                    }
                }
                if (foundNode)
                {
                    _kmtAdapter = open.hAdapter;
                    _kmtNode = bestNode;
                    _kmtDiag += $"adapter={open.hAdapter} node={bestNode} max={bestMax / 1_000_000}MHz";
                }
                else
                {
                    _kmtDiag += "所有节点查询失败 ";
                }
                break;
            }
            if (_kmtAdapter == 0 && _kmtDiag.Length == 0) _kmtDiag = "未找到 Intel 显示设备";
        }
        catch (Exception ex) { _kmtDiag = "异常：" + ex.Message; }
    }

    private static (int ret, KMT_NODE_PERF perf) QueryNodePerf(uint adapter, uint node)
    {
        var size = Marshal.SizeOf<KMT_NODE_PERF>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            for (var o = 0; o < size; o += 4) Marshal.WriteInt32(buf, o, 0);
            Marshal.StructureToPtr(new KMT_NODE_PERF { NodeOrdinal = node, PhysicalAdapterIndex = 0 }, buf, false);
            var q = new KMT_QUERY_ADAPTER_INFO { hAdapter = adapter, Type = 61, pData = buf, Size = (uint)size };
            var ret = D3DKMTQueryAdapterInfo(ref q);
            return (ret, ret == 0 ? Marshal.PtrToStructure<KMT_NODE_PERF>(buf) : default);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>WDDM 节点时钟（Hz→MHz）；接口调用失败返回 NaN，读得到 0 也返回 0（空闲时照实显示 0MHz）</summary>
    private static double ReadKmtClock()
    {
        EnsureKmt();
        if (_kmtAdapter == 0) return double.NaN;
        var (ret, perf) = QueryNodePerf(_kmtAdapter, _kmtNode);
        if (ret != 0) return double.NaN;
        return perf.Frequency / 1_000_000.0;
    }

    /// <summary>WDDM 适配器性能数据（仅用于诊断输出；Power 单位不是瓦特）</summary>
    private static (int ret, KMT_ADAPTER_PERF perf) QueryAdapterPerf(uint adapter)
    {
        var size = Marshal.SizeOf<KMT_ADAPTER_PERF>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            for (var o = 0; o < size; o += 4) Marshal.WriteInt32(buf, o, 0);
            var q = new KMT_QUERY_ADAPTER_INFO { hAdapter = adapter, Type = 62, pData = buf, Size = (uint)size };
            var ret = D3DKMTQueryAdapterInfo(ref q);
            return (ret, ret == 0 ? Marshal.PtrToStructure<KMT_ADAPTER_PERF>(buf) : default);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KMT_ADAPTER_PERF
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KMT_OPEN_FROM_HDC
    {
        public IntPtr hDc;
        public uint hAdapter;
        public long AdapterLuid;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KMT_QUERY_ADAPTER_INFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pData;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KMT_NODE_PERF
    {
        public uint NodeOrdinal;
        public uint PhysicalAdapterIndex;
        public ulong Frequency;
        public ulong MaxFrequency;
        public ulong MaxFrequencyOC;
        public uint Voltage;
        public uint VoltageMax;
        public uint VoltageMaxOC;
        public ulong MaxTransitionLatency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string? driver, string device, string? port, IntPtr initData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromHdc(ref KMT_OPEN_FROM_HDC data);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref KMT_QUERY_ADAPTER_INFO data);

    private static bool _diagWritten;

    /// <summary>
    /// 把显卡数据源探测结果写到 %APPDATA%\GameLocker\monitor-diag.txt（每次启动写一次），
    /// 方便在没有开发环境的掌机上排查"读不到 GPU 频率/功耗"。
    /// </summary>
    private static void WriteGpuDiagnostics()
    {
        if (_diagWritten) return;
        _diagWritten = true;
        try
        {
            EnsureNvml();
            EnsureAdl();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[GameLocker GPU 诊断] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"OS {Environment.OSVersion.VersionString} / x64={Environment.Is64BitProcess} / 管理员={IsElevated()}");
            var adlPath = Path.Combine(Environment.SystemDirectory, "atiadlxx.dll");
            var nvmlPath = Path.Combine(Environment.SystemDirectory, "nvml.dll");
            sb.AppendLine($"atiadlxx.dll={(File.Exists(adlPath) ? "存在" : "缺失")} ({adlPath})");
            sb.AppendLine($"nvml.dll={(File.Exists(nvmlPath) ? "存在" : "缺失")}");
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
                foreach (var o in searcher.Get())
                    sb.AppendLine($"显卡: {o["Name"]} / 驱动 {o["DriverVersion"]}");
            }
            catch (Exception ex) { sb.AppendLine("WMI 显卡枚举失败: " + ex.Message); }
            sb.AppendLine("[独立数据源探测：原始读数不代表已匹配浮层所选 GPU]");
            sb.AppendLine($"ADL: create={_adlCreateResult} ready={_adlReady} adapter={_adlAdapter} activitySize={_adlActivitySize}");
            sb.AppendLine($"ADL 探测: {_adlProbeLog}");
            sb.AppendLine($"ADL 读数: clock={ReadAdlClock()} MHz, power={ReadAdlPower()} W");
            sb.AppendLine($"NVML: devices={_nvmlDevices.Count} adapter={_gpuAdapterKey ?? "无"} matched={(SelectNvmlDevice() != IntPtr.Zero ? "是" : "否")}");
            var kmtClock = ReadKmtClock();
            sb.AppendLine($"WDDM(KMT): {(_kmtDiag.Length > 0 ? _kmtDiag : "未尝试")}, node={_kmtNode}, clock={kmtClock} MHz");
            if (_kmtAdapter != 0)
            {
                var (aret, aperf) = QueryAdapterPerf(_kmtAdapter);
                sb.AppendLine($"WDDM 适配器: ret={aret} memFreq={aperf.MemoryFrequency} maxMem={aperf.MaxMemoryFrequency}"
                    + $" power(0.1%)={aperf.Power} temp(0.1C)={aperf.Temperature} fan={aperf.FanRPM}");
            }
            // 注意先 Read() 再取 Status：Status 是初始化时写的，顺序反了会显示"未尝试"
            var igclTemp = IntelGpuTemp.Read();
            sb.AppendLine($"ControlLib.dll={(File.Exists(Path.Combine(Environment.SystemDirectory, "ControlLib.dll")) ? "存在" : "缺失")}"
                + $" / ze_loader.dll={(File.Exists(Path.Combine(Environment.SystemDirectory, "ze_loader.dll")) ? "存在" : "缺失")}");
            sb.AppendLine($"IGCL 温度: {IntelGpuTemp.Status} / temp={igclTemp} ℃");
            var igclClock = IntelGpuTemp.ReadClock();
            sb.AppendLine($"IGCL 频率: {IntelGpuTemp.ClockStatus} / clock={igclClock} MHz");
            var igclPower = IntelGpuTemp.ReadPower();
            sb.AppendLine($"IGCL 功耗: {IntelGpuTemp.PowerStatus} / power={igclPower} W / {IntelGpuTemp.PowerDetail}");
            var vendorTemp = MsiWmiProbe.ReadGpuTemperature();
            sb.AppendLine($"厂商 WMI: {(MsiWmiProbe.Available ? "可用（" + MsiWmiProbe.MethodList + "）" : MsiWmiProbe.Status)}"
                + $" / 未验证热区原始读数={vendorTemp} ℃（不作为 GPU 温度）");
            sb.AppendLine($"CPU 温度: 采用={_cpuTemp} ℃（仅可信 MSR 来源）");
            var msrTemp = IsElevated() ? PawnIoMsr.ReadTemperature() : double.NaN;
            var msrStatus = IsElevated()
                ? PawnIoMsr.Status
                : (double.IsNaN(_helperCpuTemp) ? "主进程未提权，助手未回传" : "主进程未提权，由助手读取");
            sb.AppendLine($"PawnIO/MSR: {msrStatus} / temp={msrTemp} ℃ / 助手回传={_helperCpuTemp} ℃");
            sb.AppendLine($"厂商传感器(EC/WMI): {(_helperAlive ? "由提权助手采集（见日志末尾「厂商传感器」段）" : "监控未开启或助手未运行")}");
            foreach (var line in ThermalZoneDetails()) sb.AppendLine("  " + line);
            sb.AppendLine($"CPU 功耗(RAPL): {ReadCpuPower()} W");
            sb.AppendLine($"GPU 匹配后即时探测（非浮层快照）: clock={ReadGpuClock()} MHz, power={ReadGpuPower()} W, temp={ReadGpuTemp()} ℃");
            try
            {
                var frame = FrameLimitService.Read();
                sb.AppendLine($"锁帧: backend={frame.Backend} supported={frame.Supported} fps={frame.Fps}"
                    + $" range={frame.Minimum}-{frame.Maximum}/{frame.Step} msg={frame.Message}");
                sb.AppendLine($"Intel 限帧能力: {IntelGpuTemp.FrameLimitDetail}");
                sb.AppendLine($"锁帧最近应用: {FrameLimitService.LastApply}");
            }
            catch (Exception ex) { sb.AppendLine("锁帧诊断失败: " + ex.Message); }
            sb.AppendLine("[GPU 最近采样身份与匹配状态：未采样时保持未采样，不在诊断线程重置采样]");
            sb.AppendLine($"所选 GPU: {_gpuAdapterKey ?? "无"} / 单卡传感器对齐={_gpuSensorsAligned}");
            sb.AppendLine($"引擎 WMI: {_gpuEngineStatus}");
            sb.AppendLine($"显存身份 WMI: {_gpuMemoryIdentityStatus}");
            sb.AppendLine($"GPU 显存 WMI: {_gpuVramStatus}（Intel 核显含共享，独显仅专用；不含预算）");
            sb.AppendLine($"IGCL 设备身份: {IntelGpuTemp.IdentityStatus}");
            sb.AppendLine($"DXGI 候选: {_gpuDxgiCandidates}");
            sb.AppendLine($"DXGI 对齐: {_gpuAlignmentStatus}");
            sb.AppendLine($"NVML 旧驱动唯一卡回退: {_gpuNvmlFallbackStatus}");
            foreach (var line in PowerSourceDetails()) sb.AppendLine("  " + line);
            sb.AppendLine($"电池功率: 放电={_batteryRate.Discharge} W / 充电={_batteryRate.Charge} W");
            // 直接把浮层当前显示的数值写进诊断：哪一项是"—"一目了然
            static string V(double v, string unit) => double.IsNaN(v) ? "—" : v.ToString("0.#") + unit;
            lock (Gate)
            {
                var s = _snapshot;
                sb.AppendLine($"浮层 CPU: 使用 {s.CpuUsage:0}% / 频率 {V(s.CpuMhz, "MHz")} / 温度 {V(s.CpuTemp, "℃")} / 功耗 {V(s.CpuPower, "W")}");
                sb.AppendLine($"浮层 GPU 快照: 使用 {V(s.GpuUsage, "%")} / 频率 {V(s.GpuMhz, "MHz")} / 温度 {V(s.GpuTemp, "℃")} / 功耗 {V(s.GpuPower, "W")} / 显存 {V(s.GpuVram, "GB")}");
                sb.AppendLine($"浮层 电池: 电量 {V(s.BatteryPercent, "%")} / 功率 {V(s.BatteryPower, "W")} / 充电={s.Charging} / 接电源={s.OnAc}");
            }
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "monitor-diag.txt"), sb.ToString());
        }
        catch { }
    }

    /// <summary>导出日志前重新生成一次诊断（监控是否在跑都有效），保证内容反映当前状态</summary>
    public static void RefreshDiagnostics()
    {
        _diagWritten = false;
        WriteGpuDiagnostics();
    }

    /// <summary>诊断用：逐个列出性能计数器 / ACPI 热区的原始温度，便于确认"固定 28℃"是哪个源</summary>
    private static List<string> ThermalZoneDetails()
    {
        var lines = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            foreach (var o in searcher.Get())
            {
                if (o["Temperature"] is not { } v) continue;
                lines.Add($"[perf] {o["Name"]} = {Convert.ToDouble(v) - 273.15:0.##} ℃");
            }
        }
        catch (Exception ex) { lines.Add("[perf] 不可用：" + ex.Message); }
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (var o in searcher.Get())
            {
                if (o["CurrentTemperature"] is not { } v) continue;
                lines.Add($"[acpi] {o["InstanceName"]} = {Convert.ToDouble(v) / 10.0 - 273.15:0.##} ℃");
            }
        }
        catch (Exception ex) { lines.Add("[acpi] 不可用：" + ex.Message); }
        return lines;
    }

    /// <summary>诊断用：列出 RAPL 能耗计量器的每个条目（CPU 封装功耗的来源）</summary>
    private static List<string> PowerSourceDetails()
    {
        var lines = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name, Power FROM Win32_PerfFormattedData_PowerMeterCounter_EnergyMeter");
            foreach (var o in searcher.Get())
            {
                var power = o["Power"] is { } v ? $"{Convert.ToDouble(v) / 1000.0:0.##} W" : "空";
                lines.Add($"[rapl] {o["Name"]} = {power}");
            }
        }
        catch (Exception ex) { lines.Add("[rapl] 不可用：" + ex.Message); }
        return lines;
    }

    /// <summary>
    /// 初始化 ADL 并找到能读出核心频率的适配器。
    /// 不同版本 ADL 的 ADLPMActivity 结构大小不同，这里逐个尺寸试探，避免结构体布局不一致导致失败。
    /// </summary>
    private static void EnsureAdl()
    {
        if (_adlTried) return;
        _adlTried = true;
        try
        {
            _adlAlloc = size => Marshal.AllocHGlobal(size);
            _adlCreateResult = ADL_Main_Control_Create(_adlAlloc, 1);
            if (_adlCreateResult != 0) return;

            var buffer = Marshal.AllocHGlobal(160);
            try
            {
                for (var i = 0; i < 8 && _adlAdapter < 0; i++)
                {
                    foreach (var size in new[] { 44, 40, 48, 64, 68, 80, 160 })
                    {
                        for (var off = 0; off < 160; off += 4) Marshal.WriteInt32(buffer, off, 0);
                        Marshal.WriteInt32(buffer, 0, size);
                        var ret = ADL_Overdrive5_CurrentActivity_Get(i, buffer);
                        if (_adlProbeLog.Length < 600) _adlProbeLog.Append($"[{i}/{size}]={ret} ");
                        if (ret != 0) continue;
                        _adlAdapter = i;
                        _adlActivitySize = size;
                        break;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            _adlReady = _adlAdapter >= 0;
        }
        catch
        {
            // 无 atiadlxx.dll（NVIDIA / Intel 机器）：保持不可用
            _adlReady = false;
        }
    }

    /// <summary>AMD 核心频率（ADL 返回单位 10kHz，换算为 MHz）</summary>
    private static double ReadAdlClock()
    {
        EnsureAdl();
        if (!_adlReady) return double.NaN;

        var buffer = Marshal.AllocHGlobal(160);
        try
        {
            for (var off = 0; off < 160; off += 4) Marshal.WriteInt32(buffer, off, 0);
            Marshal.WriteInt32(buffer, 0, _adlActivitySize);
            if (ADL_Overdrive5_CurrentActivity_Get(_adlAdapter, buffer) != 0) return double.NaN;
            var clock = Marshal.ReadInt32(buffer, 4); // iEngineClock
            return clock > 0 ? clock / 100.0 : double.NaN;
        }
        catch { return double.NaN; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>AMD 核心温度（ADL OD5 Temperature，单位 1/1000 ℃；APU 或驱动不支持时返回 NaN）</summary>
    private static double ReadAdlTemp()
    {
        EnsureAdl();
        if (!_adlReady) return double.NaN;

        var buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(buffer, 0, 8); // ADLTemperature.iSize
            if (ADL_Overdrive5_Temperature_Get(_adlAdapter, 0, buffer) != 0) return double.NaN;
            var milli = Marshal.ReadInt32(buffer, 4); // iTemperature（1/1000 ℃）
            return milli > 0 ? milli / 1000.0 : double.NaN;
        }
        catch { return double.NaN; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>AMD 整卡功耗（ADL OD6，单位 1/256 W；APU 上驱动可能不支持，此时返回 NaN）</summary>
    private static double ReadAdlPower()
    {
        EnsureAdl();
        if (!_adlReady) return double.NaN;
        try
        {
            // ADL_OD6_POWER_NOW = 0
            if (ADL_Overdrive6_CurrentPower_Get(_adlAdapter, 0, out var rawPower) != 0) return double.NaN;
            var watts = rawPower / 256.0;
            return watts is > 0 and < 500 ? watts : double.NaN;
        }
        catch { return double.NaN; }
    }

    [DllImport("atiadlxx.dll")]
    private static extern int ADL_Main_Control_Create(AdlAlloc alloc, int enumConnectedAdapters);

    [DllImport("atiadlxx.dll")]
    private static extern int ADL_Overdrive5_CurrentActivity_Get(int adapterIndex, IntPtr activity);

    [DllImport("atiadlxx.dll")]
    private static extern int ADL_Overdrive6_CurrentPower_Get(int adapterIndex, int powerType, out int power);

    [DllImport("atiadlxx.dll")]
    private static extern int ADL_Overdrive5_Temperature_Get(int adapterIndex, int thermalControllerIndex, IntPtr temperature);

    // ---- CPU 功耗 ----

    /// <summary>
    /// 从 RAPL 性能计数器取 CPU 封装功耗：EnergyMeter 中 RAPL_Package0_PKG 的 Power 字段
    /// （单位 mW，普通权限可读）。不可用时返回 NaN（界面显示"—"）。
    /// </summary>
    private static double ReadCpuPower()
    {
        try
        {
            double pkg = double.NaN;
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name, Power FROM Win32_PerfFormattedData_PowerMeterCounter_EnergyMeter");
            foreach (var o in searcher.Get())
            {
                var name = o["Name"] as string ?? "";
                if (o["Power"] is not { } v) continue;
                var mw = Convert.ToDouble(v);
                if (name.Contains("RAPL_Package0_PKG", StringComparison.OrdinalIgnoreCase)) pkg = mw;
            }
            if (pkg > 0) return pkg / 1000.0;
        }
        catch { }
        return double.NaN;
    }

}
