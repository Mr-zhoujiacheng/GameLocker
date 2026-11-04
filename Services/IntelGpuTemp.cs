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

    private const int FrequencyDomainGpu = 0;

    // Intel igcl_api.h: bool 为 1 字节；enum / int32_t 为 4 字节。
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct FreqProperties
    {
        public uint Size;
        public byte Version;
        public int Type;
        public byte CanControl;
        public double Min;
        public double Max;
    }

    // 官方 ctl_power_properties_t 没有 type / GPU 标识，不能按域数量推断来源。
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PowerProperties
    {
        public uint Size;
        public byte Version;
        public byte CanControl;
        public int DefaultLimit;
        public int MinLimit;
        public int MaxLimit;
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
    private delegate int FrequencyPropertiesDelegate(IntPtr domain, ref FreqProperties properties);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PowerPropertiesDelegate(IntPtr domain, ref PowerProperties properties);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FrequencyStateDelegate(IntPtr domain, ref FreqState state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPowerDelegate(IntPtr device, ref uint count, IntPtr domains);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct TelemetryItem
    {
        [FieldOffset(0)] public byte Supported;
        [FieldOffset(4)] public int Units;
        [FieldOffset(8)] public int Type;
        [FieldOffset(16)] public double Value;
    }

    // Intel igcl_api.h ctl_power_telemetry_t，MSVC x64 默认对齐验证。
    // 保留完整 native 大小，未声明的尾部仍留给驱动写入；Version=0 不请求扩展指标。
    [StructLayout(LayoutKind.Explicit, Size = 1024)]
    private struct PowerTelemetry
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public TelemetryItem Timestamp;
        [FieldOffset(32)] public TelemetryItem GpuEnergy;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PowerTelemetryDelegate(IntPtr device, ref PowerTelemetry telemetry);

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
    private static IntPtr _powerDevice;
    private static uint _powerDomainCount;
    private static bool _hasPowerBaseline;
    private static double _lastEnergy;
    private static double _lastEnergyTs;
    private static SensorStateDelegate? _getState;
    private static FrequencyStateDelegate? _getFreqState;
    private static PowerTelemetryDelegate? _getPowerTelemetry;

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
                var ret = _getState(_sensor, out var celsius);
                Status = ret == 0 ? $"最近温度读数={celsius} ℃" : "温度读取失败：" + Describe(ret);
                return ret == 0 && celsius is > 0 and < 150 ? celsius : double.NaN;
            }
            catch (Exception ex) { Status = "温度读取异常：" + ex.Message; return double.NaN; }
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
                var ret = _getFreqState(_freq, ref state);
                ClockStatus = ret == 0 ? $"最近核心频率={state.Actual} MHz" : "频率读取失败：" + Describe(ret);
                return ret == 0 && state.Actual is > 0 and < 5000 ? Math.Round(state.Actual) : double.NaN;
            }
            catch (Exception ex) { ClockStatus = "频率读取异常：" + ex.Message; return double.NaN; }
        }
    }

    /// <summary>读取 GPU 芯片功耗（W）：gpuEnergyCounter（J）/ timeStamp（s）差分；不可用返回 NaN</summary>
    public static double ReadPower()
    {
        lock (Gate)
        {
            EnsureReady();
            if (_getPowerTelemetry is null || _powerDevice == IntPtr.Zero) return double.NaN;
            try
            {
                var telemetry = new PowerTelemetry { Size = (uint)Marshal.SizeOf<PowerTelemetry>(), Version = 0 };
                var ret = _getPowerTelemetry(_powerDevice, ref telemetry);
                if (ret != 0)
                {
                    _hasPowerBaseline = false;
                    PowerDetail = "ctlPowerTelemetryGet 失败：" + Describe(ret);
                    return double.NaN;
                }
                return UpdatePower(telemetry);
            }
            catch (Exception ex)
            {
                _hasPowerBaseline = false;
                PowerDetail = "GPU 遥测异常：" + ex.Message;
                return double.NaN;
            }
        }
    }

    private static double UpdatePower(PowerTelemetry telemetry)
    {
        var energy = telemetry.GpuEnergy;
        var time = telemetry.Timestamp;
        // ctl_data_value_t 是 union，仅在 DOUBLE(9)、焦耳(6)/秒(7) 时解释为 double。
        if (energy.Supported == 0 || time.Supported == 0 || energy.Type != 9 || time.Type != 9
            || energy.Units != 6 || time.Units != 7
            || !double.IsFinite(energy.Value) || !double.IsFinite(time.Value) || energy.Value < 0 || time.Value < 0)
        {
            _hasPowerBaseline = false;
            PowerDetail = $"GPU 遥测不可用：energy(supported/type/unit)={energy.Supported}/{energy.Type}/{energy.Units}，time={time.Supported}/{time.Type}/{time.Units}，J={energy.Value} s={time.Value}";
            return double.NaN;
        }
        var deltaEnergy = energy.Value - _lastEnergy;
        var deltaTime = time.Value - _lastEnergyTs;
        var result = double.NaN;
        var reason = "首帧基线";
        if (_hasPowerBaseline)
        {
            if (deltaTime > 0 && deltaEnergy >= 0)
            {
                result = deltaEnergy / deltaTime;
                reason = "GPU 能量差分";
            }
            else reason = "时间戳未前进或计数器回退，重建基线";
        }
        _lastEnergy = energy.Value;
        _lastEnergyTs = time.Value;
        _hasPowerBaseline = true;
        PowerDetail = $"{reason}：gpuEnergy(J)={energy.Value} time(s)={time.Value} ΔJ={deltaEnergy} Δs={deltaTime} W={result:0.###}";
        return double.IsFinite(result) && result is >= 0 and < 500 ? Math.Round(result, 1) : double.NaN;
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
                var error = Marshal.GetLastWin32Error();
                Status = ClockStatus = PowerStatus = $"加载 ControlLib.dll 失败：Win32={error}（{new System.ComponentModel.Win32Exception(error).Message}）";
                return;
            }

            var init = GetProc<InitDelegate>("ctlInit");
            var enumDevices = GetProc<EnumDevicesDelegate>("ctlEnumerateDevices");
            var enumSensors = GetProc<EnumSensorsDelegate>("ctlEnumTemperatureSensors");
            var getProperties = GetProc<SensorPropertiesDelegate>("ctlTemperatureGetProperties");
            _getState = GetProc<SensorStateDelegate>("ctlTemperatureGetState");
            var enumFreq = GetProc<EnumFrequencyDelegate>("ctlEnumFrequencyDomains");
            _getFreqState = GetProc<FrequencyStateDelegate>("ctlFrequencyGetState");
            var getFreqProperties = GetProc<FrequencyPropertiesDelegate>("ctlFrequencyGetProperties");
            var enumPower = GetProc<EnumPowerDelegate>("ctlEnumPowerDomains");
            var getPowerProperties = GetProc<PowerPropertiesDelegate>("ctlPowerGetProperties");
            _getPowerTelemetry = GetProc<PowerTelemetryDelegate>("ctlPowerTelemetryGet");
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
            // 没有目标适配器的 LUID 映射时，只能安全使用唯一的 Intel 设备。
            if (count != 1)
            {
                Status = ClockStatus = PowerStatus = $"发现 {count} 个 Intel 设备，无 LUID 映射，拒绝混用遥测";
                return;
            }

            var devices = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
            try
            {
                var deviceCount = count;
                devRet = enumDevices(api, ref deviceCount, devices);
                if (devRet != 0 || deviceCount != 1)
                {
                    Status = ClockStatus = PowerStatus = $"设备枚举不可用（数量 {deviceCount}，返回码 0x{devRet:X8}），无 LUID 映射";
                    return;
                }

                var device = Marshal.ReadIntPtr(devices, 0);
                if (enumFreq is not null && getFreqProperties is not null && _getFreqState is not null)
                {
                    uint freqCount = 0;
                    var freqRet = enumFreq(device, ref freqCount, IntPtr.Zero);
                    var details = new List<string>();
                    uint gpuCount = 0;
                    if (freqRet == 0 && freqCount > 0)
                    {
                        var domains = Marshal.AllocHGlobal(checked(IntPtr.Size * (int)freqCount));
                        try
                        {
                            var n = freqCount;
                            freqRet = enumFreq(device, ref n, domains);
                            if (freqRet == 0 && n <= freqCount)
                            {
                                for (var k = 0; k < n; k++)
                                {
                                    var domain = Marshal.ReadIntPtr(domains, k * IntPtr.Size);
                                    var props = new FreqProperties { Size = (uint)Marshal.SizeOf<FreqProperties>(), Version = 0 };
                                    var propRet = getFreqProperties(domain, ref props);
                                    details.Add(propRet == 0 ? $"域{k}:type={props.Type}" : $"域{k}属性失败：{Describe(propRet)}");
                                    if (propRet != 0 || props.Type != FrequencyDomainGpu || domain == IntPtr.Zero) continue;
                                    gpuCount++;
                                    _freq = domain;
                                }
                            }
                            else details.Add($"句柄枚举失败：返回数量 {n}，0x{freqRet:X8}");
                        }
                        finally { Marshal.FreeHGlobal(domains); }
                    }
                    _freqReady = gpuCount == 1;
                    if (!_freqReady) _freq = IntPtr.Zero;
                    ClockStatus = $"{(_freqReady ? "就绪" : "无唯一可确认的 GPU 频率域")}（域数 {freqCount}，GPU 域数 {gpuCount}，枚举 0x{freqRet:X8}）；{string.Join("；", details)}";
                }
                else ClockStatus = "ControlLib.dll 缺少频率接口（含 ctlFrequencyGetProperties）";

                if (enumSensors is not null && getProperties is not null && _getState is not null)
                {
                    uint sensorCount = 0;
                    var tempRet = enumSensors(device, ref sensorCount, IntPtr.Zero);
                    var details = new List<string>();
                    var bestRank = int.MaxValue;
                    var selectedType = -1;
                    if (tempRet == 0 && sensorCount > 0)
                    {
                        var sensors = Marshal.AllocHGlobal(checked(IntPtr.Size * (int)sensorCount));
                        try
                        {
                            var n = sensorCount;
                            tempRet = enumSensors(device, ref n, sensors);
                            if (tempRet == 0 && n <= sensorCount)
                            {
                                for (var k = 0; k < n; k++)
                                {
                                    var sensor = Marshal.ReadIntPtr(sensors, k * IntPtr.Size);
                                    var props = new TempProperties { Size = (uint)Marshal.SizeOf<TempProperties>(), Version = 0 };
                                    var propRet = getProperties(sensor, ref props);
                                    details.Add(propRet == 0 ? $"传感器{k}:type={props.Type}" : $"传感器{k}属性失败：{Describe(propRet)}");
                                    if (propRet != 0 || sensor == IntPtr.Zero) continue;
                                    // 优先 GPU 核心最高温，其次设备全局最高温；不取内存或最低温。
                                    var rank = props.Type switch { SensorGpu => 0, SensorGlobal => 1, _ => 99 };
                                    if (rank == 99 || rank >= bestRank) continue;
                                    bestRank = rank;
                                    selectedType = props.Type;
                                    _sensor = sensor;
                                }
                            }
                            else details.Add($"句柄枚举失败：返回数量 {n}，0x{tempRet:X8}");
                        }
                        finally { Marshal.FreeHGlobal(sensors); }
                    }
                    _tempReady = _sensor != IntPtr.Zero;
                    Status = $"{(_tempReady ? $"就绪（传感器类型 {selectedType}）" : "无可用 GPU/全局温度传感器")}（传感器数 {sensorCount}，枚举 0x{tempRet:X8}）；{string.Join("；", details)}";
                }

                if (enumPower is not null && getPowerProperties is not null)
                {
                    var powerRet = enumPower(device, ref _powerDomainCount, IntPtr.Zero);
                    var details = new List<string>();
                    if (powerRet == 0 && _powerDomainCount > 0)
                    {
                        var domains = Marshal.AllocHGlobal(checked(IntPtr.Size * (int)_powerDomainCount));
                        try
                        {
                            var n = _powerDomainCount;
                            powerRet = enumPower(device, ref n, domains);
                            if (powerRet == 0 && n <= _powerDomainCount)
                            {
                                for (var k = 0; k < n; k++)
                                {
                                    var props = new PowerProperties { Size = (uint)Marshal.SizeOf<PowerProperties>(), Version = 0 };
                                    var propRet = getPowerProperties(Marshal.ReadIntPtr(domains, k * IntPtr.Size), ref props);
                                    details.Add(propRet == 0
                                        ? $"域{k}:canControl={props.CanControl},default/min/max(mW)={props.DefaultLimit}/{props.MinLimit}/{props.MaxLimit}"
                                        : $"域{k}属性失败：{Describe(propRet)}");
                                }
                            }
                            else details.Add($"句柄枚举失败：返回数量 {n}，0x{powerRet:X8}");
                        }
                        finally { Marshal.FreeHGlobal(domains); }
                    }
                    // ctl_power_properties_t 无域类型，唯一域也不能证明不是 CPU 整包功耗。
                    PowerStatus = $"功率域不用于 GPU 功耗（域数 {_powerDomainCount}，枚举 0x{powerRet:X8}）：ctl_power_properties_t 无域类型；{string.Join("；", details)}";
                }
                else PowerStatus = "ControlLib.dll 缺少功率域接口（含 ctlPowerGetProperties）";

                if (_getPowerTelemetry is not null)
                {
                    _powerDevice = device;
                    PowerStatus = "使用 ctlPowerTelemetryGet.gpuEnergyCounter（J）/timeStamp（s），支持性见 PowerDetail；" + PowerStatus;
                }
                else PowerStatus = "GPU 功耗不可用：缺少 ctlPowerTelemetryGet；" + PowerStatus;
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
