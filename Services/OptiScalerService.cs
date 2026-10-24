using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameLocker.Models;
using SharpCompress.Archives;
using SharpCompress.Common;

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
    /// <summary>该游戏目录已有 OptiScaler 注入痕迹：再次注入会覆盖现有文件，需要用户确认</summary>
    public bool AlreadyInjected { get; set; }
}

/// <summary>GitHub 上的一个可选 Release（供编辑页"具体版本"下拉使用）</summary>
public sealed record RemoteRelease(string Tag, string Url, long Size, string? Digest, bool Prerelease)
{
    /// <summary>下拉框显示文案：版本号（预览版标注）· 体积</summary>
    public string Label => Size > 0
        ? $"{Tag}{(Prerelease ? "（预览）" : "")} · {Math.Max(1, Size / 1048576)}MB"
        : Tag;
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

    /// <summary>本地包文件夹：用户把 zip / 7z / rar 或已解压目录放在这里（选择"本地包"时自动取用）</summary>
    private static readonly string LocalPackageDir = Path.Combine(RootDir, "local");

    /// <summary>本地包解压目录：与用户放包的文件夹分开，避免解压时清空用户目录</summary>
    private static readonly string LocalExtractDir = Path.Combine(RootDir, "local-extracted");

    /// <summary>本地包文件夹路径（供编辑页显示与打开）</summary>
    public static string LocalPackageFolder => LocalPackageDir;

