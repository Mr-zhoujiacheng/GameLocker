using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>OptiScaler 处理过程中的一条进度/结果提示（Error=true 时以警告样式显示；
/// Progress 非空时提示条下方显示小进度条，0..1）</summary>
public readonly record struct OptiScalerMessage(string Text, bool Error, double? Progress = null);

/// <summary>
/// 一次"适配性检查"的结果：交给 UI 弹窗展示（游戏名 + 是否支持 + 建议注入的 DLL 名 + wiki 备注）。
/// </summary>
public sealed class OptiScalerInspection
{
    /// <summary>游戏显示名（窗口标题优先，回退进程名）</summary>
    public string GameName { get; set; } = "";
    /// <summary>游戏进程名（不含扩展名）</summary>
    public string ExeName { get; set; } = "";
    /// <summary>游戏可执行文件完整路径</summary>
    public string ExePath { get; set; } = "";
    /// <summary>游戏所在目录（注入落盘位置）</summary>
    public string GameDir { get; set; } = "";
    /// <summary>状态徽标：✅ 支持 / ❌ 不支持 / 💥 有冲突 / ❔ 未收录</summary>
    public string Status { get; set; } = "❔";
    /// <summary>状态文案：支持 / 不支持 / 有冲突 / 未收录</summary>
    public string StatusText { get; set; } = "未收录";
    /// <summary>建议注入的代理 DLL 名（wiki 备注里提到的，否则默认 dxgi.dll）</summary>
    public string SuggestedDll { get; set; } = "dxgi.dll";
    /// <summary>wiki 兼容条目里的备注（可能为空）</summary>
    public string Note { get; set; } = "";
    /// <summary>wiki 兼容条目里给出的可用输入（DLSS / FSR3.1 …）</summary>
    public string Inputs { get; set; } = "";
}

/// <summary>
/// OptiScaler 小组件服务：检测当前正在运行的游戏 → 抓取 OptiScaler 官方 wiki 兼容列表，
/// 按游戏名模糊匹配给出适配性与建议注入的代理 DLL 名 → 用户确认后，关闭游戏、把 OptiScaler
/// 包内文件复制到游戏 exe 所在目录（OptiScaler.dll 改名为代理 DLL）并写入 ini 关键项 → 重新打开游戏。
/// 注入前会把被覆盖的同名文件备份到 %APPDATA%\GameLocker\optiscaler\backups，可一键还原。
/// 启动器是常驻置顶侧边栏，点击磁贴时游戏往往不在前台，故复用 TrainerService 的记录（LastExeName）。
/// </summary>
public static class OptiScalerService
{
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>OptiScaler 数据根目录（%APPDATA%\GameLocker\optiscaler）</summary>
    public static readonly string RootDir = Path.Combine(AppData, "GameLocker", "optiscaler");

    /// <summary>自动下载并解压的正式版包目录</summary>
    public static readonly string PackageDir = Path.Combine(RootDir, "package");

    /// <summary>测试版（Beta）包目录：与正式版分开缓存，切换渠道时不会互相覆盖</summary>
    private static readonly string BetaPackageDir = Path.Combine(RootDir, "package-beta");

    /// <summary>本地包解压目录：与自动下载缓存分开，避免本地包污染某个渠道的缓存</summary>
    private static readonly string LocalPackageDir = Path.Combine(RootDir, "local");

    /// <summary>OptiScaler 版本渠道（值, 显示名）：正式版取官方仓库 latest Release，
    /// 测试版取社区镜像的 pre 版本（OptiScaler-Betas）。ini 内容与用法完全一致，只是包更新更快。</summary>
    public static readonly (string Value, string Label)[] VersionChannelOptions =
    [
        ("stable", "正式版"),
        ("beta", "测试版")
    ];

    /// <summary>渠道 → 最近一次下载到的版本号（如 0.9.4 / 0.9.5-pre4），持久化在 channels.json</summary>
    private static readonly string ChannelVersionFile = Path.Combine(RootDir, "channels.json");

    private static Dictionary<string, string>? _channelVersions;

