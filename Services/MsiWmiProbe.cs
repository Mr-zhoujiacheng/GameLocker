using System.Management;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// MSI 官方 ACPI WMI 接口（root\WMI 下的 MSI_ACPI）探查。
/// 微星的整机固件把 EC 里的传感器数据通过 ACPI WMI 方法（Get_Temperature / Get_Thermal / Get_Fan …）
/// 暴露给自家软件，比直接怼 0x62/0x66 端口更通用（由固件负责 EC 访问）。
/// 本类只做「按子功能号取 32 字节原始数据并写日志」，用于在 MSI Claw 上比对空载与游戏时的变化，
/// 找到 GPU 温度/功耗字段后再做正式解析。任何失败都只记录状态，不影响主流程。
/// </summary>
internal static class MsiWmiProbe
{
    private const string ScopePath = @"\\.\ROOT\WMI";
    private const string ClassName = "MSI_ACPI";

    /// <summary>要尝试的方法名（对应方法 ID 13/15/17/21：温度 / 热 / 风扇 / 功耗）</summary>
    private static readonly string[] CandidateMethods = ["Get_Temperature", "Get_Thermal", "Get_Fan", "Get_Power"];

    /// <summary>探查的子功能号（输入缓冲第 1 字节），不确定哪个子功能对应 GPU，全部采一遍</summary>
    private const int SubfeatureCount = 8;

    private static ManagementScope? _scope;
    private static ManagementClass? _msi;
    private static ManagementObject? _instance;
    private static ManagementClass? _package;

    private static readonly List<string> _methods = [];

    private static bool _readyTried;

    private static readonly object TempGate = new();
    private static double _gpuTemp = double.NaN;
    private static long _gpuTempTick;

    /// <summary>初始化结果（写入诊断文件）</summary>
    public static string Status { get; private set; } = "未尝试";

    /// <summary>最近一次方法调用失败的原因（写入诊断文件，便于定位 System.Management 的限制）</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>是否找到了可用的 MSI_ACPI 接口</summary>
    public static bool Available { get; private set; }

    /// <summary>可用方法名（写入日志）</summary>
    public static string MethodList => _methods.Count == 0 ? "无" : string.Join(",", _methods);

