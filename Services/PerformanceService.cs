using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLocker.Services;

/// <summary>性能控制档位（仅 WidgetKind.Performance 使用）</summary>
public enum PerformanceLevel
{
    /// <summary>最佳能效</summary>
    Eco,
    /// <summary>平衡</summary>
    Balanced,
    /// <summary>最佳性能</summary>
    Performance
}

/// <summary>
/// 性能控制服务：通过 Windows overlay API 读写电源模式，不切换基础方案、不写入 EPP。
/// CPU 睿频与最高频率控制仍通过 powercfg.exe 修改当前方案。
/// 调用方用 Task.Run / RefreshCacheAsync 在后台执行；异常转成不可用状态或中文失败结果，不抛给 UI。
/// </summary>
public static class PerformanceService
{
    /// <summary>处理器子组 GUID（SUB_PROCESSOR）</summary>
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string PerfBoostMode = "be337238-0d82-4146-a960-4f3749d470c7";
    private const string ProcFreqMax = "75b0ae3f-bce0-45a7-8c89-c9611c25e100";
    private static readonly object CpuGate = new();

    public sealed record CpuSettingsState(bool BoostSupported, int BoostMode, bool FrequencySupported, int MaxFrequencyMhz);

    private static readonly object CpuCacheGate = new();
    private static CpuSettingsState _cachedCpu = new(false, 0, false, 0);

    /// <summary>上一次读取并缓存的 CPU 睿频/最高频率状态（磁贴绘制只读缓存，避免频繁调 powercfg）</summary>
    public static CpuSettingsState CachedCpu
    {
        get { lock (CpuCacheGate) return _cachedCpu; }
    }

    private static void SetCachedCpu(CpuSettingsState state)
    {
        lock (CpuCacheGate) _cachedCpu = state;
    }

    /// <summary>后台刷新 CPU 设置缓存（内部 Task.Run，不阻塞 UI 线程）</summary>
    public static async Task<CpuSettingsState> RefreshCpuCacheAsync()
    {
        var state = await Task.Run(GetCpuSettings).ConfigureAwait(false);
        SetCachedCpu(state);
        return state;
    }

    private sealed record CpuSetting(int Ac, int Dc, string Details);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>读取当前供电类型对应的 CPU 设置；最高频率 0 表示不限制。调用方应在后台线程执行。</summary>
    public static CpuSettingsState GetCpuSettings()
    {
        lock (CpuGate)
        {
            try
            {
                var scheme = GetActiveSchemeGuid();
                if (scheme.Length == 0)
                {
                    var empty = new CpuSettingsState(false, 0, false, 0);
                    SetCachedCpu(empty);
                    return empty;
                }
                var boost = QueryCpuSetting(scheme, PerfBoostMode);
                var frequency = QueryCpuSetting(scheme, ProcFreqMax);
                var dc = false;
                try { dc = GetSystemPowerStatus(out var status) && status.AcLineStatus == 0; }
                catch { /* 无法判断供电类型时按交流供电显示。 */ }
                var state = new CpuSettingsState(boost != null, boost == null ? 0 : dc ? boost.Dc : boost.Ac,
                    frequency != null, frequency == null ? 0 : dc ? frequency.Dc : frequency.Ac);
                SetCachedCpu(state);
                return state;
            }
            catch
            {
                var failed = new CpuSettingsState(false, 0, false, 0);
                SetCachedCpu(failed);
                return failed;
            }
        }
    }