    /// <summary>OptiScaler 版本渠道（值, 显示名）：正式版取官方仓库 latest Release，
    /// 测试版取社区镜像的 pre 版本（OptiScaler-Betas）。ini 内容与用法完全一致，只是包更新更快。</summary>
    public static readonly (string Value, string Label)[] VersionChannelOptions =
    [
        ("stable", "正式版"),
        ("beta", "测试版"),
        ("local", "本地包")
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
    /// FSR4 的主 DLL 不在 OptiScaler 官方包里，必须另从社区仓库取并注入（AMD 会按显卡世代给不同模型），
    /// 因此不提供"不注入"选项；默认 INT8（兼容面最广）。
    /// 值=(variant, 显示名)：int8=兼容卡（RDNA2/3 等，OptiScaler-Extras；Intel Arc 也走 INT8/XMX）、fp8=RDNA4（Optiscaler-Extras-FP8）。
    /// </summary>
    public static readonly (string Value, string Label)[] Fsr4FilesOptions =
    [
        ("int8", "INT8 · NVIDIA / AMD / Intel"),
        ("fp8", "FP8 · AMD RDNA4"),
        ("xmx", "XMX · intel")
    ];

    /// <summary>FSR4 文件变体默认值（旧配置里为 "auto" 的数据也归到这里）</summary>
    public const string DefaultFsr4Variant = "int8";

    /// <summary>FSR4.1.1 XMX：Intel Arc / XMX 专用模型，两个 DLL 一起使用，版本固定</summary>
    public const string XmxVariant = "xmx";
    public const string XmxTag = "4.1.1";
    private const string XmxUrl =
        "https://github.com/Mr-zhoujiacheng/GameLocker/releases/download/fsr4-xmx/FSR4.1.1XMX.zip";
    private const long XmxSize = 75108743;
    private const string XmxSha256 = "5e40087efeb789ba1d7d61901b659b8042e8cf107486bfacd448fced143da3f4";

    /// <summary>XMX 包内的两个 DLL（缺一不可，且要覆盖到 OptiScaler 包内 FSR 模块所在目录）</summary>
    private static readonly string[] XmxFileNames =
    [
        "amd_fidelityfx_upscaler_dx12.dll",
        "fsr-original.dll"
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

    /// <summary>FSR4 附加包的来源仓库（variant → 仓库 API / 显示名）</summary>
    private static readonly Dictionary<string, (string Repo, string Label)> Fsr4Sources = new()
    {
        ["int8"] = ("https://api.github.com/repos/Optiscaler-Client/OptiScaler-Extras", "INT8"),
        ["fp8"] = ("https://api.github.com/repos/Optiscaler-Client/Optiscaler-Extras-FP8", "FP8")
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
    private const string StableRepoApi = "https://api.github.com/repos/optiscaler/OptiScaler";
    private const string BetaRepoApi = "https://api.github.com/repos/Optiscaler-Client/OptiScaler-Betas";

    /// <summary>兼容列表缓存有效期（超过则尝试重新抓取）</summary>
    private static readonly TimeSpan CompatTtl = TimeSpan.FromDays(7);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        // 跟随 GitHub Release 的 302 跳转；带 UA 降低被拒概率。
        // 不设总超时：55MB 包在慢速镜像下可能要十几分钟，用各请求自己的 CTS 控制等待响应头
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = Timeout.InfiniteTimeSpan
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
            if (!TrainerService.IsLastGameActuallyForeground())
            {
                // 切到设置 / Steam / 浏览器等非游戏窗口时，虽然 LastExeName 还保留着游戏，
                // 但此刻前台不是它 —— 拒绝注入，避免改到错误的程序目录
                Report($"请先把游戏「{TrainerService.CurrentGameName()}」切到前台再注入", true);
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

    /// <summary>该组件的 FSR4 附加文件变体：画质提升=FSR4 时必注入，未选或旧配置的空值/auto 一律按 INT8</summary>
    private static string Fsr4VariantOf(LaunchItem item)
    {
        if (!string.Equals(item.OptiDx12Upscaler, "fsr4", StringComparison.OrdinalIgnoreCase)) return "";
        var v = (item.OptiFsr4Files ?? "").Trim().ToLowerInvariant();
        return v is "fp8" or XmxVariant ? v : DefaultFsr4Variant;
    }

    /// <summary>按配置取 FSR4 附加文件；画质提升不是 FSR4 时不需要，返回 null（不报错）</summary>
    private static async Task<string?> EnsureFsr4ForItemAsync(LaunchItem item)
    {
        var variant = Fsr4VariantOf(item);
        if (variant.Length == 0) return null;
        Report($"正在准备 FSR4 文件（OptiScaler 官方包不含，需单独下载）…");
        return await GetFsr4Async(variant, item.OptiFsr4VersionTag);
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
            DebugLog.Write($"OptiScaler: 使用包目录 {root}");

            // 1b) FSR4 的主 DLL 不在官方包里：选了 FSR4 就必须注入，先把它取下来
            var fsr4Root = await EnsureFsr4ForItemAsync(item);
            if (fsr4Root is null && Fsr4VariantOf(item).Length > 0)
            {
                Report("FSR4 文件获取失败，已取消注入（请检查网络后重试）", true);
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
                // 注入失败也把游戏重新打开，避免用户被卡在"游戏已被关闭"的状态
                await Task.Delay(400);
                var reopenErr = TryLaunch(insp.ExePath);
                var msg = error.Length > 0 ? error : "注入失败";
                msg += reopenErr is null ? "（已重新打开游戏）" : $"；且无法重新打开游戏：{reopenErr}";
                Report(msg, true);
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

    /// <summary>一键还原：还原指定备份（key=备份目录名）；key 为空时回退到"当前游戏优先、否则最近一次"。
    /// 同步，返回给 UI 展示的一句话结果。</summary>
    public static string Restore(string? key = null)
    {
        try
        {
            var dir = string.IsNullOrEmpty(key) ? ChooseBackupDir() : FindBackupDir(key);
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
        insp.AlreadyInjected = DetectInjected(insp.GameDir, exeName);

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

    /// <summary>游戏目录是否已有 OptiScaler 注入痕迹：本工具写入的 OptiScaler.ini、
    /// v10 版的 OptiScaler 子目录，或上次注入留下的备份清单（要求游戏目录一致）</summary>
    private static bool DetectInjected(string gameDir, string exeName)
    {
        if (string.IsNullOrEmpty(gameDir)) return false;
        try
        {
            if (File.Exists(Path.Combine(gameDir, "OptiScaler.ini"))) return true;
            if (Directory.Exists(Path.Combine(gameDir, "OptiScaler"))) return true;
            var manifestPath = Path.Combine(BackupDir, Sanitize(exeName), "manifest.json");
            if (!File.Exists(manifestPath)) return false;
            var m = JsonSerializer.Deserialize<OptiBackupManifest>(File.ReadAllText(manifestPath));
            return m is not null && string.Equals(m.GameDir, gameDir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await Http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(cts.Token);
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

    /// <summary>该项是否选了"本地包"渠道（不联网，直接用本地包文件夹里的包）</summary>
    private static bool IsLocal(LaunchItem item) => Blank(item.OptiChannel) == "local";

    /// <summary>取得解压后的 OptiScaler 包根目录（含 OptiScaler.dll）；失败返回 null。
    /// 指定了具体版本（OptiVersionTag）就取那个 Release，否则取该渠道最新版。</summary>
    private static async Task<string?> EnsurePackageAsync(LaunchItem item)
    {
        // 本地包渠道：显式选过包就用它，否则扫描"本地包文件夹"；失败直接报错，不回退联网下载
        if (IsLocal(item))
        {
            if (!string.IsNullOrWhiteSpace(item.OptiLocalPackage))
            {
                var localDir = await MaterializeLocalAsync(item.OptiLocalPackage!);
                DebugLog.Write($"OptiScaler: 本地包 {item.OptiLocalPackage} → {localDir ?? "(不可用)"}");
                if (localDir is not null) return localDir;
                Report("本地包不可用：请检查本地包文件夹里的包", true);
                return null;
            }
            var scanned = await FindPackageInLocalFolderAsync();
            DebugLog.Write($"OptiScaler: 本地包文件夹 {LocalPackageDir} → {scanned ?? "(没有可用包)"}");
            if (scanned is not null) return scanned;
            Report("本地包文件夹里没有可用的包：请放入 zip / 7z / rar 或已解压目录", true);
            return null;
        }

        return await GetPackageAsync(IsBeta(item), item.OptiVersionTag);
    }

    /// <summary>按渠道 + 版本标签取包（tag 为空=最新版）：已缓存直接用，否则从 GitHub 下载并解压。
    /// 每个版本一个缓存目录（package\{tag} / package-beta\{tag}），切换版本不会互相覆盖；
    /// 旧版直接解压在渠道根目录上的缓存仍可复用。</summary>
    private static async Task<string?> GetPackageCoreAsync(bool beta, string? versionTag)
    {
        var label = beta ? "OptiScaler 测试版" : "OptiScaler";
        var channel = beta ? "beta" : "stable";
        var repo = beta ? BetaRepoApi : StableRepoApi;
        var rootBase = beta ? BetaPackageDir : PackageDir;
        var tag = (versionTag ?? "").Trim();

        Report($"正在获取 {label} 版本信息…");
        var asset = await GetReleaseAsync(repo, tag);
        if (asset is null)
        {
            // 离线/接口失败兜底：已有缓存照样能用（旧版根目录或任一版本目录）
            if (File.Exists(Path.Combine(rootBase, "OptiScaler.dll"))) return rootBase;
            if (LatestCachedRoot(rootBase, ["OptiScaler.dll"]) is { } cached)
            {
                DebugLog.Write($"OptiScaler: 无法访问 GitHub，复用已缓存包 {cached}");
                return cached;
            }
            Report($"无法获取 {label} 版本（GitHub 访问失败）", true);
            return null;
        }

        var dir = Path.Combine(rootBase, SafeFolderName(asset.Tag));
        if (FindRootContaining(dir, ["OptiScaler.dll"]) is { } cachedRoot) return cachedRoot;
        // 旧版把包直接解压在渠道根目录（无版本子目录）：
        //  - 记录的版本号正好是最新版 → 复用；
        //  - 没有版本记录（channels.json 被清理/旧安装）→ 也复用，避免每次注入都重新下载同一个包
        if (tag.Length == 0 && IsNonEmptyFile(Path.Combine(rootBase, "OptiScaler.dll")))
        {
            if (ChannelVersions.TryGetValue(channel, out var cachedVer))
            {
                if (string.Equals(cachedVer, asset.Tag.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase))
                    return rootBase;
            }
            else
            {
                DebugLog.Write($"OptiScaler: 复用渠道根目录的已解压包（无版本记录）{rootBase}");
                RememberChannelVersion(channel, asset.Tag);
                return rootBase;
            }
        }

        Directory.CreateDirectory(rootBase);
        // 临时包按版本命名：不同版本（如"最新版"与手选的 tag）不会写同一个文件
        var tmp = Path.Combine(rootBase,
            SafeFolderName(asset.Tag) + Path.GetExtension(new Uri(asset.Url).LocalPath));
        Report($"正在下载 {label} {asset.Tag}（约 {Math.Max(1, asset.Size / 1048576)}MB，自动选最快源）…", progress: 0);
        try
        {
            var ok = await DownloadAssetAsync(asset.Url, tmp, asset.Size, asset.Digest,
                (got, total) => ReportDownload($"{label} {asset.Tag}", got, total));
            if (!ok) { Report($"{label} 下载失败，请检查网络后重试", true); return null; }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 下载异常 {ex.Message}");
            Report($"{label} 下载失败，请检查网络后重试", true);
            return null;
        }

        Report($"正在解压 {label}…");
        var root = await ExtractAsync(tmp, dir);
        try { File.Delete(tmp); } catch { /* 临时包删不掉无害 */ }
        if (root is null)
        {
            Report($"{label} 包解压失败（需要系统 tar.exe；可改用「本地包」渠道）", true);
            return null;
        }
        // 包可用了：记下这个渠道实际用的版本号，供版本下拉显示
        RememberChannelVersion(channel, asset.Tag);
        return root;
    }

    /// <summary>版本标签 → 可用作目录名的字符串（去掉非法字符）</summary>
    private static string SafeFolderName(string tag)
    {
        var sb = new StringBuilder(tag.Length);
        foreach (var ch in tag.Trim())
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        var name = sb.ToString().Trim('.', ' ');
        return name.Length == 0 ? "latest" : name;
    }

    /// <summary>在版本缓存根目录下找最新的、含指定标志文件的已解压包（离线兜底用）</summary>
    private static string? LatestCachedRoot(string baseDir, string[] markers)
    {
        if (!Directory.Exists(baseDir)) return null;
        try
        {
            foreach (var d in Directory.GetDirectories(baseDir)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                if (FindRootContaining(d, markers) is { } root) return root;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 扫描缓存失败 {ex.Message}");
        }
        return null;
    }

    /// <summary>查询指定 Release 的下载资产（tag 为空=最新正式版）；失败返回 null。
    /// preferToken 用于一个 Release 带多个资产的仓库（FSR4），优先挑名字里含该标记的包。</summary>
    private static async Task<RemoteRelease?> GetReleaseAsync(string repo, string tag, string? preferToken = null)
    {
        var url = tag.Length == 0
            ? repo + "/releases/latest"
            : repo + "/releases/tags/" + Uri.EscapeDataString(tag);
        var json = await GetStringAsync(url);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ReadRelease(doc.RootElement, preferToken);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: Release 解析异常 {ex.Message}");
            return null;
        }
    }

    /// <summary>从 Release JSON 里挑出可下载的包（zip / 7z；tar.gz 不在支持范围）</summary>
    private static RemoteRelease? ReadRelease(JsonElement release, string? preferToken = null)
    {
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (tag.Length == 0) return null;
        var prerelease = release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        RemoteRelease? best = null;
        var bestScore = -1;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            var lower = name.ToLowerInvariant();
            if (name.Length == 0 || url.Length == 0) continue;
            if (!lower.EndsWith(".zip") && !lower.EndsWith(".7z")) continue;

            var score = lower.EndsWith(".zip") ? 2 : 1;
            if (!string.IsNullOrEmpty(preferToken) &&
                lower.Contains(preferToken.ToLowerInvariant(), StringComparison.Ordinal)) score += 10;
            if (score <= bestScore) continue;
            bestScore = score;

            var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0L;
            var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
            best = new RemoteRelease(tag, url, size, digest, prerelease);
        }
        return best;
    }

    /// <summary>Release 列表缓存（10 分钟），避免反复切下拉框时打 GitHub API</summary>
    private static readonly TimeSpan ReleaseListTtl = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<string, (DateTime At, RemoteRelease[] List)> ReleaseCache = [];

    /// <summary>列出仓库的 Release（最多 30 个）；失败返回空数组</summary>
    private static async Task<RemoteRelease[]> GetReleasesAsync(string repo, string? preferToken = null)
    {
        var key = repo + "|" + (preferToken ?? "");
        lock (ReleaseCache)
        {
            if (ReleaseCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < ReleaseListTtl)
                return hit.List;
        }

        var json = await GetStringAsync(repo + "/releases?per_page=30");
        if (json is null) return [];
        var list = new List<RemoteRelease>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (ReadRelease(e, preferToken) is { } r) list.Add(r);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: Release 列表解析异常 {ex.Message}");
            return [];
        }

        var arr = list.ToArray();
        lock (ReleaseCache) ReleaseCache[key] = (DateTime.UtcNow, arr);
        return arr;
    }

    /// <summary>GET 文本（20 秒等响应头；失败返回 null）</summary>
    private static async Task<string?> GetStringAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await Http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 请求 {url} 失败 {ex.Message}");
            return null;
        }
    }

    /// <summary>编辑页「具体版本」下拉项：首项"最新版"（空值）+ 该渠道可下载的 Release。
    /// 正式版渠道只列非预览 Release；本地包渠道没有版本概念，返回空。</summary>
    public static async Task<(string Value, string Label)[]> GetVersionOptionsAsync(string channel)
    {
        if (channel.Equals("local", StringComparison.OrdinalIgnoreCase)) return [];
        var beta = channel.Equals("beta", StringComparison.OrdinalIgnoreCase);
        var list = await GetReleasesAsync(beta ? BetaRepoApi : StableRepoApi);
        var items = list.Where(r => beta || !r.Prerelease).Select(r => (r.Tag, r.Label)).ToList();

        var baseDir = beta ? BetaPackageDir : PackageDir;
        var downloaded = DownloadedDirs(baseDir, ["OptiScaler.dll"]);
        // 旧版把包直接解压在渠道根目录：版本号只能看 channels.json 的记录
        if (IsNonEmptyFile(Path.Combine(baseDir, "OptiScaler.dll")) &&
            ChannelVersions.TryGetValue(beta ? "beta" : "stable", out var legacyVer))
            downloaded.Add(legacyVer);

        // "最新版（自动）"解析到的就是列表里第一个 Release，已下载同样是打勾
        var latestTag = items.Count > 0 ? items[0].Tag : "";
        return [
            ("", TagLabel("最新版（自动）", latestTag, downloaded)),
            .. items.Select(i => (i.Tag, TagLabel(i.Label, i.Tag, downloaded)))
        ];
    }

    /// <summary>编辑页「FSR4 版本」下拉项：首项"最新版"（空值）+ 该变体仓库可下载的 Release</summary>
    public static async Task<(string Value, string Label)[]> GetFsr4VersionOptionsAsync(string variant)
    {
        // XMX 是固定版本的专用模型包，不走社区仓库的版本列表
        if (variant == XmxVariant) return [(XmxTag, "FSR4.1.1XMX")];
        if (!Fsr4Sources.TryGetValue(variant, out var src)) return [];
        var list = await GetReleasesAsync(src.Repo, variant);
        var items = list.Select(r => (r.Tag, r.Label)).ToList();
        var downloaded = DownloadedDirs(Path.Combine(Fsr4Dir, variant), Fsr4KnownNames);
        var latestTag = items.Count > 0 ? items[0].Tag : "";
        return [
            ("", TagLabel("最新版（自动）", latestTag, downloaded)),
            .. items.Select(i => (i.Tag, TagLabel(i.Label, i.Tag, downloaded)))
        ];
    }

    /// <summary>某缓存目录下已下载完成（解压出有效标志文件）的版本目录名</summary>
    private static HashSet<string> DownloadedDirs(string baseDir, string[] markers)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(baseDir)) return set;
            foreach (var d in Directory.GetDirectories(baseDir))
                if (FindRootContaining(d, markers) is not null) set.Add(Path.GetFileName(d));
        }
        catch (Exception ex) { DebugLog.Write($"OptiScaler: 扫描已下载版本失败 {ex.Message}"); }
        return set;
    }

    /// <summary>已下载完成的版本在名称后加 ✓（tag 与目录名都去掉 v 前缀后比较）</summary>
    private static string TagLabel(string label, string tag, HashSet<string> downloaded)
    {
        if (string.IsNullOrEmpty(tag)) return label;
        var key = SafeFolderName(tag).TrimStart('v', 'V');
        foreach (var d in downloaded)
        {
            var name = Path.GetFileName(d).TrimStart('v', 'V');
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return label + "  ✓";
        }
        return label;
    }

    /// <summary>编辑页选定版本后调用的预下载（后台进行；失败只记日志，不打扰编辑流程）</summary>
    public static async Task PrefetchAsync(string channel, string tag)
    {
        if (channel.Equals("local", StringComparison.OrdinalIgnoreCase)) return;
        try { await GetPackageAsync(channel.Equals("beta", StringComparison.OrdinalIgnoreCase), tag); }
        catch (Exception ex) { DebugLog.Write($"OptiScaler: 预下载失败 {ex.Message}"); }
    }

    /// <summary>按渠道取包；预下载与注入可能同时发起，同渠道串行化。
    /// 注意按渠道而不是"渠道+版本"串行：「最新版」与手选版本往往解析成同一个 Release，
    /// 按版本分别加锁会并发出两份下载，用户看到的就是"下载两遍"。</summary>
    private static async Task<string?> GetPackageAsync(bool beta, string? versionTag)
    {
        var gate = FetchGateFor($"pkg|{(beta ? "beta" : "stable")}");
        await gate.WaitAsync();
        try { return await GetPackageCoreAsync(beta, versionTag); }
        finally { gate.Release(); }
    }

    /// <summary>同一渠道/版本的"检查缓存 → 下载 → 解压"只允许一个任务执行（多个并发调用复用同一份结果）</summary>
    private static readonly Dictionary<string, SemaphoreSlim> FetchGates = [];
    private static SemaphoreSlim FetchGateFor(string key)
    {
        lock (FetchGates)
        {
            if (!FetchGates.TryGetValue(key, out var gate))
                FetchGates[key] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    /// <summary>编辑页选定 FSR4 版本后调用的预下载（XMX 是固定 URL，同样走这里）</summary>
    public static async Task PrefetchFsr4Async(string variant, string tag)
    {
        if (variant != XmxVariant && !Fsr4Sources.ContainsKey(variant)) return;
        try { await GetFsr4Async(variant, tag); }
        catch (Exception ex) { DebugLog.Write($"OptiScaler: FSR4 预下载失败 {ex.Message}"); }
    }

    /// <summary>取 FSR4 附加文件；与预下载串行化，避免同时写同一个临时包</summary>
    private static async Task<string?> GetFsr4Async(string variant, string? versionTag)
    {
        var gate = FetchGateFor($"fsr4|{variant}");
        await gate.WaitAsync();
        try { return await GetFsr4CoreAsync(variant, versionTag); }
        finally { gate.Release(); }
    }

    /// <summary>GitHub 下载加速镜像（按顺序尝试；均为 GitHub 文件转发，下载后按 API 给出的 SHA256 校验）</summary>
    private static readonly string[] DownloadMirrors =
    [
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
        "https://ghfast.top/"
    ];

    /// <summary>下载 Release 资产：镜像优先、直连兜底；镜像失效或明显过慢（6 秒内不足 300KB）自动换下一个源</summary>
    private static async Task<bool> DownloadAssetAsync(
        string url, string destPath, long expectedSize, string? digest,
        Action<long, long?> onProgress, CancellationToken token = default)
    {
        var candidates = DownloadMirrors.Select(m => m + url).Append(url);
        foreach (var candidate in candidates)
        {
            try
            {
                if (await DownloadOnceAsync(candidate, destPath, expectedSize, digest, onProgress, token))
                    return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"OptiScaler: 下载源 {candidate} 失败 {ex.Message}");
            }
            // 保留已下载的部分：下一个源 / 下次运行可断点续传
        }
        return false;
    }

    /// <summary>从单个 URL 下载并校验（支持续传）；速度过慢/长度不符/哈希不符返回 false</summary>
    private static async Task<bool> DownloadOnceAsync(
        string url, string destPath, long expectedSize, string? digest,
        Action<long, long?> onProgress, CancellationToken token)
    {
        // 上次中断/换源留下的部分文件：直接用 Range 续传，不必从 0 重下
        long have = 0;
        try { if (File.Exists(destPath)) have = new FileInfo(destPath).Length; } catch { }
        if (expectedSize > 0 && have >= expectedSize)
            return VerifyDigest(destPath, digest);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(20)); // 等待响应头（镜像失联时不至于卡满）
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var resp = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode) return false;
        cts.CancelAfter(Timeout.InfiniteTimeSpan);

        // 只有 206 才能接着写；服务器不支持 Range（返回 200）就重头下
        var append = have > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!append) have = 0;
        var length = resp.Content.Headers.ContentLength;
        var total = length is null ? (expectedSize > 0 ? expectedSize : (long?)null) : have + length;

        await using var net = await resp.Content.ReadAsStreamAsync(cts.Token);
        long got = have;
        var start = Environment.TickCount64;
        var last = start;
        // 写完并关闭文件句柄后再校验：校验要重新打开文件，句柄未释放会导致无法读取、也无法删除
        await using (var fs = new FileStream(destPath, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            while (true)
            {
                var read = await net.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                got += read;
                var now = Environment.TickCount64;
                // 连接建立很快但被限速时（国内直连常见），6 秒还不到 300KB 就换源
                if (now - start >= 6000 && got - have < 300 * 1024)
                {
                    DebugLog.Write($"OptiScaler: {url} 速度过慢（{(got - have) / 1024.0:0}KB/6s），切换下载源");
                    return false;
                }
                if (now - last >= 200) { last = now; onProgress(got, total); }
            }
            await fs.FlushAsync(cts.Token);
        }
        onProgress(got, total);

        if (expectedSize > 0 && got != expectedSize) return false;
        return VerifyDigest(destPath, digest);
    }

    /// <summary>按 SHA256 校验（API 有 digest 时）；不符则删除坏文件，避免下次续传垃圾。
    /// 注意 GitHub API 的 digest 形如 "sha256:xxxx"，比较前必须去掉算法前缀。</summary>
    private static bool VerifyDigest(string path, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return true;
        var expect = digest.Trim();
        var colon = expect.IndexOf(':');
        if (colon >= 0) expect = expect[(colon + 1)..].Trim();
        var actual = Sha256File(path);
        if (string.Equals(actual, expect, StringComparison.OrdinalIgnoreCase)) return true;
        DebugLog.Write($"OptiScaler: SHA256 校验失败（期望 {Short(expect)}，实际 {Short(actual)}），删除已下载内容");
        try { File.Delete(path); } catch { }
        return false;
    }

    /// <summary>日志用的短哈希（前 12 位）</summary>
    private static string Short(string? hash) =>
        string.IsNullOrEmpty(hash) ? "(读取失败)" : hash[..Math.Min(12, hash.Length)] + "…";

    private static string? Sha256File(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>把本地包（目录 / zip / 7z / rar）物化成含 OptiScaler.dll 的目录；失败返回 null</summary>
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
            if (ext is not (".zip" or ".7z" or ".rar")) return null;
            Report("正在解压本地 OptiScaler 包…");
            return await ExtractAsync(path, LocalExtractDir);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 本地包处理异常 {ex.Message}");
            return null;
        }
    }

    /// <summary>扫描"本地包文件夹"：优先已解压好的目录，其次取最新的 zip / 7z / rar 解压
    /// （解压到独立目录，不删用户放的包）</summary>
    private static async Task<string?> FindPackageInLocalFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(LocalPackageDir);
            if (FindPackageRoot(LocalPackageDir) is { } dir) return dir;
            var archive = Directory.EnumerateFiles(LocalPackageDir)
                .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".zip" or ".7z" or ".rar")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (archive is null) return null;
            Report("正在解压本地包…");
            return await ExtractAsync(archive, LocalExtractDir);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 本地包文件夹扫描异常 {ex.Message}");
            return null;
        }
    }

    /// <summary>打开本地包文件夹（不存在则创建）</summary>
    public static void OpenLocalFolder()
    {
        try
        {
            Directory.CreateDirectory(LocalPackageDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{LocalPackageDir}\"",
                UseShellExecute = true
            });
        }
        catch { /* 打不开就算了，不打断编辑流程 */ }
    }

    /// <summary>
    /// 取 FSR4 附加文件（OptiScaler 官方包里没有）：已缓存则直接用，否则拉对应社区仓库的
    /// 指定 Release（tag 为空=最新版）并解压，返回含 FSR4 DLL 的目录；失败返回 null。
    /// </summary>
    private static async Task<string?> GetFsr4CoreAsync(string variant, string? versionTag)
    {
        if (variant == XmxVariant) return await GetXmxAsync();
        if (!Fsr4Sources.TryGetValue(variant, out var src)) return null;
        var baseDir = Path.Combine(Fsr4Dir, variant);
        var tag = (versionTag ?? "").Trim();

        Report($"正在获取 FSR4 {src.Label} 版本信息…");
        var asset = await GetReleaseAsync(src.Repo, tag, variant);
        if (asset is null)
        {
            // 离线兜底：任一已缓存版本都能用
            if (FindRootContaining(baseDir, Fsr4KnownNames) is { } cached)
            {
                DebugLog.Write($"OptiScaler: 无法访问 GitHub，复用已缓存的 FSR4 {src.Label} 文件（{cached}）");
                return cached;
            }
            Report($"无法获取 FSR4 {src.Label} 版本（GitHub 访问失败）", true);
            return null;
        }

        var dir = Path.Combine(baseDir, SafeFolderName(asset.Tag));
        if (FindRootContaining(dir, Fsr4KnownNames) is { } hit) return hit;

        Directory.CreateDirectory(baseDir);
        var tmp = Path.Combine(baseDir,
            SafeFolderName(asset.Tag) + Path.GetExtension(new Uri(asset.Url).LocalPath));
        Report($"正在下载 FSR4 {src.Label} {asset.Tag}（约 {Math.Max(1, asset.Size / 1048576)}MB，自动选最快源）…", progress: 0);
        try
        {
            var ok = await DownloadAssetAsync(asset.Url, tmp, asset.Size, asset.Digest,
                (got, total) => ReportDownload($"FSR4 {src.Label} {asset.Tag}", got, total));
            if (!ok) { Report("FSR4 下载失败，请检查网络后重试", true); return null; }
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
            Report($"FSR4 {src.Label} 包解压失败（需要系统 tar.exe）", true);
            return null;
        }
        return root;
    }

    /// <summary>取 FSR4.1.1 XMX 模型包：来源固定为本项目 Release 资源，校验大小与 SHA256 后解压</summary>
    private static async Task<string?> GetXmxAsync()
    {
        var baseDir = Path.Combine(Fsr4Dir, XmxVariant);
        var dir = Path.Combine(baseDir, XmxTag);
        if (FindRootContaining(dir, XmxFileNames) is { } cached) return cached;

        Directory.CreateDirectory(baseDir);
        var tmp = Path.Combine(baseDir, "FSR4.1.1XMX.zip");
        Report($"正在下载 FSR4.1.1 XMX（约 {XmxSize / 1048576}MB，自动选最快源）…", progress: 0);
        try
        {
            var ok = await DownloadAssetAsync(XmxUrl, tmp, XmxSize, XmxSha256,
                (got, total) => ReportDownload("FSR4.1.1 XMX", got, total));
            if (!ok) { Report("FSR4.1.1 XMX 下载失败，请检查网络后重试", true); return null; }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: FSR4.1.1 XMX 下载异常 {ex.Message}");
            Report("FSR4.1.1 XMX 下载失败，请检查网络后重试", true);
            return null;
        }

        Report("正在解压 FSR4.1.1 XMX…");
        var root = await ExtractAsync(tmp, dir, XmxFileNames);
        try { File.Delete(tmp); } catch { /* 临时包删不掉无害 */ }
        if (root is null)
        {
            Report("FSR4.1.1XMX 包解压失败（需要系统 tar.exe）", true);
            return null;
        }
        Report("FSR4.1.1XMX 已下载完成，注入时直接使用");
        return root;
    }

    /// <summary>解压 zip / 7z / rar 到 destDir，并返回其中含指定标志文件的目录；失败返回 null。
    /// 先解到 ".tmp" 目录，成功后再替换目标目录，避免中断留下半个包被当作可用缓存。</summary>
    private static async Task<string?> ExtractAsync(string archive, string destDir, string[]? markers = null)
    {
        markers ??= ["OptiScaler.dll"];
        var staging = destDir + ".tmp";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        catch { /* 删不掉：解压时会覆盖同名文件 */ }
        Directory.CreateDirectory(staging);

        var ext = Path.GetExtension(archive).ToLowerInvariant();
        if (ext == ".zip")
        {
            try
            {
                // 大包（XMX 约 111MB）同步解压会卡住 UI 线程，放后台线程解
                await Task.Run(() => ZipFile.ExtractToDirectory(archive, staging, true));
            }
            catch (Exception ex)
            {
                DebugLog.Write($"OptiScaler: zip 解压失败 {ex.Message}");
                TryDeleteDir(staging);
                return null;
            }
        }
        else if (ext == ".7z")
        {
            if (!await Task.Run(() => RunBsdtar(archive, staging))) { TryDeleteDir(staging); return null; }
        }
        else if (ext == ".rar")
        {
            // 系统 tar.exe 不保证支持 RAR，改用 SharpCompress（RAR4 / RAR5 均可解）
            if (!await Task.Run(() => RunRarExtract(archive, staging))) { TryDeleteDir(staging); return null; }
        }
        else
        {
            TryDeleteDir(staging);
            return null;
        }

        var found = FindRootContaining(staging, markers);
        if (found is null) { TryDeleteDir(staging); return null; }

        try
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            if (string.Equals(found, staging, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(staging, destDir);
            }
            else
            {
                Directory.Move(found, destDir);
                TryDeleteDir(staging);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 解压结果落盘失败 {ex.Message}");
            TryDeleteDir(staging);
            return null;
        }
        return FindRootContaining(destDir, markers);
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 忽略 */ }
    }

    /// <summary>用 SharpCompress 解压 RAR（支持 RAR4 / RAR5；加密包需要密码，会失败）</summary>
    private static bool RunRarExtract(string archive, string destDir)
    {
        try
        {
            var root = Path.GetFullPath(destDir);
            using var ar = ArchiveFactory.Open(archive);
            foreach (var entry in ar.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;
                var full = Path.GetFullPath(Path.Combine(root, entry.Key));
                // 防目录穿越：只解到 destDir 之内
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) continue;
                var parent = Path.GetDirectoryName(full);
                if (parent is not null) Directory.CreateDirectory(parent);
                entry.WriteToFile(full, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
            }
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: rar 解压失败 {ex.Message}");
            return false;
        }
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
                // 空文件视为解压中断的半成品，不算有效包
                if (IsNonEmptyFile(Path.Combine(d, name))) return d;
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

    /// <summary>文件存在且非空（0 字节 = 解压/复制中断的半成品）</summary>
    private static bool IsNonEmptyFile(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
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

    /// <summary>OptiScaler 包内 FSR 模块所在的相对目录（0.9.4=根目录，v10=OptiScaler）；找不到返回空</summary>
    private static string Fsr4TargetSubDir(string pkgRoot)
    {
        var file = FindFileNamed(pkgRoot, "amd_fidelityfx_upscaler_dx12.dll");
        if (file is null) return "";
        var rel = Path.GetDirectoryName(Path.GetRelativePath(pkgRoot, file));
        return string.IsNullOrEmpty(rel) || rel == "." ? "" : rel;
    }

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

    /// <summary>复制文件；刚结束的游戏/杀软可能仍短暂占用句柄，遇到占用时重试（最多约 10 秒）</summary>
    private static void CopyWithRetry(string src, string dest)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Copy(src, dest, true); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 33)
            {
                Thread.Sleep(300);
            }
        }
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
                    CopyWithRetry(dest, bk);
                    manifest.BackedUp.Add(destRel);
                }
                else
                {
                    manifest.Created.Add(destRel);
                }
                CopyWithRetry(src, dest);
            }

            // FSR4 附加文件：官方包不含，按已知文件名补进游戏根目录（游戏硬编码哪个名字就用哪个）。
            // XMX 是 Intel 专用模型，要覆盖到 OptiScaler 包内 FSR 模块所在目录
            // （0.9.4 在游戏根目录、v10 在 OptiScaler\ 子目录），两个 DLL 一起放。
            if (fsr4Root is not null)
            {
                var fsr4Variant = Fsr4VariantOf(item);
                var names = fsr4Variant == XmxVariant ? XmxFileNames : Fsr4KnownNames;
                var subDir = fsr4Variant == XmxVariant ? Fsr4TargetSubDir(pkgRoot) : "";
                foreach (var name in names)
                {
                    var src = FindFileNamed(fsr4Root, name);
                    if (src is null) continue;
                    var rel = Path.Combine(subDir, name);
                    var dest = Path.Combine(gameDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (File.Exists(dest))
                    {
                        var bk = Path.Combine(bdir, "files", rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(bk)!);
                        CopyWithRetry(dest, bk);
                        manifest.BackedUp.Add(rel);
                    }
                    else
                    {
                        manifest.Created.Add(rel);
                    }
                    CopyWithRetry(src, dest);
                    DebugLog.Write($"OptiScaler: 补入 FSR4 文件 {rel}");
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
            // FSR4 模型：INT8/XMX=2 / FP8=1（画质提升=FSR4 时必注入，默认 INT8）
            var fsr4Files = Fsr4VariantOf(item);
            text = SetIniValue(text, "FSR", "Fsr4ForceModel",
                fsr4Files is "int8" or XmxVariant ? "2" : fsr4Files == "fp8" ? "1" : "auto");
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

    /// <summary>列出可还原的备份（值=备份目录名，显示名=游戏名（进程名）· 备份时间），按时间倒序</summary>
    public static (string Value, string Label)[] GetBackupOptions()
    {
        try
        {
            if (!Directory.Exists(BackupDir)) return [];
            var list = new List<(string Value, string Label, DateTime Time)>();
            foreach (var dir in Directory.GetDirectories(BackupDir))
            {
                var manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest)) continue;

                var key = Path.GetFileName(dir);
                var game = key;
                var exe = "";
                var time = DateTime.MinValue;
                try
                {
                    var m = JsonSerializer.Deserialize<OptiBackupManifest>(File.ReadAllText(manifest));
                    if (m is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(m.Game)) game = m.Game;
                        exe = m.ExeName;
                        time = m.Time;
                    }
                }
                catch { /* 清单损坏：用目录名兜底 */ }

                // 窗口标题里常已含进程名，避免重复成"游戏名（游戏名）"
                var name = exe.Length > 0 && !game.Contains(exe, StringComparison.OrdinalIgnoreCase)
                    ? $"{game}（{exe}）"
                    : game;
                var label = time == default ? name : $"{name} · {time:MM-dd HH:mm}";
                list.Add((key, label, time));
            }
            return [.. list.OrderByDescending(x => x.Time).Select(x => (x.Value, x.Label))];
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 备份列表读取失败 {ex.Message}");
            return [];
        }
    }

    /// <summary>默认选中的备份：当前游戏优先，否则最新一份；没有备份返回空串</summary>
    public static string DefaultBackupKey()
    {
        var options = GetBackupOptions();
        if (options.Length == 0) return "";
        var key = Sanitize(TrainerService.LastExeName ?? "");
        return options.Any(o => string.Equals(o.Value, key, StringComparison.OrdinalIgnoreCase))
            ? key
            : options[0].Value;
    }

    /// <summary>按备份目录名定位备份目录（校验清单存在）</summary>
    private static string? FindBackupDir(string key)
    {
        try
        {
            var dir = Path.Combine(BackupDir, Sanitize(key));
            return Directory.Exists(dir) && File.Exists(Path.Combine(dir, "manifest.json")) ? dir : null;
        }
        catch { return null; }
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
                    try
                    {
                        p.Kill();
                        killed++;
                        p.WaitForExit(5000);   // 等它真正退出，文件句柄才会释放
                    }
                    catch { /* 受保护/已退出：忽略，靠后续存活检查判定 */ }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 结束进程异常 {ex.Message}");
        }
        if (killed == 0 && !TrainerService.IsGameRunning() && !IsProcessAlive(exeName)) return true;

        // 不只看"最后记录的游戏"，同名的进程全部退出再注入，避免文件仍被占用
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && (TrainerService.IsGameRunning() || IsProcessAlive(exeName)))
            await Task.Delay(300);
        var ok = !TrainerService.IsGameRunning() && !IsProcessAlive(exeName);
        if (!ok) DebugLog.Write($"OptiScaler: 游戏进程 {exeName} 未完全退出，放弃注入");
        return ok;
    }

    /// <summary>按进程名判断是否仍有存活进程</summary>
    private static bool IsProcessAlive(string exeName)
    {
        try { return Process.GetProcessesByName(exeName).Length > 0; }
        catch { return false; }
    }

    /// <summary>重新打开游戏（以 exe 所在目录为工作目录）；成功返回 null，失败返回错误信息。
    /// 先走 ShellExecute（与桌面双击一致，兼容 UAC / Steam），失败再直接 CreateProcess 兜底。</summary>
    private static string? TryLaunch(string path)
    {
        var workDir = Path.GetDirectoryName(path) ?? "";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = workDir
            });
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 重新打开游戏（ShellExecute）失败 {ex.Message}");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                WorkingDirectory = workDir
            });
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiScaler: 重新打开游戏（CreateProcess）失败 {ex.Message}");
            return ex.Message;
        }
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