    private static Dictionary<string, string> ChannelVersions
    {
        get
        {
            if (_channelVersions is not null) return _channelVersions;
            try
            {
                _channelVersions = File.Exists(ChannelVersionFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ChannelVersionFile)) ?? []
                    : [];
            }
            catch (Exception ex)
            {
                DebugLog.Write($"OptiScaler: 版本缓存读取失败 {ex.Message}");
                _channelVersions = [];
            }
            return _channelVersions;
        }
    }

    /// <summary>版本渠道下拉项：已下载过的渠道在"正式版 / 测试版"后附加实际版本号后缀</summary>
    public static (string Value, string Label)[] GetVersionChannelOptions() =>
        [.. VersionChannelOptions.Select(o =>
            ChannelVersions.TryGetValue(o.Value, out var v) ? (o.Value, $"{o.Label} {v}") : o)];

    /// <summary>记住某渠道下载到的版本号（包可用的那一刻调用），供下拉框显示版本后缀</summary>
    private static void RememberChannelVersion(string channel, string tag)
    {
        var version = tag.TrimStart('v', 'V').Trim();
        if (version.Length == 0) return;
        try
        {
            ChannelVersions[channel] = version;
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(ChannelVersionFile, JsonSerializer.Serialize(ChannelVersions));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 版本缓存写入失败 {ex.Message}");
        }
    }

    /// <summary>注入前的备份目录（按游戏进程名分目录）</summary>
    public static readonly string BackupDir = Path.Combine(RootDir, "backups");

    private static readonly string CompatCacheFile = Path.Combine(RootDir, "compat.json");

    /// <summary>可手选的代理 DLL 名（"自动"=按 wiki 建议，空串表示自动）</summary>
    public static readonly (string Value, string Label)[] ProxyOptions =
    [
        ("", "自动"),
        ("dxgi.dll", "dxgi"),
        ("version.dll", "version"),
        ("winmm.dll", "winmm"),
        ("d3d12.dll", "d3d12"),
        ("dbghelp.dll", "dbghelp"),
        ("winhttp.dll", "winhttp"),
        ("wininet.dll", "wininet")
    ];

    /// <summary>自动（未手选）时默认注入的代理 DLL 名</summary>
    public const string DefaultProxy = "dxgi.dll";

    /// <summary>OptiScaler.ini 里 Dx12Upscaler 的可选值（值, 显示名）。
    /// 注意：ini 没有 fsr4 这个取值，FSR4 = ffx + [FSR] UpscalerIndex=0，故这里是"显示值"层面的合并项。</summary>
    public static readonly (string Value, string Label)[] UpscalerOptions =
    [
        ("auto", "自动"),
        ("dlss", "DLSS"),
        ("ffx", "FSR"),
        ("fsr4", "FSR4"),
        ("xess", "XeSS")
    ];

    /// <summary>FSR4 内部画质档（值, 显示名）：写 [FSR] 的 Fsr4Preset，0=原生AA … 5=超级性能</summary>
    public static readonly (string Value, string Label)[] Fsr4PresetOptions =
    [
        ("auto", "自动"),
        ("0", "原生 AA"),
        ("1", "质量"),
        ("2", "平衡"),
        ("3", "性能"),
        ("4", "DRS"),
        ("5", "超级性能")
    ];

    /// <summary>
    /// FSR4 的主 DLL 不在 OptiScaler 官方包里，需要另从社区仓库取（AMD 会按显卡世代给不同模型）。
    /// 值=(variant, 显示名)：int8=兼容卡（RDNA2/3 等，OptiScaler-Extras）、fp8=RDNA4（Optiscaler-Extras-FP8）。
    /// </summary>
    public static readonly (string Value, string Label)[] Fsr4FilesOptions =
    [
        ("auto", "不额外注入"),
        ("int8", "INT8 · 兼容卡"),
        ("fp8", "FP8 · RDNA4")
    ];

    /// <summary>FSR4 包缓存目录（按 int8 / fp8 分子目录）</summary>
    public static readonly string Fsr4Dir = Path.Combine(RootDir, "fsr4");

    /// <summary>FSR4 包里会被识别并注入的文件名（AMD 换过主 DLL 名，两个都认；其余为 FidelityFX 2.0 拆分包）</summary>
    private static readonly string[] Fsr4KnownNames =
    [
        "amdxcffx64.dll",                            // FSR4 主体（现行名）
        "amd_fidelityfx_upscaler_dx12.dll",          // FSR4 主体（旧名）
        "amd_fidelityfx_loader_dx12.dll",
        "amd_fidelityfx_framegeneration_dx12.dll",
        "amd_fidelityfx_denoiser_dx12.dll",
        "amd_fidelityfx_radiancecache_dx12.dll",
        "amd_fidelityfx_dx12.dll"                    // FSR3.1 单体 DLL（部分游戏硬编码此名 → 也作为替换目标）
    ];

    /// <summary>FSR4 附加包的来源仓库（variant → Release API / 显示名）</summary>
    private static readonly Dictionary<string, (string Api, string Label)> Fsr4Sources = new()
    {
        ["int8"] = ("https://api.github.com/repos/Optiscaler-Client/OptiScaler-Extras/releases/latest", "INT8"),
        ["fp8"] = ("https://api.github.com/repos/Optiscaler-Client/Optiscaler-Extras-FP8/releases/latest", "FP8")
    };

    /// <summary>OptiScaler.ini 里 FrameGen.FGInput 的可选值（值, 显示名），对齐 0.9.x 稳定版示例 ini。
    /// 注意：ini 没有 "optifg" 这个取值 —— OptiScaler 内置的 OptiFG 通路就是 FGInput=upscaler，
    /// 其细节由 [OptiFG] 段调节；xefg 是 nightly 0.10 才作为输入源加入的，稳定版不写。</summary>
    public static readonly (string Value, string Label)[] FgInputOptions =
    [
        ("auto", "自动"),
        ("nofg", "关闭"),
        ("dlssg", "DLSSG"),
        ("nvngxfg", "NvngxFG"),
        ("fsrfg", "FSRFG"),
        ("fsrfg30", "FSRFG 3.0"),
        ("upscaler", "OptiFG")
    ];

    /// <summary>OptiScaler.ini 里 FrameGen.FGOutput 的可选值（值, 显示名），对齐 0.9.x 稳定版示例 ini</summary>
    public static readonly (string Value, string Label)[] FgOutputOptions =
    [
        ("auto", "自动"),
        ("nofg", "关闭"),
        ("fsrfg", "FSRFG"),
        ("xefg", "XeFG"),
        ("nvngxfg", "NvngxFG"),
        ("dlssg", "DLSSG"),
        ("dlssgwithnvngx", "DLSSG+NvngxFG")
    ];

    /// <summary>帧生成倍数（值, 显示名）：写 [DLSSG]/[XeFG] 的 InterpolationCount，1=2X … 5=6X</summary>
    public static readonly (string Value, string Label)[] FgMultiplierOptions =
    [
        ("auto", "自动"),
        ("1", "2X"),
        ("2", "3X"),
        ("3", "4X"),
        ("4", "5X"),
        ("5", "6X")
    ];

    // 兼容列表来源：优先 raw markdown（结构干净），失败再回退渲染后的 HTML 页
    private const string WikiMdUrl = "https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/Compatibility-List.md";
    private const string WikiHtmlUrl = "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List";
    private const string ReleaseApiUrl = "https://api.github.com/repos/optiscaler/OptiScaler/releases/latest";
    private const string BetaReleaseApiUrl = "https://api.github.com/repos/Optiscaler-Client/OptiScaler-Betas/releases/latest";

    /// <summary>兼容列表缓存有效期（超过则尝试重新抓取）</summary>
    private static readonly TimeSpan CompatTtl = TimeSpan.FromDays(7);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        // 跟随 GitHub Release 的 302 跳转；带 UA 降低被拒概率
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        try { http.DefaultRequestHeaders.UserAgent.ParseAdd("GameLocker-OptiScaler/1.0"); }
        catch { /* UA 设置失败不影响使用 */ }
        return http;
    }

    // ---- 状态上报 ----

    /// <summary>处理过程中的提示（可能来自后台线程，订阅方自行 marshal 到 UI 线程）</summary>
    public static event Action<OptiScalerMessage>? StatusChanged;

    /// <summary>适配性检查就绪（后台线程）：带上被点击的磁贴项，交由 UI 层弹确认框询问是否注入；
    /// 不在这里弹窗，避免服务层引入 UI 依赖。</summary>
    public static event Action<LaunchItem, OptiScalerInspection>? InspectionReady;

    private static void Report(string text, bool error = false, double? progress = null) =>
        StatusChanged?.Invoke(new OptiScalerMessage(text, error, progress));

    /// <summary>下载进度 → 提示条文案（带 MB 数值）+ 小进度条取值；总长度未知时只报体积、不显示进度条</summary>
    private static void ReportDownload(string what, long got, long? total) =>
        Report(total is > 0
                ? $"正在下载{what}… {DownloadProgress.Mb(got)}/{DownloadProgress.Mb(total.Value)} MB（{got * 100 / total.Value}%）"
                : $"正在下载{what}… 已接收 {DownloadProgress.Mb(got)} MB",
            progress: total is > 0 ? (double)got / total.Value : null);

    // ---- 入口：点击磁贴 / A 键 ----

    private static bool _busy;

    /// <summary>点击/ A 键触发：检测当前游戏 → 抓兼容列表 → 弹窗询问是否注入</summary>
    public static void Activate(LaunchItem item)
    {
        if (_busy) { Report("正在处理中，请稍候…", true); return; }
        _busy = true;
        _ = ActivateAsync(item);
    }

    private static async Task ActivateAsync(LaunchItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(TrainerService.LastExeName))
            {
                Report("未检测到游戏进程，请先运行游戏再点此磁贴", true);
                return;
            }
            if (!TrainerService.IsGameRunning())
            {
                Report($"游戏 {TrainerService.CurrentGameName()} 已退出，请先运行游戏", true);
                return;
            }
            var exePath = TrainerService.CurrentGameExePath();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                Report("无法获取游戏可执行文件路径，请以普通权限运行游戏后重试", true);
                return;
            }

            Report("正在检查 OptiScaler 适配性…");
            var insp = await InspectAsync(exePath);
            DebugLog.Write($"OptiScaler: 检查 {insp.GameName}（{insp.ExeName}）→ {insp.StatusText}，建议 {insp.SuggestedDll}");
            InspectionReady?.Invoke(item, insp);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 检查异常 {ex.GetType().Name}: {ex.Message}\n{ex}");
            Report("检查适配性失败：" + ex.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>该组件的 FSR4 附加文件变体：仅"画质提升=FSR4"且指定了 int8/fp8 时才有值（空=不额外注入）</summary>
    private static string Fsr4VariantOf(LaunchItem item)
    {
        if (!string.Equals(item.OptiDx12Upscaler, "fsr4", StringComparison.OrdinalIgnoreCase)) return "";
        var v = (item.OptiFsr4Files ?? "").Trim().ToLowerInvariant();
        return v is "int8" or "fp8" ? v : "";
    }

    /// <summary>按配置取 FSR4 附加文件；不需要时返回 null 且不报错（用 needFsr4 区分"没取"和"取失败"）</summary>
    private static async Task<string?> EnsureFsr4ForItemAsync(LaunchItem item)
    {
        var variant = Fsr4VariantOf(item);
        if (variant.Length == 0) return null;
        Report($"正在准备 FSR4 文件（OptiScaler 官方包不含，需单独下载）…");
        return await EnsureFsr4Async(variant);
    }

    /// <summary>用户在确认弹窗里选择"注入"后调用：关闭游戏 → 注入 → 重新打开游戏（后台执行）</summary>
    public static void InjectConfirmed(LaunchItem item, OptiScalerInspection insp)
    {
        if (_busy) { Report("正在处理中，请稍候…", true); return; }
        _busy = true;
        _ = InjectAsync(item, insp);
    }

    private static async Task InjectAsync(LaunchItem item, OptiScalerInspection insp)
    {
        try
        {
            // 1) 取包（本地覆盖优先，否则下载最新 Release）
            var root = await EnsurePackageAsync(item);
            if (root is null)
            {
                Report("无法获取 OptiScaler 包（网络或解压失败）", true);
                return;
            }

            // 1b) FSR4 的主 DLL 不在官方包里：选了 FSR4 且指定要额外注入时，先把它取下来
            var fsr4Root = await EnsureFsr4ForItemAsync(item);
            if (fsr4Root is null && Fsr4VariantOf(item).Length > 0)
            {
                Report("FSR4 文件获取失败，已取消注入（可在编辑页把「FSR4 文件」改为不额外注入）", true);
                return;
            }

            // 2) 关闭游戏
            Report($"正在关闭游戏 {insp.GameName}…");
            if (!await CloseGameAsync(insp.ExeName))
            {
                Report("无法关闭游戏进程，已取消注入（请手动退出游戏后重试）", true);
                return;
            }

            // 3) 注入（复制文件 + 改名代理 DLL + FSR4 文件 + 写 ini）
            var proxy = string.IsNullOrWhiteSpace(item.OptiDllName) ? insp.SuggestedDll : item.OptiDllName!;
            Report($"正在注入 OptiScaler（{proxy}）…");
            if (!InjectFiles(root, insp.GameDir, proxy, item, out var error, fsr4Root))
            {
                Report(error.Length > 0 ? error : "注入失败", true);
                return;
            }

            // 4) 重新打开游戏
            Report("正在重新打开游戏…");
            await Task.Delay(600);
            if (TryLaunch(insp.ExePath) is { } err)
            {
                Report("注入成功，但无法重新打开游戏：" + err, true);
                return;
            }
            Report($"已注入 OptiScaler（{proxy}）并重新打开游戏");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 注入异常 {ex.GetType().Name}: {ex.Message}\n{ex}");
            Report("注入失败：" + ex.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>一键还原：优先还原当前游戏、否则还原最近一次注入的备份（同步，返回给 UI 展示的一句话结果）</summary>
    public static string Restore()
    {
        try
        {
            var dir = ChooseBackupDir();
            if (dir is null) return "没有可还原的 OptiScaler 备份";
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) return "备份缺少清单，无法还原";

            var m = JsonSerializer.Deserialize<OptiBackupManifest>(File.ReadAllText(manifestPath));
            if (m is null) return "备份清单损坏";

            var removed = 0;
            foreach (var rel in m.Created)
            {
                try
                {
                    var p = Path.Combine(m.GameDir, rel);
                    if (File.Exists(p)) { File.Delete(p); removed++; }
                }
                catch { /* 单个文件删不掉不影响其余 */ }
            }
            var restored = 0;
            foreach (var rel in m.BackedUp)
            {
                try
                {
                    var bk = Path.Combine(dir, "files", rel);
                    var p = Path.Combine(m.GameDir, rel);
                    if (!File.Exists(bk)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    File.Copy(bk, p, true);
                    restored++;
                }
                catch { /* 同上 */ }
            }
            try { Directory.Delete(dir, true); } catch { /* 备份目录删不掉无害 */ }

            DebugLog.Write($"OptiScaler: 还原 {m.Game}（删除 {removed} 个注入文件，复原 {restored} 个原文件）");
            return $"已还原 {m.Game} 的 OptiScaler 注入";
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 还原异常 {ex}");
            return "还原失败：" + ex.Message;
        }
    }

    /// <summary>是否有可还原的备份（供编辑页提示）</summary>
    public static bool HasBackup()
    {
        try
        {
            return Directory.Exists(BackupDir) &&
                   Directory.GetFiles(BackupDir, "manifest.json", SearchOption.AllDirectories).Length > 0;
        }
        catch { return false; }
    }

    // ---- 适配性检查 ----

    private static async Task<OptiScalerInspection> InspectAsync(string exePath)
    {
        var exeName = Path.GetFileNameWithoutExtension(exePath);
        var insp = new OptiScalerInspection
        {
            GameName = TrainerService.CurrentGameName(),
            ExeName = exeName,
            ExePath = exePath,
            GameDir = Path.GetDirectoryName(exePath) ?? ""
        };

        var entries = await LoadCompatibilityAsync();
        var keys = GameKeys(insp.GameName, exeName, exePath);
        var best = MatchBest(entries, keys, out var score);
        if (best is not null && score >= 40)
        {
            insp.Status = StatusBadge(best.Status);
            insp.StatusText = StatusLabel(best.Status);
            insp.Note = best.Note;
            insp.Inputs = best.Inputs;
            insp.SuggestedDll = SuggestDll(best) ?? DefaultProxy;
        }
        return insp;
    }

    /// <summary>匹配用的名字：窗口标题、进程名、Steam 安装目录名（去空、去重）。
    /// 中文标题分词后为空，只有安装目录名能提供英文名，等于自动完成"中文→英文"。</summary>
    private static List<string> GameKeys(string title, string exeName, string exePath)
    {
        var keys = new List<string> { title.Trim(), exeName.Trim() };
        var dir = InstallDirName(exePath);
        if (dir.Length > 0) keys.Add(dir);
        return keys.Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Steam 安装目录名（&lt;库&gt;\steamapps\common\&lt;目录&gt;）；非 Steam 游戏返回空串</summary>
    private static string InstallDirName(string exePath)
    {
        const string marker = @"\steamapps\common\";
        var idx = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        return exePath[(idx + marker.Length)..]
            .Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static string StatusBadge(string wiki)
    {
        if (wiki.Contains("✅")) return "✅";
        if (wiki.Contains("💀") || wiki.Contains("❌")) return "❌"; // 💀 为代理对，只能用字符串比较
        if (wiki.Contains("💥") || wiki.Contains("⚠")) return "💥";
        return "❔";
    }

    private static string StatusLabel(string wiki)
    {
        if (wiki.Contains("✅")) return "支持";
        if (wiki.Contains("💀") || wiki.Contains("❌")) return "不支持";
        if (wiki.Contains("💥") || wiki.Contains("⚠")) return "有冲突";
        return "未收录";
    }

    /// <summary>从 wiki 条目的备注/输入里找出建议注入的代理 DLL 名（出现任一白名单 DLL 即采用）</summary>
    private static string? SuggestDll(CompatEntry e)
    {
        var text = e.Note + " " + e.Inputs;
        foreach (var (value, _) in ProxyOptions)
        {
            if (value.Length == 0) continue;
            var stem = value[..^4]; // 去掉 ".dll"
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(stem)}\.dll\b", RegexOptions.IgnoreCase))
                return value;
        }
        return null;
    }

    // ---- 兼容列表：抓取 / 解析 / 缓存 / 匹配 ----

    private sealed class CompatEntry
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Inputs { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private sealed class CompatCacheDto
    {
        public DateTime Fetched { get; set; }
        public List<CompatEntry> Entries { get; set; } = [];
    }

    private static List<CompatEntry>? _entries;
    private static DateTime _entriesTime;

    /// <summary>加载兼容列表：内存缓存 → 磁盘缓存（未过期直接用）→ 联网抓取（成功写盘）。全部失败则返回空表。</summary>
    private static async Task<List<CompatEntry>> LoadCompatibilityAsync()
    {
        if (_entries is { Count: > 0 } && DateTime.Now - _entriesTime < CompatTtl) return _entries;

        // 磁盘缓存未过期：直接用
        var cached = TryLoadCache();
        if (cached is { } c && DateTime.Now - c.Fetched < CompatTtl && c.Entries.Count > 0)
        {
            _entries = c.Entries;
            _entriesTime = c.Fetched;
            return _entries;
        }

        // 联网抓取（raw markdown 优先，回退 HTML）
        try
        {
            var md = await TryGetStringAsync(WikiMdUrl);
            var list = md is not null ? ParseMarkdown(md) : [];
            if (list.Count == 0)
            {
                var html = await TryGetStringAsync(WikiHtmlUrl);
                if (html is not null) list = ParseHtml(html);
            }
            if (list.Count > 0)
            {
                DebugLog.Write($"OptiScaler: 兼容列表抓取成功，{list.Count} 条");
                _entries = list;
                _entriesTime = DateTime.Now;
                SaveCache(list);
                return list;
            }
            DebugLog.Write("OptiScaler: 兼容列表抓取成功但未解析到条目");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 兼容列表抓取失败 {ex.GetType().Name}: {ex.Message}");
        }

        // 抓取失败：退回过期磁盘缓存
        if (cached is { Entries.Count: > 0 })
        {
            DebugLog.Write("OptiScaler: 使用过期缓存");
            _entries = cached.Entries;
            _entriesTime = cached.Fetched;
            return _entries;
        }
        return [];
    }

    private static async Task<string?> TryGetStringAsync(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }
    }

    private static CompatCacheDto? TryLoadCache()
    {
        try
        {
            if (!File.Exists(CompatCacheFile)) return null;
            return JsonSerializer.Deserialize<CompatCacheDto>(File.ReadAllText(CompatCacheFile));
        }
        catch { return null; }
    }

    private static void SaveCache(List<CompatEntry> list)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(CompatCacheFile,
                JsonSerializer.Serialize(new CompatCacheDto { Fetched = DateTime.Now, Entries = list }));
        }
        catch { /* 落盘失败不影响本次使用 */ }
    }

    /// <summary>解析 raw markdown 表格：每行以 | 分隔，第二格是状态徽标</summary>
    private static List<CompatEntry> ParseMarkdown(string md)
    {
        var list = new List<CompatEntry>();
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] != '|') continue;
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 3) continue;
            if (IsTableHeader(cells[0])) continue;                  // 表头
            if (cells[0].All(c => c is '-' or ':' or ' ')) continue; // 分隔行
            var name = CleanMarkdownName(cells[0]);
            if (name.Length == 0) continue;
            list.Add(new CompatEntry
            {
                Name = name,
                Status = cells[1],
                Inputs = cells.Length > 2 ? cells[2] : "",
                Note = string.Join(" ", cells.Skip(3))
            });
        }
        return list;
    }

    /// <summary>解析渲染后的 wiki HTML 表格：按 &lt;tr&gt;/&lt;td&gt; 取值并去标签</summary>
    private static List<CompatEntry> ParseHtml(string html)
    {
        var list = new List<CompatEntry>();
        foreach (Match tr in Regex.Matches(html, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline))
        {
            var cells = Regex.Matches(tr.Groups[1].Value, "<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline)
                .Select(m => CleanHtml(m.Groups[1].Value)).ToArray();
            if (cells.Length < 3) continue;
            if (IsTableHeader(cells[0]) || cells[1].Contains("Compatibility", StringComparison.OrdinalIgnoreCase)) continue;
            if (cells[0].Length == 0 || cells[1].Length == 0) continue;
            list.Add(new CompatEntry
            {
                Name = cells[0],
                Status = cells[1],
                Inputs = cells[2],
                Note = string.Join(" ", cells.Skip(3))
            });
        }
        return list;
    }

    /// <summary>是否为兼容列表的表头单元格（"Game" / "GAME NAME"；不误伤 "Game of Thrones" 这类游戏名）</summary>
    private static bool IsTableHeader(string cell) =>
        cell.Equals("Game", StringComparison.OrdinalIgnoreCase) ||
        cell.Equals("GAME NAME", StringComparison.OrdinalIgnoreCase);

    /// <summary>去掉 markdown 链接只留显示名，并清理强调符/反引号</summary>
    private static string CleanMarkdownName(string cell)
    {
        var s = Regex.Replace(cell, @"\[([^\]]*)\]\([^)]*\)", "$1");
        return s.Replace("**", "").Replace("`", "").Trim();
    }

    /// <summary>去 HTML 标签并解码实体，压缩空白</summary>
    private static string CleanHtml(string cell)
    {
        var s = Regex.Replace(cell, "<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>
    /// 逐个候选打分取最优：归一化与任一名字相等 100 / 双向包含 60 / 命中词元 ×20，候选词元多于游戏词元时按超出数降权。
    /// 最高分低于 40 视为未匹配。
    /// </summary>
    private static CompatEntry? MatchBest(List<CompatEntry> entries, IReadOnlyList<string> keys, out int bestScore)
    {
        var tokens = new HashSet<string>();
        var norms = new List<string>();
        foreach (var k in keys)
        {
            foreach (var t in Tokens(k)) tokens.Add(t);
            var n = Norm(k);
            if (n.Length > 0) norms.Add(n);
        }

        CompatEntry? best = null;
        bestScore = 0;
        foreach (var e in entries)
        {
            var norm = Norm(e.Name);
            if (norm.Length < 4) continue;
            var toks = Tokens(e.Name);
            if (toks.Count == 0) continue;

            var score = 0;
            if (norms.Contains(norm)) score += 100;
            if (norms.Any(n => n.Contains(norm) || norm.Contains(n))) score += 60;
            score += toks.Count(tokens.Contains) * 20;
            score -= Math.Max(0, toks.Count - tokens.Count) * 10;

            if (score > bestScore) { bestScore = score; best = e; }
        }
        return bestScore >= 40 ? best : null;
    }

    /// <summary>归一化：仅保留字母数字并转小写</summary>
    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>切词元：按非字母数字切分，剔除长度 1 与常见冠词</summary>
    private static List<string> Tokens(string s) =>
        s.ToLowerInvariant()
         .Split([' ', '-', '_', ':', '.', '™', '®', '©', '!', '?', ',', '\'', '"', '(', ')', '[', ']', '/', '\\', '+', '&'],
                StringSplitOptions.RemoveEmptyEntries)
         .Where(t => t.Length >= 2 && t is not ("the" or "of" or "and"))
         .ToList();

    // ---- 取包：本地覆盖 / 自动下载最新 Release ----

    /// <summary>该项是否选了"测试版"渠道（默认/空值=正式版）</summary>
    private static bool IsBeta(LaunchItem item) => Blank(item.OptiChannel) == "beta";

    /// <summary>取得解压后的 OptiScaler 包根目录（含 OptiScaler.dll）；失败返回 null</summary>
    private static async Task<string?> EnsurePackageAsync(LaunchItem item)
    {
        // 本地包覆盖
        var local = item.OptiLocalPackage;
        if (!string.IsNullOrWhiteSpace(local))
        {
            var localDir = await MaterializeLocalAsync(local!);
            if (localDir is not null) return localDir;
            Report("本地 OptiScaler 包不可用，改用自动下载", true);
        }

        // 按渠道分开缓存：正式版/测试版各自的包目录，切换渠道时不会复用另一渠道的包
        var beta = IsBeta(item);
        var dir = beta ? BetaPackageDir : PackageDir;
        if (File.Exists(Path.Combine(dir, "OptiScaler.dll"))) return dir;

        // 查询最新 Release
        Report($"正在获取 OptiScaler{(beta ? " 测试版" : "")}版本信息…");
        var asset = await GetLatestAssetAsync(beta ? BetaReleaseApiUrl : ReleaseApiUrl);
        if (asset is null)
        {
            Report($"无法获取 OptiScaler{(beta ? " 测试版" : "")}版本（GitHub 访问失败）", true);
            return null;
        }

        // 下载到本地临时文件
        var tmp = Path.Combine(RootDir, asset.Value.Name);
        Report($"正在下载 OptiScaler{(beta ? " 测试版" : "")}（{asset.Value.Name}，约 {asset.Value.Size / 1048576}MB）…", progress: 0);
        Directory.CreateDirectory(RootDir);
        try
        {
            using var resp = await Http.GetAsync(asset.Value.Url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) { Report("OptiScaler 下载失败", true); return null; }
            await using var net = await resp.Content.ReadAsStreamAsync();
            await using var fs = File.Create(tmp);
            await DownloadProgress.CopyAsync(net, resp.Content.Headers.ContentLength, fs,
                (got, total) => ReportDownload("OptiScaler", got, total));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 下载异常 {ex.Message}");
            Report("OptiScaler 下载失败，请检查网络后重试", true);
            return null;
        }

        // 解压
        Report("正在解压 OptiScaler…");
        var root = await ExtractAsync(tmp, dir);
        try { File.Delete(tmp); } catch { /* 临时包删不掉无害 */ }
        if (root is null)
        {
            Report("OptiScaler 包解压失败（需要系统 tar.exe；可改在编辑页指定本地已解压目录）", true);
            return null;
        }
        // 包可用了：记下这个渠道的实际版本号，下次进编辑页时在"正式版 / 测试版"后显示
        RememberChannelVersion(beta ? "beta" : "stable", asset.Value.Tag);
        return root;
    }

    /// <summary>查询 GitHub Release 最新资产（名称/地址/大小 + Release 版本号）；失败返回 null</summary>
    private static async Task<(string Name, string Url, long Size, string Tag)?> GetLatestAssetAsync(string apiUrl = ReleaseApiUrl)
    {
        try
        {
            using var resp = await Http.GetAsync(apiUrl);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0L;
                if (name.Length > 0 && url.Length > 0) return (name, url, size, tag); // 取第一个资产（通常是唯一的主包）
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: Release 查询异常 {ex.Message}");
        }
        return null;
    }

    /// <summary>把本地包（目录 / zip / 7z）物化成含 OptiScaler.dll 的目录；失败返回 null</summary>
    private static async Task<string?> MaterializeLocalAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var root = FindPackageRoot(path);
                if (root is not null) return root;
                return null;
            }
            if (!File.Exists(path)) return null;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".zip" or ".7z")) return null;
            Report("正在解压本地 OptiScaler 包…");
            return await ExtractAsync(path, LocalPackageDir);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 本地包处理异常 {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 取 FSR4 附加文件（OptiScaler 官方包里没有）：已缓存则直接用，否则拉对应社区仓库的最新 Release
    /// 并解压，返回含 FSR4 DLL 的目录；失败返回 null。
    /// </summary>
    private static async Task<string?> EnsureFsr4Async(string variant)
    {
        if (!Fsr4Sources.TryGetValue(variant, out var src)) return null;

        var dir = Path.Combine(Fsr4Dir, variant);
        if (FindRootContaining(dir, Fsr4KnownNames) is { } cached)
        {
            DebugLog.Write($"OptiScaler: 复用已缓存的 FSR4 {src.Label} 文件（{cached}）");
            return cached;
        }

        Report($"正在获取 FSR4 {src.Label} 版本信息…");
        var asset = await GetLatestAssetAsync(src.Api);
        if (asset is null)
        {
            Report($"无法获取 FSR4 {src.Label} 版本（GitHub 访问失败）", true);
            return null;
        }

        Directory.CreateDirectory(Fsr4Dir);
        var tmp = Path.Combine(Fsr4Dir, asset.Value.Name);
        Report($"正在下载 FSR4 {src.Label}（{asset.Value.Name}，约 {asset.Value.Size / 1048576}MB）…", progress: 0);
        try
        {
            using var resp = await Http.GetAsync(asset.Value.Url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) { Report("FSR4 下载失败", true); return null; }
            await using var net = await resp.Content.ReadAsStreamAsync();
            await using var fs = File.Create(tmp);
            await DownloadProgress.CopyAsync(net, resp.Content.Headers.ContentLength, fs,
                (got, total) => ReportDownload($"FSR4 {src.Label}", got, total));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: FSR4 下载异常 {ex.Message}");
            Report("FSR4 下载失败，请检查网络后重试", true);
            return null;
        }

        Report("正在解压 FSR4…");
        var root = await ExtractAsync(tmp, dir, Fsr4KnownNames);
        try { File.Delete(tmp); } catch { /* 临时包删不掉无害 */ }
        if (root is null)
        {
            Report($"FSR4 {src.Label} 包解压失败（需要系统 tar.exe；可改设为不额外注入）", true);
            return null;
        }
        return root;
    }

    /// <summary>解压 zip / 7z 到 destDir，并返回其中含指定标志文件的目录；失败返回 null</summary>
    private static async Task<string?> ExtractAsync(string archive, string destDir, string[]? markers = null)
    {
        markers ??= ["OptiScaler.dll"];
        try
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
        }
        catch { /* 老目录删不掉：解压会覆盖同名文件 */ }
        Directory.CreateDirectory(destDir);

        var ext = Path.GetExtension(archive).ToLowerInvariant();
        if (ext == ".zip")
        {
            try { ZipFile.ExtractToDirectory(archive, destDir, true); }
            catch (Exception ex)
            {
                DebugLog.Write($"OptiScaler: zip 解压失败 {ex.Message}");
                return null;
            }
        }
        else if (ext == ".7z")
        {
            if (!await Task.Run(() => RunBsdtar(archive, destDir))) return null;
        }
        else return null;

        return FindRootContaining(destDir, markers);
    }

    /// <summary>用系统自带 bsdtar（libarchive，支持 7z）解压</summary>
    private static bool RunBsdtar(string archive, string destDir)
    {
        try
        {
            var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (!File.Exists(tar)) return false;
            var psi = new ProcessStartInfo(tar)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-xf");
            psi.ArgumentList.Add(archive);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(destDir);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: tar 解压异常 {ex.Message}");
            return false;
        }
    }

    /// <summary>广度优先找到含 OptiScaler.dll 的目录（包可能带一层顶层文件夹）</summary>
    private static string? FindPackageRoot(string dir) => FindRootContaining(dir, ["OptiScaler.dll"]);

    /// <summary>广度优先找到含任一指定文件名的目录（包可能带一层顶层文件夹）</summary>
    private static string? FindRootContaining(string dir, string[] names)
    {
        if (!Directory.Exists(dir)) return null;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((dir, 0));
        while (queue.Count > 0)
        {
            var (d, depth) = queue.Dequeue();
            foreach (var name in names)
            {
                if (File.Exists(Path.Combine(d, name))) return d;
            }
            if (depth >= 3) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(d))
                    queue.Enqueue((sub, depth + 1));
            }
            catch { /* 目录读不到：跳过 */ }
        }
        return null;
    }

    /// <summary>在目录里递归找指定文件名的文件（同名可能被放在子目录）</summary>
    private static string? FindFileNamed(string dir, string name)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
            }
        }
        catch { /* 读不到就当没有 */ }
        return null;
    }

    // ---- 注入 / 备份 ----

    private sealed class OptiBackupManifest
    {
        public string Game { get; set; } = "";
        public string GameDir { get; set; } = "";
        public string ExeName { get; set; } = "";
        public string Proxy { get; set; } = "";
        public DateTime Time { get; set; }
        /// <summary>本次注入新建的文件（还原时删除）</summary>
        public List<string> Created { get; set; } = [];
        /// <summary>本次注入覆盖的原文件（还原时从备份复制回来）</summary>
        public List<string> BackedUp { get; set; } = [];
    }

    /// <summary>
    /// 把包内文件复制到游戏目录（OptiScaler.dll 改名为代理 DLL）；fsr4Root 非空时再补入 FSR4 附加文件。
    /// 覆盖前统一备份，最后写入 ini 关键项。
    /// </summary>
    private static bool InjectFiles(
        string pkgRoot, string gameDir, string proxy, LaunchItem item, out string error, string? fsr4Root = null)
    {
        error = "";
        try
        {
            if (!File.Exists(Path.Combine(pkgRoot, "OptiScaler.dll")))
            {
                error = "OptiScaler 包缺少 OptiScaler.dll";
                return false;
            }
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                error = "游戏目录不存在，无法注入";
                return false;
            }
            var files = Directory.GetFiles(pkgRoot, "*", SearchOption.AllDirectories);
            if (files.Length == 0) { error = "OptiScaler 包为空"; return false; }

            // 备份目录按游戏进程名归类；每次注入只保留最新一份（可还原）
            var key = Sanitize(string.IsNullOrEmpty(TrainerService.LastExeName) ? "game" : TrainerService.LastExeName);
            var bdir = Path.Combine(BackupDir, key);
            try { if (Directory.Exists(bdir)) Directory.Delete(bdir, true); } catch { /* 旧备份删不掉：随后会覆盖 */ }
            Directory.CreateDirectory(Path.Combine(bdir, "files"));

            var manifest = new OptiBackupManifest
            {
                Game = TrainerService.CurrentGameName(),
                GameDir = gameDir,
                ExeName = key,
                Proxy = proxy,
                Time = DateTime.Now
            };

            foreach (var src in files)
            {
                var rel = Path.GetRelativePath(pkgRoot, src);
                var fileName = Path.GetFileName(src);
                if (ShouldSkipRootFile(rel, fileName)) continue;

                // OptiScaler.dll 改名成代理 DLL 放根目录；其余文件保持原相对路径一并拷入
                var destRel = fileName.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? proxy : rel;
                var dest = Path.Combine(gameDir, destRel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                if (File.Exists(dest))
                {
                    var bk = Path.Combine(bdir, "files", destRel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bk)!);
                    File.Copy(dest, bk, true);
                    manifest.BackedUp.Add(destRel);
                }
                else
                {
                    manifest.Created.Add(destRel);
                }
                File.Copy(src, dest, true);
            }

            // FSR4 附加文件：官方包不含，按已知文件名补进游戏根目录（游戏硬编码哪个名字就用哪个）
            if (fsr4Root is not null)
            {
                foreach (var name in Fsr4KnownNames)
                {
                    var src = FindFileNamed(fsr4Root, name);
                    if (src is null) continue;
                    var dest = Path.Combine(gameDir, name);
                    if (File.Exists(dest))
                    {
                        var bk = Path.Combine(bdir, "files", name);
                        Directory.CreateDirectory(Path.GetDirectoryName(bk)!);
                        File.Copy(dest, bk, true);
                        manifest.BackedUp.Add(name);
                    }
                    else
                    {
                        manifest.Created.Add(name);
                    }
                    File.Copy(src, dest, true);
                    DebugLog.Write($"OptiScaler: 补入 FSR4 文件 {name}");
                }
            }

            // 写入 ini 关键项（Dx12Upscaler / FSR4 / 帧生成开关、输入输出、倍数），不改其余内容
            PatchIni(Path.Combine(gameDir, "OptiScaler.ini"), item);

            File.WriteAllText(Path.Combine(bdir, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            DebugLog.Write($"OptiScaler: 注入 {manifest.Created.Count} 新文件 / {manifest.BackedUp.Count} 覆盖文件 → {gameDir}（{proxy}）");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 注入文件异常 {ex}");
            error = "注入失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>跳过根目录下的安装脚本与说明文本（保留 Licenses 等子目录里的授权文件）</summary>
    private static bool ShouldSkipRootFile(string relPath, string fileName)
    {
        if (relPath.Contains('\\') || relPath.Contains('/')) return false;
        return fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 写入 OptiScaler.ini 关键项：[Upscalers] Dx12Upscaler、[FrameGen] Enabled/FGInput/FGOutput、
    /// [DLSSG]/[XeFG] InterpolationCount（帧生成倍数；文件不存在则跳过）。
    /// </summary>
    private static void PatchIni(string iniPath, LaunchItem item)
    {
        try
        {
            if (!File.Exists(iniPath)) return;
            var text = File.ReadAllText(iniPath);
            var dx12 = Blank(item.OptiDx12Upscaler);
            // ini 里没有 fsr4 取值：FSR4 = ffx（FSR 2.3/3.1/4.x 后端）+ [FSR] UpscalerIndex=0（FSR 4.0.2）
            var isFsr4 = dx12.Equals("fsr4", StringComparison.OrdinalIgnoreCase);
            text = SetIniValue(text, "Upscalers", "Dx12Upscaler", isFsr4 ? "ffx" : dx12);
            text = SetIniValue(text, "FSR", "UpscalerIndex", isFsr4 ? "0" : "auto");
            text = SetIniValue(text, "FSR", "Fsr4Preset", Blank(item.OptiFsr4Preset));
            // FSR4 模型：INT8=2 / FP8=1（对应两种附加包；不额外注入则交给驱动自动判定）
            var fsr4Files = (item.OptiFsr4Files ?? "").Trim().ToLowerInvariant();
            text = SetIniValue(text, "FSR", "Fsr4ForceModel",
                fsr4Files == "int8" ? "2" : fsr4Files == "fp8" ? "1" : "auto");
            text = SetIniValue(text, "FrameGen", "Enabled", item.OptiFrameGen ? "true" : "false");
            text = SetIniValue(text, "FrameGen", "FGInput", Blank(item.OptiFgInput));
            text = SetIniValue(text, "FrameGen", "FGOutput", Blank(item.OptiFgOutput));

            var mult = Blank(item.OptiFgMultiplier);
            // DLSSG 支持 1=2X…5=6X；XeFG 只支持到 3=4X，超出时按 4X 写入（倍数仍只在对应输出下生效）
            text = SetIniValue(text, "DLSSG", "InterpolationCount", mult);
            text = SetIniValue(text, "XeFG", "InterpolationCount",
                int.TryParse(mult, out var n) && n > 3 ? "3" : mult);
            File.WriteAllText(iniPath, text);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 写 ini 失败 {ex.Message}");
        }
    }

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "auto" : value!;

    /// <summary>在 ini 文本的指定段落里设置 key=value；段落/键不存在则插入（保留其余行）</summary>
    private static string SetIniValue(string ini, string section, string key, string value)
    {
        var lines = ini.Replace("\r\n", "\n").Split('\n').ToList();
        var secIdx = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (secIdx < 0)
        {
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return string.Join("\r\n", lines);
        }
        for (var i = secIdx + 1; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[')) { lines.Insert(i, $"{key}={value}"); return string.Join("\r\n", lines); }
            if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return string.Join("\r\n", lines);
            }
        }
        lines.Add($"{key}={value}");
        return string.Join("\r\n", lines);
    }

    /// <summary>选中要还原的备份目录：当前游戏优先，否则最近一次注入</summary>
    private static string? ChooseBackupDir()
    {
        try
        {
            if (!Directory.Exists(BackupDir)) return null;
            var dirs = Directory.GetDirectories(BackupDir);
            if (dirs.Length == 0) return null;

            if (!string.IsNullOrEmpty(TrainerService.LastExeName))
            {
                var want = Path.Combine(BackupDir, Sanitize(TrainerService.LastExeName));
                if (Directory.Exists(want)) return want;
            }
            // 最近一次：按 manifest 里的 Time（缺失则按目录写入时间）
            return dirs
                .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                .OrderByDescending(ManifestTime)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static DateTime ManifestTime(string dir)
    {
        try
        {
            var m = JsonSerializer.Deserialize<OptiBackupManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));
            if (m is not null) return m.Time;
        }
        catch { /* 清单坏了就按目录时间 */ }
        try { return Directory.GetLastWriteTime(dir); } catch { return DateTime.MinValue; }
    }

    // ---- 关闭 / 重新打开游戏 ----

    /// <summary>关闭游戏进程并等待其完全退出（成功返回 true；无法结束进程时返回 false，避免在游戏运行时注入）</summary>
    private static async Task<bool> CloseGameAsync(string exeName)
    {
        if (string.IsNullOrEmpty(exeName)) return false;
        var killed = 0;
        try
        {
            foreach (var p in Process.GetProcessesByName(exeName))
            {
                using (p)
                {
                    try { p.Kill(); killed++; }
                    catch { /* 受保护/已退出：忽略，靠后续存活检查判定 */ }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 结束进程异常 {ex.Message}");
        }
        if (killed == 0 && !TrainerService.IsGameRunning()) return true;

        var sw = Stopwatch.StartNew();
        while (TrainerService.IsGameRunning() && sw.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(300);
        return !TrainerService.IsGameRunning();
    }

    /// <summary>重新打开游戏（以 exe 所在目录为工作目录）；成功返回 null，失败返回错误信息</summary>
    private static string? TryLaunch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? ""
            });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>整理成合法目录名</summary>
    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 ? "game" : s;
    }

    /// <summary>打开存放 OptiScaler 数据/备份的目录</summary>
    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{RootDir}\"",
                UseShellExecute = true
            });
        }
        catch { /* 打不开就算了，不打断编辑流程 */ }
    }
}