    /// <summary>只修改当前方案的睿频模式或最高频率，同时写入并验证交流、直流值。</summary>
    public static (bool Ok, string Message) ApplyCpuSetting(bool boost, int value)
    {
        if (boost ? value != 0 && value != 2 : value != 0 && (value < 1000 || value > 5000 || value % 100 != 0))
            return (false, boost ? "睿频模式只允许 0 或 2，未修改设置" : "最高频率只允许 0（不限制）或 1000–5000 MHz（步长 100），未修改设置");

        lock (CpuGate)
        {
            var scheme = "";
            var setting = boost ? PerfBoostMode : ProcFreqMax;
            CpuSetting? original = null;
            var acAttempted = false;
            var dcAttempted = false;
            try
            {
                scheme = GetActiveSchemeGuid();
                if (scheme.Length == 0) return (false, "无法确定当前电源方案 GUID，未修改设置");
                original = QueryCpuSetting(scheme, setting);
                if (original == null) return (false, "当前设备不支持该 CPU 设置或无法读取交流/直流旧值，未修改设置");
                if (!IsCpuValueAllowed(original.Details, boost, value))
                    return (false, "该 CPU 设置的可用范围无法确认或不包含请求值，未修改设置");

                // 命令超时也可能已写入，因此从尝试写入起就纳入恢复范围。
                acAttempted = true;
                SetCpuValue(scheme, setting, false, value);
                dcAttempted = true;
                SetCpuValue(scheme, setting, true, value);
                ActivateCpuScheme(scheme);
                var actual = QueryCpuSetting(scheme, setting);
                if (actual == null || actual.Ac != value || actual.Dc != value)
                    throw new InvalidOperationException("交流/直流设置读回验证失败");
                return (true, boost ? $"已应用睿频模式 {value}（交流及直流）"
                    : value == 0 ? "已取消最高频率限制（交流及直流）" : $"已将最高频率设为 {value} MHz（交流及直流）");
            }
            catch (Exception ex)
            {
                if (original == null || !acAttempted)
                    return (false, "应用 CPU 设置失败，未修改设置：" + ex.Message);

                var failures = new List<string>();
                try { SetCpuValue(scheme, setting, false, original.Ac); }
                catch (Exception restore) { failures.Add("交流：" + restore.Message); }
                if (dcAttempted)
                {
                    try { SetCpuValue(scheme, setting, true, original.Dc); }
                    catch (Exception restore) { failures.Add("直流：" + restore.Message); }
                }
                try { ActivateCpuScheme(scheme); }
                catch (Exception restore) { failures.Add("激活方案：" + restore.Message); }
                try
                {
                    var restored = QueryCpuSetting(scheme, setting);
                    if (restored == null || restored.Ac != original.Ac || restored.Dc != original.Dc)
                        failures.Add("交流/直流旧值读回验证失败");
                }
                catch (Exception restore) { failures.Add("恢复验证：" + restore.Message); }
                return (false, "应用 CPU 设置失败：" + ex.Message + (failures.Count == 0
                    ? "；恢复成功，已恢复原交流/直流设置并重新激活方案"
                    : "；恢复失败或无法确认恢复成功：" + string.Join("；", failures)));
            }
        }
    }

    private static string GetActiveSchemeGuid()
    {
        var (code, output, _) = Run("powercfg.exe", "/getactivescheme");
        return code == 0 ? Regex.Match(output, GuidPattern).Value.ToLowerInvariant() : "";
    }

