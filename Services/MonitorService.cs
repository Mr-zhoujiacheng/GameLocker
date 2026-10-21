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
    /// <summary>电池功率（W，充电时为充电功率、放电时为放电功率，NaN=不可用）</summary>
    public double BatteryPower = double.NaN;
    /// <summary>预计可用/充满时间（分钟，NaN=不可用）</summary>
    public double BatteryMinutes = double.NaN;
    /// <summary>电池电量（%，NaN=不可用）</summary>
    public double BatteryPercent = double.NaN;
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
                m?.MonitorOpacity ?? 1.0);
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

    /// <summary>把监控磁贴里保存的窗口设置（位置/大小/透明度）实时应用到悬浮条；未开启时忽略</summary>
    public static void ApplySettings()
    {
        if (!IsRunning) return;
        var m = FindMonitorItem();
        _overlay?.ApplySettings(
            m?.MonitorPos ?? MonitorPosition.TopCenter,
            m?.MonitorScale ?? 1.0,
            m?.MonitorOpacity ?? 1.0);
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
            _overlay?.Update(snapshot);
            // 全屏游戏切前后，DWM 可能把悬浮条降级到普通层；前台全屏时每秒重新置顶
            if (FullscreenDetector.IsForegroundFullscreen()) _overlay?.ReassertTopmost();
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
            GpuPower = ReadGpuPower()
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
        if (p.BatteryLifePercent != 255) s.BatteryPercent = p.BatteryLifePercent; // 255 = 未知
        if (p.BatteryLifeTime > 0 && p.BatteryLifeTime != -1)
            s.BatteryMinutes = p.BatteryLifeTime / 60.0;

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
        if (_nvmlDevice == IntPtr.Zero) return double.NaN;
        try
        {
            if (nvmlDeviceGetPowerUsage(_nvmlDevice, out var milliwatts) != 0) return double.NaN;
            return milliwatts > 0 ? milliwatts / 1000.0 : double.NaN;
        }
        catch { return double.NaN; }
    }

    private static double ReadGpuClock()
    {
        EnsureNvml();
        if (_nvmlDevice == IntPtr.Zero) return double.NaN;
        try
        {
            // NVML_CLOCK_GRAPHICS = 0
            if (nvmlDeviceGetClockInfo(_nvmlDevice, 0, out var mhz) != 0) return double.NaN;
            return mhz > 0 ? mhz : double.NaN;
        }
        catch { return double.NaN; }
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
