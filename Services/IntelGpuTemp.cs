using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// Intel 核显（Arc / Iris Xe）温度：走 Intel IGCL（ControlLib.dll，随显卡驱动安装）的传感器接口。
/// 实测要点：
/// - ctlInit 的结构里 Version 是"结构版本"，必须传 0；AppVersion 传 0x00010001（IGCL 1.1），
///   传 1 会被判为 UNSUPPORTED_VERSION（0x40000009）。
/// - 温度接口底层依赖 Level Zero 系统管理（sysman），驱动不带该组件时枚举失败，返回 NaN。
/// 初始化只尝试一次，失败后保持不可用，不影响每秒采样。
/// </summary>
internal static class IntelGpuTemp
{
    /// <summary>传感器类型：GLOBAL=0（整个设备最高温）、GPU=1（GPU 核心最高温）</summary>
    private const int SensorGlobal = 0;
    private const int SensorGpu = 1;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    private static readonly object Gate = new();
    private static bool _tried;
    private static bool _ready;
    private static IntPtr _module;
    private static IntPtr _sensor;
    private static SensorStateDelegate? _getState;

    /// <summary>初始化结果，写入诊断文件便于在掌机上排查</summary>
    public static string Status { get; private set; } = "未尝试";

    /// <summary>读取 GPU 温度（℃）；不可用返回 NaN</summary>
    public static double Read()
    {
        lock (Gate)
        {
            EnsureReady();
            if (!_ready || _getState is null) return double.NaN;
            try
            {
                if (_getState(_sensor, out var celsius) != 0) return double.NaN;
                return celsius is > 0 and < 150 ? celsius : double.NaN;
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
                Status = "未安装 Intel 显卡控制库";
                return;
            }

            var init = GetProc<InitDelegate>("ctlInit");
            var enumDevices = GetProc<EnumDevicesDelegate>("ctlEnumerateDevices");
            var enumSensors = GetProc<EnumSensorsDelegate>("ctlEnumTemperatureSensors");
            var getProperties = GetProc<SensorPropertiesDelegate>("ctlTemperatureGetProperties");
            _getState = GetProc<SensorStateDelegate>("ctlTemperatureGetState");
            if (init is null || enumDevices is null || enumSensors is null || getProperties is null || _getState is null)
            {
                Status = "ControlLib.dll 缺少温度接口";
                return;
            }

            var args = new InitArgs
            {
                Size = (uint)Marshal.SizeOf<InitArgs>(),
                Version = 0,
                AppVersion = 0x0001_0001
            };
            var ret = init(ref args, out var api);
            if (ret != 0 || api == IntPtr.Zero)
            {
                Status = $"ctlInit 失败 0x{ret:X8}";
                return;
            }

            uint count = 0;
            if (enumDevices(api, ref count, IntPtr.Zero) != 0 || count == 0)
            {
                Status = "未发现 Intel 显卡设备";
                return;
            }
            if (count > 8) count = 8;

            var devices = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
            try
            {
                var deviceCount = count;
                if (enumDevices(api, ref deviceCount, devices) != 0)
                {
                    Status = "设备枚举失败";
                    return;
                }

                var bestRank = int.MaxValue;
                for (var i = 0; i < deviceCount; i++)
                {
                    var device = Marshal.ReadIntPtr(devices, i * IntPtr.Size);
                    uint sensorCount = 0;
                    if (enumSensors(device, ref sensorCount, IntPtr.Zero) != 0 || sensorCount == 0) continue;
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
                                Version = 1
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

                if (_sensor == IntPtr.Zero)
                {
                    Status = "没有可用的 GPU 温度传感器";
                    return;
                }

                _ready = true;
                Status = $"就绪（传感器类型 {bestRank}）";
            }
            finally { Marshal.FreeHGlobal(devices); }
        }
        catch (Exception ex)
        {
            Status = "IGCL 异常：" + ex.Message;
        }
    }
}