    public static bool EnsureReady()
    {
        // 浮层每秒都会取一次温度，失败时不要把 WMI 连接反复建一遍
        if (_readyTried) return Available;
        _readyTried = true;
        try
        {
            _scope = new ManagementScope(ScopePath);
            _scope.Connect();

            try
            {
                _msi = new ManagementClass(_scope, new ManagementPath(ClassName), null);
                _msi.Get(); // 类不存在时抛异常
            }
            catch (Exception ex)
            {
                // 顺便把这个机型实际有的 MSI 类列出来，方便以后适配
                Status = "缺少 " + ClassName + "（" + ex.Message + "）";
                try
                {
                    var names = new List<string>();
                    using var searcher = new ManagementObjectSearcher(_scope,
                        new ObjectQuery("SELECT * FROM meta_class WHERE __CLASS LIKE 'MSI[_]%'"));
                    foreach (var cls in searcher.Get())
                    {
                        names.Add(cls.ClassPath.ClassName);
                        if (names.Count >= 8) break; // 只要线索，别把日志刷爆
                    }
                    if (names.Count > 0) Status += " 已找到：" + string.Join(",", names);
                }
                catch { }
                _msi = null;
                return false;
            }

            // 类里真正实现了哪些方法
            var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MethodData method in _msi.Methods)
                defined.Add(method.Name);
            foreach (var name in CandidateMethods)
                if (defined.Contains(name)) _methods.Add(name);

            if (_methods.Count == 0)
            {
                Status = "MSI_ACPI 存在但缺少温度方法（已有：" + string.Join(",", defined) + "）";
                return false;
            }

            try { _package = new ManagementClass(_scope, new ManagementPath("Package_32"), null); }
            catch (Exception ex) { LastError = "Package_32:" + ex.Message; _package = null; }
            try { _instance = _msi.GetInstances().Cast<ManagementObject>().FirstOrDefault(); }
            catch { _instance = null; }

            Available = true;
            Status = "就绪";
            return true;
        }
        catch (Exception ex)
        {
            Status = "MSI WMI 不可用：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 采集一轮：每个方法 × 子功能号 取 32 字节原始数据，返回一行可写日志的文本。
    /// 输出格式 <c>方法[子功能]=OK:32字节HEX</c>，NG 表示固件返回失败标志；失败时带上原因。
    /// </summary>
    public static string Capture()
    {
        if (!Available || _msi is null) return "";
        var sb = new StringBuilder();
        var success = 0;
        foreach (var method in _methods)
        {
            for (var sub = 0; sub < SubfeatureCount; sub++)
            {
                var (bytes, error) = Invoke(method, (byte)sub);
                if (bytes is null)
                {
                    sb.Append(method).Append("[*]=失败(").Append(error).Append(")  ");
                    break; // 该方法不可用，换下一个
                }
                success++;
                sb.Append(method).Append('[').Append(sub).Append("]=")
                    .Append(bytes.Length > 0 && bytes[0] == 0 ? "NG:" : "OK:");
                foreach (var b in bytes) sb.Append(b.ToString("X2"));
                sb.Append("  ");
            }
        }
        return success == 0 && sb.Length == 0 ? "" : sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 读取 GPU 温度（℃）：取 Get_Thermal[3] 输出包的第 8 字节（固件给的热区读数）。
    /// 在 MSI Claw 上实测该字节与 EC 0x9E（封装温度）同源，Lunar Lake 的 GPU 与 CPU 同 die，
    /// 因此它是比"—"更接近真实 GPU 温度的值；接口不可用或数值不合理时返回 NaN。
    /// </summary>
    public static double ReadGpuTemperature()
    {
        lock (TempGate)
        {
            var now = Environment.TickCount64;
            // WMI 调用十几毫秒，浮层每秒取一次，缓存 2 秒足够
            if (now - _gpuTempTick < 2000) return _gpuTemp;
            _gpuTempTick = now;
            _gpuTemp = QueryGpuTemperature();
            return _gpuTemp;
        }
    }

    private static double QueryGpuTemperature()
    {
        if (!EnsureReady() || !_methods.Contains("Get_Thermal")) return double.NaN;
        var (bytes, _) = Invoke("Get_Thermal", 3);
        // 第 0 字节 = 固件成败标志（0=失败），第 7 字节 = 摄氏度
        if (bytes is not { Length: >= 8 } || bytes[0] == 0) return double.NaN;
        return bytes[7] is > 0 and < 130 ? bytes[7] : double.NaN;
    }

    /// <summary>调用一个 WMI 方法，参数为 32 字节包（第 1 字节 = 子功能号），返回输出包</summary>
    private static (byte[]? data, string error) Invoke(string method, byte subfeature)
    {
        // 依次尝试「实例调用」与「类调用」两种方式，哪种能用就用哪种
        var last = "未尝试";
        var tryInstance = _instance is not null;
        var modes = tryInstance ? 2 : 1;
        for (var mode = 0; mode < modes; mode++)
        {
            var useInstance = tryInstance && mode == 0;
            try
            {
                var input = new byte[32];
                input[0] = subfeature;
                var data = _package?.CreateInstance();
                if (data is null) return (null, LastError = "缺少 Package_32 类");
                data["Bytes"] = input;

                ManagementBaseObject? inParams = useInstance
                    ? _instance!.GetMethodParameters(method)
                    : _msi!.GetMethodParameters(method);
                if (inParams is null) { last = "无方法参数"; continue; }
                inParams["Data"] = data;

                using var outParams = useInstance
                    ? _instance!.InvokeMethod(method, inParams, null)
                    : _msi!.InvokeMethod(method, inParams, null);
                if (outParams is null) { last = "无返回值"; continue; }
                var outData = outParams["Data"] as ManagementBaseObject;
                var bytes = outData?["Bytes"] as byte[];
                if (bytes is null) { last = "输出无 Bytes"; continue; }
                return (bytes, "");
            }
            catch (Exception ex)
            {
                last = (useInstance ? "实例调用：" : "类调用：") + Compact(ex.Message);
            }
        }
        LastError = last;
        return (null, last);
    }

    private static string Compact(string message)
    {
        var text = message.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 120 ? text[..120] : text;
    }
}