    private static CpuSetting? QueryCpuSetting(string scheme, string setting)
    {
        var (code, output, _) = Run("powercfg.exe", "/qh", scheme, SubProcessor, setting);
        if (code != 0) return null;
        var lines = output.Split('\n');
        var start = Array.FindIndex(lines, line => line.Contains(setting, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var details = string.Join("\n", lines.Skip(start + 1).TakeWhile(line => !Regex.IsMatch(line, GuidPattern)));
        if (!TryCpuIndex(details, @"当前交流(?:电源)?设置索引|Current\s+AC\s+Power\s+Setting\s+Index", out var ac)
            || !TryCpuIndex(details, @"当前直流(?:电源)?设置索引|Current\s+DC\s+Power\s+Setting\s+Index", out var dc))
            return null;
        return new(ac, dc, details);
    }

    private static bool TryCpuIndex(string text, string label, out int value)
    {
        var match = Regex.Match(text, @"(?:" + label + @")\s*[:：]\s*(?:0x([0-9a-f]+)|([0-9]+))",
            RegexOptions.IgnoreCase);
        value = 0;
        return match.Success && int.TryParse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
            match.Groups[1].Success ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && (value >= 0 || match.Groups[1].Value.Equals("ffffffff", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCpuValueAllowed(string details, bool boost, int value)
    {
        if (boost)
        {
            foreach (Match match in Regex.Matches(details,
                @"(?:可能的?设置索引|Possible\s+Setting\s+Index)\s*[:：]\s*(?:0x)?([0-9a-f]+)", RegexOptions.IgnoreCase))
            {
                if (int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var index)
                    && index == value) return true;
            }
            return false;
        }
        return TryCpuIndex(details, @"最小可能的?设置|Minimum\s+Possible\s+Setting", out var min)
            && TryCpuIndex(details, @"最大可能的?设置|Maximum\s+Possible\s+Setting", out var max)
            && TryCpuIndex(details, @"可能的?设置(?:增量|递增)|Possible\s+Settings?\s+Increment", out var increment)
            && increment > 0 && min <= value && (uint)value <= (uint)max && (value - min) % increment == 0;
    }

    private static void SetCpuValue(string scheme, string setting, bool dc, int value)
    {
        var (code, output, error) = Run("powercfg.exe", dc ? "/setdcvalueindex" : "/setacvalueindex",
            scheme, SubProcessor, setting, value.ToString(CultureInfo.InvariantCulture));
        if (code != 0) throw new InvalidOperationException(Reason(code, output, error));
    }

    private static void ActivateCpuScheme(string scheme)
    {
        var (code, output, error) = Run("powercfg.exe", "/setactive", scheme);
        if (code != 0) throw new InvalidOperationException(Reason(code, output, error));
    }

    private static readonly Guid EcoOverlayGuid = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BalancedOverlayGuid = Guid.Empty;
    private static readonly Guid PerformanceOverlayGuid = new("ded574b5-45a0-4f42-8737-46345c09c238");

    // Windows 标准基础电源方案：部分掌机忽略 overlay 写入，只能回退到切换基础方案
    private static readonly Guid PowerSaverScheme = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformanceScheme = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private static readonly object SchemeCacheGate = new();
    private static string _activeScheme = "";
    private static long _activeSchemeTicks;

    /// <summary>读取当前基础电源方案 GUID（缓存 3 秒，避免磁贴刷新频繁启动 powercfg）</summary>
    private static string GetActiveSchemeCached()
    {
        lock (SchemeCacheGate)
        {
            var now = Environment.TickCount64;
            if (_activeScheme.Length > 0 && now - _activeSchemeTicks < 3000) return _activeScheme;
            _activeScheme = GetActiveSchemeGuid();
            _activeSchemeTicks = now;
            return _activeScheme;
        }
    }

    private static void InvalidateActiveSchemeCache()
    {
        lock (SchemeCacheGate)
        {
            _activeScheme = "";
            _activeSchemeTicks = 0;
        }
    }

    /// <summary>按基础电源方案推断档位；无法识别（OEM 自定义方案）时返回 null。</summary>
    private static PerformanceLevel? ReadBaseSchemeLevel()
    {
        var scheme = GetActiveSchemeCached();
        if (scheme.Length == 0) return null;
        if (string.Equals(scheme, HighPerformanceScheme.ToString(), StringComparison.OrdinalIgnoreCase))
            return PerformanceLevel.Performance;
        if (string.Equals(scheme, PowerSaverScheme.ToString(), StringComparison.OrdinalIgnoreCase))
            return PerformanceLevel.Eco;
        if (string.Equals(scheme, BalancedScheme.ToString(), StringComparison.OrdinalIgnoreCase))
            return PerformanceLevel.Balanced;
        return null;
    }


    // 获取接口直接写入调用方的 GUID 缓冲区，不返回分配的指针，无需 LocalFree。
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    private static readonly object Gate = new();
    private static PerformanceState _cached = PerformanceState.Unknown;

    /// <summary>当前有效电源模式的快照（磁贴在 UI 线程只读缓存）</summary>
    public sealed record PerformanceState(PerformanceLevel Level, bool Available)
    {
        /// <summary>从未刷新过、不支持、未知模式或读取失败时的状态</summary>
        public static PerformanceState Unknown { get; } = new(PerformanceLevel.Balanced, false);

        public string Display => Available ? Level.Label() : "—";
    }

    /// <summary>上一次读取并缓存的电源状态（线程安全；UI 线程只读）</summary>
    public static PerformanceState Cached
    {
        get { lock (Gate) return _cached; }
    }

    private static void SetCached(PerformanceState state)
    {
        lock (Gate) _cached = state;
    }

    /// <summary>档位中文名</summary>
    public static string Label(this PerformanceLevel level) => level switch
    {
        PerformanceLevel.Eco => "最佳能效",
        PerformanceLevel.Balanced => "平衡",
        PerformanceLevel.Performance => "最佳性能",
        _ => "未知"
    };

    /// <summary>
    /// 后台刷新缓存（磁贴显示上次结果；首次运行/切回主页时调用）。
    /// 内部 Task.Run，不阻塞 UI 线程。
    /// </summary>
    public static async Task<PerformanceState> RefreshCacheAsync()
    {
        var state = await Task.Run(GetState).ConfigureAwait(false);
        SetCached(state);
        return state;
    }

    /// <summary>读取当前有效 overlay；overlay 为平衡或不可用时用基础电源方案兜底推断。</summary>
    public static PerformanceState GetState()
    {
        try
        {
            var ret = PowerGetEffectiveOverlayScheme(out var guid);
            if (ret == 0)
            {
                if (guid == EcoOverlayGuid) return new(PerformanceLevel.Eco, true);
                if (guid == PerformanceOverlayGuid) return new(PerformanceLevel.Performance, true);
            }
            // 平衡 overlay：部分掌机（如 MSI Claw）会忽略 overlay 写入，实际档位由基础方案决定
            var baseLevel = ReadBaseSchemeLevel();
            if (ret == 0 && guid == BalancedOverlayGuid)
                return new(baseLevel is PerformanceLevel.Performance or PerformanceLevel.Eco
                    ? baseLevel.Value
                    : PerformanceLevel.Balanced, true);
            return baseLevel is { } fallback
                ? new(fallback, true)
                : PerformanceState.Unknown;
        }
        catch
        {
            return PerformanceState.Unknown;
        }
    }

    /// <summary>读取失败的诊断信息：返回原始错误码和读到的 overlay GUID，便于定位 OEM 自定义档位</summary>
    public static string DescribeOverlay()
    {
        try
        {
            var ret = PowerGetEffectiveOverlayScheme(out var guid);
            return $"PowerGetEffectiveOverlayScheme ret={ret} guid={guid}";
        }
        catch (Exception ex)
        {
            return "PowerGetEffectiveOverlayScheme 异常：" + ex.Message;
        }
    }

    /// <summary>设置电源模式：优先写 Windows overlay，读回不一致时回退为切换基础电源方案。</summary>
    public static (bool Ok, string Message) ApplyLevel(PerformanceLevel level)
    {
        if (level != PerformanceLevel.Eco && level != PerformanceLevel.Balanced && level != PerformanceLevel.Performance)
            return (false, "未知性能档位，未修改设置");

        try
        {
            var current = GetState();
            SetCached(current);

            var guid = level switch
            {
                PerformanceLevel.Eco => EcoOverlayGuid,
                PerformanceLevel.Balanced => BalancedOverlayGuid,
                _ => PerformanceOverlayGuid
            };
            var code = PowerSetActiveOverlayScheme(guid);
            // 部分掌机的 overlay 写入由 OEM 服务异步接管，稍等再读回，避免刚写完读到旧值
            Thread.Sleep(180);
            var actual = GetState();
            SetCached(actual);
            if (code == 0 && actual.Level == level)
                return (true, $"已应用「{level.Label()}」电源模式");

            DebugLog.Write(code == 0
                ? $"[电源模式] 读回不一致：请求={level} 实际={actual.Level} {DescribeOverlay()} 基础方案={GetActiveSchemeCached()}"
                : $"[电源模式] 写入失败 code={code} 目标={guid} 基础方案={GetActiveSchemeCached()}");

            // 回退：MSI Claw 等掌机会忽略 Windows 电源模式滑块，改为切换基础电源方案
            var fallback = ApplyBaseScheme(level);
            DebugLog.Write($"[电源模式] 回退基础方案：{fallback.Ok} {fallback.Message}");
            var after = GetState();
            SetCached(after);
            if (fallback.Ok && after.Level == level)
                return (true, $"已应用「{level.Label()}」（此设备不支持 Windows 电源模式滑块，已切换电源方案：{fallback.Message}）");

            var detail = fallback.Ok
                ? $"切换电源方案后仍读回「{after.Level.Label()}」"
                : fallback.Message;
            return (false, (code == 0
                ? $"电源模式读回验证失败：请求「{level.Label()}」，当前有效模式为「{actual.Level.Label()}」；"
                : $"应用「{level.Label()}」失败：Windows 返回错误代码 {code}；") + detail);
        }
        catch (Exception ex)
        {
            SetCached(PerformanceState.Unknown);
            return (false, "应用性能档位失败（系统可能不支持电源模式 API）：" + ex.Message);
        }
    }

    /// <summary>把基础电源方案切到标准档位（overlay 被 OEM 忽略时的回退）。</summary>
    private static (bool Ok, string Message) ApplyBaseScheme(PerformanceLevel level)
    {
        Guid target;
        string name;
        if (level == PerformanceLevel.Balanced)
        {
            target = ResolveBalancedScheme();
            name = "平衡";
        }
        else if (level == PerformanceLevel.Eco)
        {
            target = PowerSaverScheme;
            name = "节能";
        }
        else
        {
            target = HighPerformanceScheme;
            name = "高性能";
        }
        if (target == Guid.Empty) return (false, "无法确定可用的平衡电源方案");
        DebugLog.Write($"[电源模式] 回退基础方案：{level} -> {target}（{name}）");
        var (code, output, error) = Run("powercfg.exe", "/setactive", target.ToString());
        if (code != 0) return (false, "切换电源方案失败：" + Reason(code, output, error));
        InvalidateActiveSchemeCache();
        var active = GetActiveSchemeCached();
        if (!string.Equals(active, target.ToString(), StringComparison.OrdinalIgnoreCase))
            return (false, $"切换电源方案后读回不一致：{active}");
        return (true, name);
    }

    /// <summary>启动时记录原始基础电源方案；标准方案不记录（避免把回退结果当成原始方案）。</summary>
    public static void RememberBaseScheme()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(SettingsService.Current.PowerModeBaseScheme)) return;
            var active = GetActiveSchemeGuid();
            if (active.Length == 0 || IsStandardScheme(active)) return;
            SettingsService.Update(s => s.PowerModeBaseScheme = active);
            DebugLog.Write("[电源模式] 记录原始基础方案：" + active);
        }
        catch (Exception ex)
        {
            DebugLog.Write("[电源模式] 记录原始基础方案失败：" + ex.Message);
        }
    }

    /// <summary>掌机上标准“平衡”方案可能不存在，优先用记录的/枚举到的 OEM 原始方案。</summary>
    private static Guid ResolveBalancedScheme()
    {
        var saved = SettingsService.Current.PowerModeBaseScheme;
        if (Guid.TryParse(saved, out var savedGuid) && !IsStandardScheme(saved)) return savedGuid;
        if (FindOemScheme() is { } oem)
        {
            SettingsService.Update(s => s.PowerModeBaseScheme = oem.ToString());
            return oem;
        }
        return BalancedScheme;
    }

    /// <summary>从 powercfg /list 里找第一个非 Windows 标准方案的 OEM 方案。</summary>
    private static Guid? FindOemScheme()
    {
        try
        {
            var (code, output, _) = Run("powercfg.exe", "/list");
            if (code != 0) return null;
            foreach (Match match in Regex.Matches(output, GuidPattern))
            {
                if (IsStandardScheme(match.Value)) continue;
                if (Guid.TryParse(match.Value, out var guid)) return guid;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("[电源模式] 枚举基础方案失败：" + ex.Message);
        }
        return null;
    }

    private static bool IsStandardScheme(string guid) =>
        string.Equals(guid, BalancedScheme.ToString(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(guid, HighPerformanceScheme.ToString(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(guid, PowerSaverScheme.ToString(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(guid, EcoOverlayGuid.ToString(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(guid, PerformanceOverlayGuid.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>把 powercfg 的原始错误压成一行中文提示（空输出时给退出码）</summary>
    private static string Reason(int code, string output, string error)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        var line = text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);
        return string.IsNullOrEmpty(line) ? $"powercfg 返回错误代码 {code}" : line;
    }

    private static readonly Encoding ConsoleEncoding = CreateConsoleEncoding();

    /// <summary>powercfg 控制台输出用系统 OEM 代码页（中文 Windows 为 936），否则方案名会乱码</summary>
    private static Encoding CreateConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>同步执行 powercfg 并读取输出（后台线程调用；超时/启动失败转成错误码，不抛）</summary>
    private static (int Code, string Output, string Error) Run(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return (-1, "", "无法启动 powercfg.exe");
            var timer = Stopwatch.StartNew();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var streams = Task.WhenAll(output, error);
            if (!process.WaitForExit(15000)
                || !streams.Wait((int)Math.Max(0, 15000 - timer.ElapsedMilliseconds)))
            {
                try { process.Kill(true); } catch { }
                return (-1, output.IsCompletedSuccessfully ? output.Result : "", "powercfg 执行超时");
            }
            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, "", "无法执行 powercfg：" + ex.Message);
        }
    }
}
