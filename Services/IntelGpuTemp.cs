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

    // igcl_api.h x64：设备标识为 Windows LUID；完整结构大小 320 字节。
    [StructLayout(LayoutKind.Explicit, Size = 320)]
    private struct DeviceProperties
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public IntPtr DeviceId;
        [FieldOffset(16)] public uint DeviceIdSize;
        [FieldOffset(64)] public uint VendorId;
        [FieldOffset(188)] public uint AdapterFlags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DevicePropertiesDelegate(IntPtr device, ref DeviceProperties properties);

    public static string? AdapterKey { get { lock (Gate) { EnsureReady(); return _adapterKey; } } }
    public static bool IsIntegrated { get { lock (Gate) { EnsureReady(); return _isIntegrated; } } }
    public static string IdentityStatus { get; private set; } = "未尝试";
    private static string? _adapterKey;
    private static bool _isIntegrated;

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

    // 官方 igcl_api.h 的 x64 默认对齐：IntType 为 bool + 填充 + 4 个 int，共 20 字节；
    // EnumType 含 uint64_t，令 property_info 联合体按 8 字节对齐并补齐到 24 字节。
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct FrameIntInfo
    {
        [FieldOffset(0)] public byte DefaultEnable;
        [FieldOffset(4)] public int Minimum;
        [FieldOffset(8)] public int Maximum;
        [FieldOffset(12)] public int Step;
        [FieldOffset(16)] public int DefaultValue;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FramePropertyInfo
    {
        [FieldOffset(0)] public FrameIntInfo IntType;
        [FieldOffset(0)] public ulong SupportedTypes;
        [FieldOffset(8)] public uint DefaultType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct FrameFeatureDetails
    {
        [FieldOffset(0)] public int FeatureType;
        [FieldOffset(4)] public int ValueType;
        [FieldOffset(8)] public FramePropertyInfo Value;
        [FieldOffset(32)] public int CustomValueSize;
        [FieldOffset(40)] public IntPtr CustomValue;
        [FieldOffset(48)] public byte PerAppSupport;
        [FieldOffset(56)] public long ConflictingFeatures;
        [FieldOffset(64)] public short FeatureMiscSupport;
        [FieldOffset(66)] public short Reserved;
        [FieldOffset(68)] public short Reserved1;
        [FieldOffset(70)] public short Reserved2;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FrameCapabilities
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public uint Count;
        [FieldOffset(16)] public IntPtr Details;
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct FrameValue
    {
        [FieldOffset(0)] public byte Enable;
        [FieldOffset(4)] public int Value;
        [FieldOffset(0)] public uint EnumValue;
    }

    [StructLayout(LayoutKind.Explicit, Size = 56)]
    private struct FrameGetSet
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public int FeatureType;
        [FieldOffset(16)] public IntPtr ApplicationName;
        [FieldOffset(24)] public sbyte ApplicationNameLength;
        [FieldOffset(25)] public byte Set;
        [FieldOffset(28)] public int ValueType;
        [FieldOffset(32)] public FrameValue Value;
        [FieldOffset(40)] public int CustomValueSize;
        [FieldOffset(48)] public IntPtr CustomValue;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FrameCapabilitiesDelegate(IntPtr device, ref FrameCapabilities capabilities);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FrameGetSetDelegate(IntPtr device, ref FrameGetSet feature);

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
    private static IntPtr _frameDevice;
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

    /// <summary>Intel 3D 限帧能力探测结果（功能类型/值类型/范围/应用模式），写入诊断文件</summary>
    public static string FrameLimitDetail { get; private set; } = "未探测";

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

    internal static FrameLimitService.State ReadFrameLimit()
    {
        lock (Gate)
        {
            try
            {
                var (range, getSet, _) = GetFrameLimitSupport();
                var value = AccessFrameFeature(getSet, 2, 2, false, default).Value;
                if (value.Enable != 0 && (value.Value < range.Minimum || value.Value > range.Maximum
                    || ((long)value.Value - range.Minimum) % range.Step != 0))
                    throw new InvalidOperationException("Intel 驱动返回了超出能力范围的限帧值");
                return new(true, "Intel IGCL", value.Enable == 0 ? 0 : value.Value,
                    "Intel 驱动全局限帧 · 仅 Intel 渲染的程序生效，独立游戏配置可覆盖；部分游戏需重启",
                    range.Minimum, range.Maximum, range.Step);
            }
            catch (Exception ex)
            {
                return new(false, "Intel IGCL", 0, "Intel 全局限帧不可用：" + ex.Message);
            }
        }
    }

    internal static void WriteFrameLimit(int fps)
    {
        lock (Gate)
        {
            var (range, getSet, hasMode) = GetFrameLimitSupport();
            if (fps != 0 && (fps < range.Minimum || fps > range.Maximum
                || ((long)fps - range.Minimum) % range.Step != 0))
                throw new ArgumentOutOfRangeException(nameof(fps),
                    $"Intel 锁帧范围为 {range.Minimum}–{range.Maximum} FPS，步长 {range.Step}，或关闭");

            // 保存原始 Enable 和 Value；关闭时保留原值，不修改其他 3D 设置。
            var old = AccessFrameFeature(getSet, 2, 2, false, default).Value;
            // 全局锁帧必须让驱动处于「全局」模式（2）；逐应用模式下写全局值不会生效。
            // 枚举值：0=NONE、1=PER_APP、2=GLOBAL
            var modeBefore = -1;
            if (hasMode && fps != 0)
            {
                try
                {
                    modeBefore = (int)AccessFrameFeature(getSet, 15, 4, false, default).Value.EnumValue;
                    if (modeBefore != 2)
                    {
                        AccessFrameFeature(getSet, 15, 4, true, new FrameValue { EnumValue = 2 });
                        var modeAfter = (int)AccessFrameFeature(getSet, 15, 4, false, default).Value.EnumValue;
                        FrameLimitDetail += $" 应用模式 {modeBefore}→{modeAfter}";
                        if (modeAfter != 2) modeBefore = -1;
                    }
                    else modeBefore = -1;
                }
                catch (Exception ex)
                {
                    FrameLimitDetail += " 切换全局模式失败：" + ex.Message;
                    modeBefore = -1;
                }
            }
            var desired = new FrameValue { Enable = fps == 0 ? (byte)0 : (byte)1, Value = fps == 0 ? old.Value : fps };
            try
            {
                AccessFrameFeature(getSet, 2, 2, true, desired);
                var actual = AccessFrameFeature(getSet, 2, 2, false, default).Value;
                if ((actual.Enable != 0) != (desired.Enable != 0)
                    || (desired.Enable != 0 && actual.Value != desired.Value))
                    throw new InvalidOperationException("Intel 驱动限帧读回值与请求不一致");
            }
            catch (Exception ex)
            {
                var recovery = "已尝试恢复原始状态";
                try
                {
                    AccessFrameFeature(getSet, 2, 2, true, old);
                    var restored = AccessFrameFeature(getSet, 2, 2, false, default).Value;
                    if (restored.Enable != old.Enable || restored.Value != old.Value)
                        recovery = "已尝试恢复，但原始状态读回不一致";
                    if (modeBefore >= 0)
                        AccessFrameFeature(getSet, 15, 4, true, new FrameValue { EnumValue = (uint)modeBefore });
                }
                catch (Exception restoreError) { recovery = "恢复原始状态失败：" + restoreError.Message; }
                throw new InvalidOperationException("Intel 限帧写入失败：" + ex.Message + "；" + recovery, ex);
            }
        }
    }

    private static (FrameIntInfo Range, FrameGetSetDelegate GetSet, bool HasMode) GetFrameLimitSupport()
    {
        EnsureReady();
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Intel IGCL 限帧仅支持 64 位进程");
        if (_frameDevice == IntPtr.Zero) throw new InvalidOperationException("无唯一可用的 Intel 设备：" + Status);
        var getCaps = GetProc<FrameCapabilitiesDelegate>("ctlGetSupported3DCapabilities")
            ?? throw new EntryPointNotFoundException("ControlLib.dll 缺少 ctlGetSupported3DCapabilities");
        var getSet = GetProc<FrameGetSetDelegate>("ctlGetSet3DFeature")
            ?? throw new EntryPointNotFoundException("ControlLib.dll 缺少 ctlGetSet3DFeature");
        var caps = new FrameCapabilities { Size = (uint)Marshal.SizeOf<FrameCapabilities>(), Version = 0 };
        var ret = getCaps(_frameDevice, ref caps);
        if (ret != 0) throw new InvalidOperationException("Intel 3D 能力计数读取失败：" + Describe(ret));
        if (caps.Count == 0) throw new NotSupportedException("驱动未提供 3D 功能");
        var count = caps.Count;
        var stride = Marshal.SizeOf<FrameFeatureDetails>();
        var bytes = checked((int)count * stride);
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            // 第二次调用前清零整个数组，避免把未初始化的自定义指针交给驱动。
            Marshal.Copy(new byte[bytes], 0, buffer, bytes);
            caps.Details = buffer;
            ret = getCaps(_frameDevice, ref caps);
            if (ret != 0) throw new InvalidOperationException("Intel 3D 能力读取失败：" + Describe(ret));
            if (caps.Count > count) throw new InvalidOperationException("Intel 3D 能力数量发生变化，请刷新后重试");
            FrameIntInfo? range = null;
            var hasMode = false;
            var entries = new List<string>();
            for (var i = 0; i < caps.Count; i++)
            {
                var feature = Marshal.PtrToStructure<FrameFeatureDetails>(IntPtr.Add(buffer, checked(i * stride)));
                entries.Add($"f{feature.FeatureType}/v{feature.ValueType}");
                if (feature.FeatureType == 2 && feature.ValueType == 2) range = feature.Value.IntType;
                if (feature.FeatureType == 15 && feature.ValueType == 4) hasMode = true;
            }
            FrameLimitDetail = $"设备={(_frameDevice == IntPtr.Zero ? "无" : "OK")} 能力=[{string.Join(",", entries)}]"
                + $" 命中f2/v2={(range is null ? "否" : "是")} 应用模式能力={hasMode}";
            if (range is { } detailRange)
                FrameLimitDetail += $" 范围={detailRange.Minimum}-{detailRange.Maximum}/{detailRange.Step}";
            if (range is not { } supported || supported.Minimum <= 0
                || supported.Maximum < supported.Minimum || supported.Step <= 0)
                throw new NotSupportedException("驱动未提供有效的整数限帧范围和正数步长");
            var appMode = -1;
            if (hasMode)
            {
                try { appMode = (int)AccessFrameFeature(getSet, 15, 4, false, default).Value.EnumValue; }
                catch (Exception ex) { FrameLimitDetail += " 读应用模式失败：" + ex.Message; }
            }
            FrameLimitDetail += $" 应用模式={appMode}";
            return (supported, getSet, hasMode);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static FrameGetSet AccessFrameFeature(FrameGetSetDelegate getSet, int feature, int valueType, bool set, FrameValue value)
    {
        // 官方要求空字符串表示全局；必须是非空指针，不能以 null 代替空 ANSI 字符串。
        var application = Marshal.StringToHGlobalAnsi(string.Empty);
        try
        {
            var request = new FrameGetSet
            {
                Size = (uint)Marshal.SizeOf<FrameGetSet>(), Version = 0, FeatureType = feature,
                ApplicationName = application, ApplicationNameLength = 0, Set = set ? (byte)1 : (byte)0,
                ValueType = valueType, Value = value
            };
            var ret = getSet(_frameDevice, ref request);
            if (ret != 0) throw new InvalidOperationException($"Intel 功能 {feature} {(set ? "写入" : "读取")}失败：" + Describe(ret));
            return request;
        }
        finally { Marshal.FreeHGlobal(application); }
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
            _module = LoadLibraryW(System.IO.Path.Combine(Environment.SystemDirectory, "ControlLib.dll"));
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
                _frameDevice = device;
                var getDeviceProperties = GetProc<DevicePropertiesDelegate>("ctlGetDeviceProperties");
                if (getDeviceProperties is not null)
                {
                    var luid = Marshal.AllocHGlobal(8);
                    try
                    {
                        Marshal.WriteInt64(luid, 0);
                        var properties = new DeviceProperties
                        {
                            Size = (uint)Marshal.SizeOf<DeviceProperties>(), Version = 0,
                            DeviceId = luid, DeviceIdSize = 8
                        };
                        var propertyResult = getDeviceProperties(device, ref properties);
                        var low = unchecked((uint)Marshal.ReadInt32(luid));
                        var high = unchecked((uint)Marshal.ReadInt32(luid, 4));
                        if (propertyResult == 0 && properties.VendorId == 0x8086
                            && properties.DeviceIdSize == 8 && (low != 0 || high != 0))
                        {
                            _adapterKey = FormattableString.Invariant($"luid_0x{high:x8}_0x{low:x8}_phys_0");
                            _isIntegrated = (properties.AdapterFlags & 1) != 0;
                            IdentityStatus = $"{_adapterKey}，核显={_isIntegrated}";
                        }
                        else IdentityStatus = $"设备身份不可用：0x{propertyResult:X8}，vendor=0x{properties.VendorId:X4}，size={properties.DeviceIdSize}";
                    }
                    finally { Marshal.FreeHGlobal(luid); }
                }
                else IdentityStatus = "缺少 ctlGetDeviceProperties";
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
