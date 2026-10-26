using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// Intel 核显（Arc / Iris Xe）的驱动级读数：走 Intel IGCL（ControlLib.dll，随显卡驱动安装）。
/// 实测要点：
/// - ctlInit 的 Version 是"结构版本"，必须传 0；flags 必须带 CTL_INIT_FLAG_USE_LEVEL_ZERO(1)，
///   否则频率/温度这类遥测接口不可用（传 0 时温度枚举直接报缺少 Level Zero）。
/// - ctlFrequencyGetState 的 Version 同样必须传 0（传 1 报 UNSUPPORTED_VERSION）。
/// - 温度传感器由驱动决定：多数核显驱动不提供（枚举成功但数量为 0），此时温度返回 NaN；
///   频率域一般都有，可拿到当前核心频率。
/// 初始化只尝试一次，失败后保持不可用，不影响每秒采样。
/// </summary>
internal static class IntelGpuTemp
{
    /// <summary>传感器类型：GLOBAL=0（整个设备最高温）、GPU=1（GPU 核心最高温）</summary>
    private const int SensorGlobal = 0;
    private const int SensorGpu = 1;

    /// <summary>CTL_INIT_FLAG_USE_LEVEL_ZERO：频率/温度等遥测接口需要</summary>
    private const uint InitFlagUseLevelZero = 1;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public Guid ApplicationUid;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct TempProperties
    {
        public uint Size;
        public byte Version;
        public int Type;
        public double MaxTemperature;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct FreqState
    {
        public uint Size;
        public byte Version;
        public double CurrentVoltage;
        public double Request;
        public double Tdp;
        public double Efficient;
        public double Actual;
        public uint ThrottleReasons;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitDelegate(ref InitArgs args, out IntPtr apiHandle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumDevicesDelegate(IntPtr apiHandle, ref uint count, IntPtr devices);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumSensorsDelegate(IntPtr device, ref uint count, IntPtr sensors);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SensorPropertiesDelegate(IntPtr sensor, ref TempProperties properties);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SensorStateDelegate(IntPtr sensor, out double temperature);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumFrequencyDelegate(IntPtr device, ref uint count, IntPtr domains);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FrequencyStateDelegate(IntPtr domain, ref FreqState state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPowerDelegate(IntPtr device, ref uint count, IntPtr domains);

    /// <summary>ctl_power_energy_counter_t：能量（微焦）与时间戳（微秒）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct EnergyCounter
    {
        public ulong Energy;
        public ulong Timestamp;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PowerEnergyDelegate(IntPtr domain, ref EnergyCounter counter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    private static readonly object Gate = new();
    private static bool _tried;
    private static bool _tempReady;
    private static bool _freqReady;
    private static IntPtr _module;
    private static IntPtr _sensor;
    private static IntPtr _freq;
    private static IntPtr _powerDomain;
    private static uint _powerDomainCount;
    private static ulong _lastEnergy;
    private static ulong _lastEnergyTs;
    private static SensorStateDelegate? _getState;
    private static FrequencyStateDelegate? _getFreqState;
    private static PowerEnergyDelegate? _getEnergy;

    /// <summary>温度初始化结果，写入诊断文件便于在掌机上排查</summary>
    public static string Status { get; private set; } = "未尝试";

    /// <summary>频率初始化结果，写入诊断文件</summary>
    public static string ClockStatus { get; private set; } = "未尝试";

    /// <summary>功率域初始化结果，写入诊断文件</summary>
    public static string PowerStatus { get; private set; } = "未尝试";

    /// <summary>最近一次能量计数器读数（诊断用：判断是计数器不走还是换算问题）</summary>
    public static string PowerDetail { get; private set; } = "";

    /// <summary>读取 GPU 温度（℃）；驱动不提供温度传感器时返回 NaN</summary>
    public static double Read()
    {
        lock (Gate)
        {
            EnsureReady();
            if (!_tempReady || _getState is null || _sensor == IntPtr.Zero) return double.NaN;
            try
            {
                if (_getState(_sensor, out var celsius) != 0) return double.NaN;
                return celsius is > 0 and < 150 ? celsius : double.NaN;
            }
            catch { return double.NaN; }
        }
    }

    /// <summary>读取 GPU 核心频率（MHz）；不可用返回 NaN</summary>
    public static double ReadClock()
    {
        lock (Gate)
        {
            EnsureReady();
            if (!_freqReady || _getFreqState is null || _freq == IntPtr.Zero) return double.NaN;
            try
            {
                var state = new FreqState
                {
                    Size = (uint)Marshal.SizeOf<FreqState>(),
                    Version = 0
                };
                if (_getFreqState(_freq, ref state) != 0) return double.NaN;
                return state.Actual is > 0 and < 5000 ? Math.Round(state.Actual) : double.NaN;
            }
            catch { return double.NaN; }
        }
    }

    /// <summary>读取 GPU 功耗（W）：由驱动功率域的能量计数器差分得到；不可用返回 NaN</summary>
    public static double ReadPower()
    {
        lock (Gate)
        {
            EnsureReady();
            if (_getEnergy is null || _powerDomain == IntPtr.Zero) return double.NaN;
            try
            {
                var counter = new EnergyCounter();
                if (_getEnergy(_powerDomain, ref counter) != 0)
                {
                    PowerDetail = "能量计数器读取失败";
                    return double.NaN;
                }
                // 微焦 / 微秒 = 瓦特；第一次采样只记基线
                var result = double.NaN;
                if (_lastEnergyTs != 0 && counter.Timestamp > _lastEnergyTs && counter.Energy >= _lastEnergy)
                    result = (double)(counter.Energy - _lastEnergy) / (counter.Timestamp - _lastEnergyTs);
                PowerDetail = $"energy={counter.Energy} ts={counter.Timestamp}"
                    + $" Δenergy={counter.Energy - _lastEnergy} Δts={counter.Timestamp - _lastEnergyTs} W={result:0.##}";
                _lastEnergy = counter.Energy;
                _lastEnergyTs = counter.Timestamp;
                return result is > 0 and < 500 ? Math.Round(result, 1) : double.NaN;
            }
            catch { return double.NaN; }
        }
    }

    private static T? GetProc<T>(string name) where T : Delegate
    {
        var p = GetProcAddress(_module, name);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static void EnsureReady()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            _module = LoadLibraryW("ControlLib.dll");
            if (_module == IntPtr.Zero)
            {
                Status = ClockStatus = PowerStatus = "未安装 Intel 显卡控制库";
                return;
            }

            var init = GetProc<InitDelegate>("ctlInit");
            var enumDevices = GetProc<EnumDevicesDelegate>("ctlEnumerateDevices");
            var enumSensors = GetProc<EnumSensorsDelegate>("ctlEnumTemperatureSensors");
            var getProperties = GetProc<SensorPropertiesDelegate>("ctlTemperatureGetProperties");
            _getState = GetProc<SensorStateDelegate>("ctlTemperatureGetState");
            var enumFreq = GetProc<EnumFrequencyDelegate>("ctlEnumFrequencyDomains");
            _getFreqState = GetProc<FrequencyStateDelegate>("ctlFrequencyGetState");
            var enumPower = GetProc<EnumPowerDelegate>("ctlEnumPowerDomains");
            _getEnergy = GetProc<PowerEnergyDelegate>("ctlPowerGetEnergyCounter");
            if (init is null || enumDevices is null)
            {
                Status = ClockStatus = PowerStatus = "ControlLib.dll 缺少基础接口";
                return;
            }
            if (enumSensors is null || getProperties is null || _getState is null)
                Status = "ControlLib.dll 缺少温度接口";
            if (enumFreq is null || _getFreqState is null)
                ClockStatus = "ControlLib.dll 缺少频率接口";

            var args = new InitArgs
            {
                Size = (uint)Marshal.SizeOf<InitArgs>(),
                Version = 0,
                AppVersion = 0x0001_0001,
                Flags = InitFlagUseLevelZero
            };
            var ret = init(ref args, out var api);
            if (ret != 0 || api == IntPtr.Zero)
            {
                // 老驱动可能不认 Level Zero 标志：退回 flags=0 再试一次（此时一般只有温度接口可用）
                args = new InitArgs
                {
                    Size = (uint)Marshal.SizeOf<InitArgs>(),
                    Version = 0,
                    AppVersion = 0x0001_0001
                };
                ret = init(ref args, out api);
                if (ret != 0 || api == IntPtr.Zero)
                {
                    var msg = "ctlInit 失败：" + Describe(ret);
                    Status = ClockStatus = PowerStatus = msg;
                    return;
                }
                ClockStatus = "ctlInit 未启用 Level Zero，频率不可用";
            }

            uint count = 0;
            var devRet = enumDevices(api, ref count, IntPtr.Zero);
            if (devRet != 0 || count == 0)
            {
                var msg = devRet != 0 ? "设备枚举失败：" + Describe(devRet) : "未发现 Intel 显卡设备";
                Status = ClockStatus = PowerStatus = msg;
                return;
            }
            if (count > 8) count = 8;

            var devices = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
            try
            {
                var deviceCount = count;
                if (enumDevices(api, ref deviceCount, devices) != 0)
                {
                    Status = ClockStatus = PowerStatus = "设备枚举失败";
                    return;
                }

                var bestRank = int.MaxValue;
                var tempEnumRet = 0;
                var freqEnumRet = 0;
                var bestDevice = IntPtr.Zero;
                for (var i = 0; i < deviceCount; i++)
                {
                    var device = Marshal.ReadIntPtr(devices, i * IntPtr.Size);
                    if (bestDevice == IntPtr.Zero) bestDevice = device;

                    // 频率域：取第一个能枚举到的（核显只有一个设备，取到即用）
                    if (!_freqReady && enumFreq is not null && _getFreqState is not null)
                    {
                        uint freqCount = 0;
                        freqEnumRet = enumFreq(device, ref freqCount, IntPtr.Zero);
                        if (freqEnumRet == 0 && freqCount > 0)
                        {
                            if (freqCount > 8) freqCount = 8;
                            var domains = Marshal.AllocHGlobal(IntPtr.Size * (int)freqCount);
                            try
                            {
                                var n = freqCount;
                                if (enumFreq(device, ref n, domains) == 0 && n > 0)
                                {
                                    _freq = Marshal.ReadIntPtr(domains, 0);
                                    _freqReady = _freq != IntPtr.Zero;
                                }
                            }
                            finally { Marshal.FreeHGlobal(domains); }
                        }
                    }

                    if (enumSensors is null) continue;
                    uint sensorCount = 0;
                    tempEnumRet = enumSensors(device, ref sensorCount, IntPtr.Zero);
                    if (tempEnumRet != 0 || sensorCount == 0) continue;
                    if (sensorCount > 8) sensorCount = 8;

                    var sensors = Marshal.AllocHGlobal(IntPtr.Size * (int)sensorCount);
                    try
                    {
                        var n = sensorCount;
                        if (enumSensors(device, ref n, sensors) != 0) continue;
                        for (var k = 0; k < n; k++)
                        {
                            var sensor = Marshal.ReadIntPtr(sensors, k * IntPtr.Size);
                            var props = new TempProperties
                            {
                                Size = (uint)Marshal.SizeOf<TempProperties>(),
                                Version = 0
                            };
                            var type = getProperties(sensor, ref props) == 0 ? props.Type : -1;
                            // 只认全局 / GPU 核心传感器；内存温度、最低温传感器不参与
                            var rank = type switch
                            {
                                SensorGlobal => 0,
                                SensorGpu => 1,
                                _ => 99
                            };
                            if (rank >= bestRank) continue;
                            bestRank = rank;
                            _sensor = sensor;
                        }
                    }
                    finally { Marshal.FreeHGlobal(sensors); }
                }

                _tempReady = _sensor != IntPtr.Zero;
                if (_tempReady)
                    Status = $"就绪（传感器类型 {bestRank}）";
                else if (enumSensors is not null && getProperties is not null && _getState is not null)
                    Status = "驱动未提供温度传感器：" + Describe(tempEnumRet);

                if (_freqReady)
                    ClockStatus = "就绪";
                else if (enumFreq is not null && _getFreqState is not null)
                    ClockStatus = "驱动未提供频率域：" + Describe(freqEnumRet);

                if (enumPower is not null && _getEnergy is not null)
                {
                    var powerRet = enumPower(bestDevice, ref _powerDomainCount, IntPtr.Zero);
                    if (powerRet != 0)
                    {
                        PowerStatus = "功率域枚举失败：" + Describe(powerRet);
                    }
                    else if (_powerDomainCount == 0)
                    {
                        PowerStatus = "驱动未提供功率域：" + Describe(powerRet);
                    }
                    else
                    {
                        var n = Math.Min(_powerDomainCount, 8u);
                        var domains = Marshal.AllocHGlobal(IntPtr.Size * (int)n);
                        try
                        {
                            if (enumPower(bestDevice, ref n, domains) == 0 && n > 0)
                                _powerDomain = Marshal.ReadIntPtr(domains, 0);
                        }
                        finally { Marshal.FreeHGlobal(domains); }
                        PowerStatus = _powerDomain != IntPtr.Zero
                            ? $"就绪（{_powerDomainCount} 个域）"
                            : "功率域句柄获取失败";
                    }
                }
                else
                {
                    PowerStatus = "ControlLib.dll 缺少功率域接口";
                }
            }
            finally { Marshal.FreeHGlobal(devices); }
        }
        catch (Exception ex)
        {
            Status = ClockStatus = PowerStatus = "IGCL 异常：" + ex.Message;
        }
    }

    /// <summary>把 IGCL 返回码翻译成可读原因，写进诊断方便在掌机上排查</summary>
    private static string Describe(int ret) => ret switch
    {
        0 => "成功但数量为 0",
        0x40000019 => "0x40000019 缺少 Level Zero 组件",
        0x40000009 => "0x40000009 不支持该结构版本",
        0x40000007 => "0x40000007 资源不可用",
        _ => $"0x{ret:X8}"
    };
}
