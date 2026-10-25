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
    /// <summary>平均帧时间（毫秒，0=不可用）</summary>
    public double FrameMs;
    /// <summary>帧时间序列（毫秒，旧→新），用于绘制帧波动线</summary>
    public double[] FrameHistory = [];
    /// <summary>系统 CPU 总占用率（%）</summary>
    public double CpuUsage;
    /// <summary>CPU 当前平均频率（MHz，0=不可用）</summary>
    public double CpuMhz;
    /// <summary>CPU 温度（℃，NaN=不可用）</summary>
    public double CpuTemp = double.NaN;
    /// <summary>CPU 封装功耗（W，NaN=不可用；来自 RAPL 性能计数器）</summary>
    public double CpuPower = double.NaN;
    /// <summary>GPU 3D 引擎占用率（%，NaN=不可用）</summary>
    public double GpuUsage = double.NaN;
    /// <summary>GPU 频率（MHz，NaN=不可用；来自 NVML，无 NVIDIA 显卡时为 NaN）</summary>
    public double GpuMhz = double.NaN;
    /// <summary>GPU 功耗（W，NaN=不可用；来自 NVML）</summary>
    public double GpuPower = double.NaN;
    /// <summary>GPU 温度（℃，NaN=不可用；来自 NVML / ADL）</summary>
    public double GpuTemp = double.NaN;
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
    public double MemPercent;
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
    private static int _ticking;
    private static int _tickCount;

    /// <summary>主窗口当前是否处于前台（Activated 且非 Deactivated）。MainWindow 在 Activated/Deactivated 里驱动。
    /// 后台时降低采样/重绘频率以省资源，但 ETW 帧率会话保持全速（后台玩游戏仍要显示真实帧数据）</summary>
    private static int _appIsForeground = 1;
    private static int _backgroundTick;
    private static int _gcDoneThisBackground;

    /// <summary>主窗口切换前台/后台；由 MainWindow 在 Activated/Deactivated 时调用</summary>
    public static void SetAppForeground(bool foreground)
    {
        var prev = Interlocked.Exchange(ref _appIsForeground, foreground ? 1 : 0);
        if (prev == (foreground ? 1 : 0)) return;
        if (foreground)
        {
            _backgroundTick = 0;
            _gcDoneThisBackground = 0;
            // 重新切到前台立刻全功率采样一次，避免回来后看到的还是后台低频的旧数据
            _timer?.Change(200, 1000);
        }
    }

    private static ulong _lastIdle, _lastKernel, _lastUser;

    private static double _gpuUsage = double.NaN;
    private static double _cpuTemp = double.NaN;
    private static double _cpuPower = double.NaN;
    private static double _cpuMhzFallback;

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
        _lastIdle = _lastKernel = _lastUser = 0;
        LastError = "";

        // 帧率：已提权 → 本地 ETW；未提权 → 拉起提权助手（UAC 取消则降级为无帧率）
        if (IsElevated())
        {
            _shared?.Dispose();
            _shared = null;
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

        try
        {
            var m = FindMonitorItem();
            _overlay = new MonitorOverlayWindow(
                m?.MonitorPos ?? MonitorPosition.TopCenter,
                m?.MonitorScale ?? 1.0,
                m?.MonitorMetrics ?? MonitorMetric.All,
                m?.MonitorColor ?? MonitorColorTheme.Default,
                m?.MonitorAlwaysOn ?? false);
            if (!_overlay.Create())
            {
                _overlay.Dispose();
                _overlay = null;
            }
        }
        catch { _overlay = null; }

        _timer = new Timer(Tick, null, 250, 1000);
        _ = Task.Run(WriteGpuDiagnostics);
        StateChanged?.Invoke();
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
        var m = FindMonitorItem();
        _overlay?.ApplySettings(
            m?.MonitorPos ?? MonitorPosition.TopCenter,
            m?.MonitorScale ?? 1.0,
            m?.MonitorMetrics ?? MonitorMetric.All,
            m?.MonitorColor ?? MonitorColorTheme.Default,
            m?.MonitorAlwaysOn ?? false);
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
            var snapshot = Sample();
            lock (Gate) _snapshot = snapshot;

            // 悬浮条显示策略：主窗口前台时永远显示；主窗口后台时只有全屏游戏才显示（避免白白消耗 GPU）；
            // 用户勾选"常开"时后台也一直显示并保持 1 秒刷新
            var appFg = Volatile.Read(ref _appIsForeground) == 1;
            var fullscreen = FullscreenDetector.IsForegroundFullscreen();
            var alwaysOn = _overlay?.AlwaysOn == true;
            _overlay?.SetForeground(appFg, fullscreen);

            if (!appFg && !fullscreen && !alwaysOn)
            {
                // 后台累计 10 秒后主动 GC 一次（不频繁触发）
                var bg = Interlocked.Increment(ref _backgroundTick);
                if (bg == 10 && Interlocked.CompareExchange(ref _gcDoneThisBackground, 1, 0) == 0)
                {
                    _ = Task.Run(() =>
                    {
                        try { GC.Collect(2, GCCollectionMode.Optimized, true, true); } catch { }
                    });
                }
                // 后台每 3 秒重绘一次（Tick 每 1s 到，所以隔 3 轮真正 Update 一次）
                if (bg % 3 != 0) return;
            }
            else
            {
                _backgroundTick = 0;
                _gcDoneThisBackground = 0;
            }

            _overlay?.Update(snapshot);
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
        var s = new MonitorSnapshot
        {
            CpuUsage = ReadCpuUsage(),
            CpuMhz = ReadCpuMhz(),
            MemPercent = ReadMemoryPercent(),
            GpuMhz = ReadGpuClock(),
            GpuPower = ReadGpuPower(),
            GpuTemp = ReadGpuTemp()
        };

        // GPU 占用 / 电池功率走 WMI（较慢）、温度走 WMI，错开周期在后台采样并缓存
        if (n % 2 == 0)
        {
            _gpuUsage = ReadGpuUsage();
            _batteryRate = ReadBatteryRate();
        }
        if (n % 5 == 0)
        {
            _cpuTemp = ReadCpuTemp();
            _cpuPower = ReadCpuPower();
            if (_cpuMhzFallback <= 0) _cpuMhzFallback = ReadCpuMhzWmi();
        }
        s.GpuUsage = _gpuUsage;
        s.CpuTemp = _cpuTemp;
        s.CpuPower = _cpuPower;
        if (s.CpuMhz <= 0) s.CpuMhz = _cpuMhzFallback;

        ReadBattery(s);

        // 帧率：本地 ETW 优先，否则读提权助手发布的共享内存
        if (_etw is { Available: true })
        {
            _etw.TargetPid = ForegroundPid();
            var (fps, frameMs, history) = _etw.GetStats();
            s.Fps = fps;
            s.FrameMs = frameMs;
            s.FrameHistory = history;
        }
        else if (_shared is not null)
        {
            _shared.SetTargetPid(ForegroundPid());
            if (_shared.TryRead(out var fps, out var frameMs, out var history))
            {
                _helperAlive = true;
                s.Fps = fps;
                s.FrameMs = frameMs;
                s.FrameHistory = history;
            }
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
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var i = ToValue(idle);
        var k = ToValue(kernel);
        var u = ToValue(user);
        if (_lastKernel == 0)
        {
            _lastIdle = i; _lastKernel = k; _lastUser = u;
            return 0;
        }
        var di = i - _lastIdle;
        var dk = k - _lastKernel;
        var du = u - _lastUser;
        _lastIdle = i; _lastKernel = k; _lastUser = u;
        var total = dk + du; // kernel 已含 idle
        if (total == 0) return 0;
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
                    return 0;
                var sum = 0UL;
                for (var i = 0; i < count; i++)
                    sum += Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(ptr + i * size).CurrentMhz;
                return (double)sum / count;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { return 0; }
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
            return max;
        }
        catch { return 0; }
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
            if (!GlobalMemoryStatusEx(ref mem)) return 0;
            return mem.MemoryLoad;
        }
        catch { return 0; }
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

    /// <summary>汇总 GPUEngine 计数器中 3D 引擎的占用率（Windows 性能计数器，普通权限可读）</summary>
    private static double ReadGpuUsage()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            double sum = 0;
            var found = false;
            foreach (var o in searcher.Get())
            {
                var name = o["Name"] as string ?? "";
                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                var v = o["UtilizationPercentage"];
                if (v is null) continue;
                sum += Convert.ToDouble(v);
                found = true;
            }
            return found ? Math.Clamp(sum, 0, 100) : double.NaN;
        }
        catch { return double.NaN; }
    }

    // ---- GPU 频率 / 功耗（NVML，需 NVIDIA 驱动自带的 nvml.dll）----

    private static bool _nvmlTried;
    private static IntPtr _nvmlDevice;

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
                _nvmlDevice = device;
                break;
            }
        }
        catch
        {
            // 无 nvml.dll（AMD/Intel 机器）：保持 NaN，界面显示"—"
            _nvmlDevice = IntPtr.Zero;
        }
    }

    private static double ReadGpuPower()
    {
        EnsureNvml();
        if (_nvmlDevice != IntPtr.Zero)
        {
            try
            {
                if (nvmlDeviceGetPowerUsage(_nvmlDevice, out var milliwatts) == 0 && milliwatts > 0)
                    return milliwatts / 1000.0;
            }
            catch { }
        }
        var adlPower = ReadAdlPower();
        if (!double.IsNaN(adlPower)) return adlPower;
        // Intel 核显：WDDM 适配器性能数据的 Power 字段单位是"最大功耗的百分比"，
        // 无法换算为瓦特（本机恒为 0），因此不做兜底，界面省略功耗段
        return double.NaN;
    }

    private static double ReadGpuClock()
    {
        EnsureNvml();
        if (_nvmlDevice != IntPtr.Zero)
        {
            try
            {
                // NVML_CLOCK_GRAPHICS = 0
                if (nvmlDeviceGetClockInfo(_nvmlDevice, 0, out var mhz) == 0 && mhz > 0)
                    return mhz;
            }
            catch { }
        }
        var adl = ReadAdlClock();
        if (!double.IsNaN(adl)) return adl;
        return ReadKmtClock();
    }

    /// <summary>GPU 温度（℃）：NVIDIA 走 NVML，AMD 走 ADL；Intel 核显没有通用温度接口，返回 NaN</summary>
    private static double ReadGpuTemp()
    {
        EnsureNvml();
        if (_nvmlDevice != IntPtr.Zero)
        {
            try
            {
                // NVML_TEMPERATURE_GPU = 0
                if (nvmlDeviceGetTemperature(_nvmlDevice, 0, out var celsius) == 0 && celsius > 0)
                    return celsius;
            }
            catch { }
        }
        return ReadAdlTemp();
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int nvmlDeviceGetCount_v2(out uint count);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

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
                for (uint node = 0; node < 8; node++)
                {
                    var (ret, perf) = QueryNodePerf(open.hAdapter, node);
                    if (ret != 0) continue;
                    if (perf.MaxFrequency > bestMax)
                    {
                        bestMax = perf.MaxFrequency;
                        bestNode = node;
                    }
                }
                if (bestMax > 0)
                {
                    _kmtAdapter = open.hAdapter;
                    _kmtNode = bestNode;
                    _kmtDiag += $"adapter={open.hAdapter} node={bestNode} max={bestMax / 1_000_000}MHz";
                }
                else
                {
                    _kmtDiag += "所有节点 MaxFrequency=0 ";
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
            sb.AppendLine($"ADL: create={_adlCreateResult} ready={_adlReady} adapter={_adlAdapter} activitySize={_adlActivitySize}");
            sb.AppendLine($"ADL 探测: {_adlProbeLog}");
            sb.AppendLine($"ADL 读数: clock={ReadAdlClock()} MHz, power={ReadAdlPower()} W");
            sb.AppendLine($"NVML: device={(_nvmlDevice != IntPtr.Zero ? "已找到" : "无")}");
            var kmtClock = ReadKmtClock();
            sb.AppendLine($"WDDM(KMT): {(_kmtDiag.Length > 0 ? _kmtDiag : "未尝试")}, node={_kmtNode}, clock={kmtClock} MHz");
            if (_kmtAdapter != 0)
            {
                var (aret, aperf) = QueryAdapterPerf(_kmtAdapter);
                sb.AppendLine($"WDDM 适配器: ret={aret} memFreq={aperf.MemoryFrequency} maxMem={aperf.MaxMemoryFrequency}"
                    + $" power(0.1%)={aperf.Power} temp(0.1C)={aperf.Temperature} fan={aperf.FanRPM}");
            }
            sb.AppendLine($"GPU 最终读数: clock={ReadGpuClock()} MHz, power={ReadGpuPower()} W");
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "monitor-diag.txt"), sb.ToString());
        }
        catch { }
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

    /// <summary>AMD 整卡功耗（ADL OD6，单位 mW；APU 上驱动可能不支持，此时返回 NaN）</summary>
    private static double ReadAdlPower()
    {
        EnsureAdl();
        if (!_adlReady) return double.NaN;
        try
        {
            // ADL_OD6_POWER_NOW = 0
            if (ADL_Overdrive6_CurrentPower_Get(_adlAdapter, 0, out var milliwatts) != 0) return double.NaN;
            return milliwatts > 0 ? milliwatts / 1000.0 : double.NaN;
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
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name, Power FROM Win32_PerfFormattedData_PowerMeterCounter_EnergyMeter");
            foreach (var o in searcher.Get())
            {
                var name = o["Name"] as string ?? "";
                if (!name.Contains("RAPL_Package0_PKG", StringComparison.OrdinalIgnoreCase)) continue;
                if (o["Power"] is not { } v) continue;
                var mw = Convert.ToDouble(v);
                if (mw > 0) return mw / 1000.0;
            }
        }
        catch { }
        return double.NaN;
    }

    // ---- CPU 温度 ----

    /// <summary>
    /// 优先读性能计数器里的 ACPI 热区温度（Kelvin，普通权限可读，无需 WMI 特权）；
    /// 不可用时再尝试 root\WMI MSAcpi_ThermalZoneTemperature（需管理员）。
    /// </summary>
    private static double ReadCpuTemp()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            var best = double.NaN;
            foreach (var o in searcher.Get())
            {
                var v = o["Temperature"];
                if (v is null) continue;
                var c = Convert.ToDouble(v) - 273.15; // 性能计数器为 Kelvin
                if (c is > 10 and < 150 && (double.IsNaN(best) || c > best)) best = c;
            }
            if (!double.IsNaN(best)) return best;
        }
        catch { }
        return ReadAcpiTemp();
    }

    private static double ReadAcpiTemp()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            var best = double.NaN;
            foreach (var o in searcher.Get())
            {
                var v = o["CurrentTemperature"];
                if (v is null) continue;
                var c = Convert.ToDouble(v) / 10.0 - 273.15;
                if (double.IsNaN(best) || c > best) best = c;
            }
            return best is > 0 and < 150 ? best : double.NaN;
        }
        catch { return double.NaN; }
    }
}
