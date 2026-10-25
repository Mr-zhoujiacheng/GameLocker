using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameLocker.Services;

/// <summary>修改器取回过程中的一条进度/结果提示（Error=true 时以警告样式显示；
/// Progress 非空时提示条下方显示小进度条，0..1）</summary>
public readonly record struct TrainerMessage(string Text, bool Error, double? Progress = null);

/// <summary>列表页里的一条候选修改器（Name=页面标题，Url=修改器页地址，Percent=折算后的匹配度百分比，Score=原始打分）</summary>
public readonly record struct TrainerCandidate(string Name, string Url, int Percent, int Score);

/// <summary>
/// 修改器小组件服务：识别最近一个「非本程序的前台游戏进程」→ 在风灵月影官方站 flingtrainer.com
/// 搜索对应修改器 → 解析出「自动更新版」下载地址 → 下载到本地并打开。
/// 启动器是常驻置顶侧边栏，点击磁贴时游戏通常不在前台，故用定时跟踪（CaptureForeground）记录最近游戏，
/// 而不是在点击时读 GetForegroundWindow。
/// </summary>
public static class TrainerService
{
    /// <summary>修改器下载目录（%APPDATA%\GameLocker\trainers）</summary>
    public static readonly string TrainersDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "trainers");

    private const string AllTrainersUrl = "https://flingtrainer.com/all-trainers-a-z/";

    /// <summary>修改器列表页链接（名称 + 地址）</summary>
    private static readonly Regex ListLinkRegex = new(
        "href=\"((?:https://flingtrainer\\.com)?/trainer/[^\"]+)\"[^>]*>([^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 修改器页里的真实下载链接：形如 <c>href="https://flingtrainer.com/downloads/&lt;opaque&gt;,," title="Enshrouded.v1.0.Plus.22.Trainer-FLiNG"</c>，
    /// 即页面上「Standalone Versions」的各个具体版本（最新版排在最前）。
    /// 注意：页面上由 trainer-page.js 注入的「Auto-Updating Version」指向 download.php → api.wemod.com，
    /// 实测返回的其实是 WeMod 安装包（伪装成 "xxx Trainer - FLiNG.exe"），故一律不使用该入口。
    /// </summary>
    private static readonly Regex DownloadLinkRegex = new(
        "href=\"(https://flingtrainer\\.com/downloads/[^\"]+)\"[^>]*?title=\"([^\"]*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        // 跟随 download.php 的 302 跳转到 api.wemod.com 取直链；浏览器 UA 降低 Cloudflare 误拦
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
        catch { /* UA 设置失败不影响使用 */ }
        return http;
    }

    // ---- 最近游戏进程跟踪 ----

    /// <summary>最近一个非本程序的前台进程名（不含扩展名）</summary>
    public static string LastExeName { get; private set; } = "";

    /// <summary>最近一个非本程序的前台窗口标题</summary>
    public static string LastWindowTitle { get; private set; } = "";

    /// <summary>
    /// 捕获当前前台窗口所属进程（由 MainWindow 定时调用，窗口隐藏期间也要持续调用）：
    /// 只在"看起来像游戏"的窗口上更新 LastExeName / LastWindowTitle。
    /// 切到系统设置、浏览器、聊天工具、Steam 等非游戏程序时，保持上一次的游戏不变 ——
    /// 这样回到主页点"Opti 注入 / 存档备份 / 修改器"时，目标仍是真正的游戏，不会误套到非游戏窗口上。
    /// </summary>
    public static void CaptureForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if ((int)pid == Environment.ProcessId) return;
            using var p = Process.GetProcessById((int)pid);
            var exe = p.ProcessName;
            if (!IsGameLikeProcess(hwnd, exe)) return;
            var title = WindowText(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return; // 无标题的窗口不认作游戏
            if (!string.Equals(LastExeName, exe, StringComparison.OrdinalIgnoreCase))
                DebugLog.Write($"修改器: 记录前台游戏 exe={exe} 标题={title}");
            LastExeName = exe;
            LastWindowTitle = title;
        }
        catch { /* 进程已退出 / 无权限读取：忽略本次采样 */ }
    }

    /// <summary>
    /// 基于进程名 + 窗口类名判断当前前台窗口是否"看起来像游戏"。
    /// 黑名单覆盖：系统核心进程、资源管理器壳/子窗、UWP 宿主、修改器自身、浏览器/聊天/启动器/设置/媒体等通用软件。
    /// 命中黑名单 → 不更新 LastExeName（让调用方继续用之前那一款游戏）。
    /// </summary>
    private static bool IsGameLikeProcess(IntPtr hwnd, string exe)
    {
        var lower = exe.ToLowerInvariant();

        // 1) 系统核心 / 资源管理器壳 —— 冻结、杀进程都不应该操作它们
        switch (lower)
        {
            case "system": case "registry": case "idle": case "csrss": case "wininit":
            case "winlogon": case "services": case "lsass": case "smss": case "dwm":
            case "audiodg": case "fontdrvhost": case "sihost": case "svchost":
            case "searchhost": case "searchindexer": case "searchprotocolhost":
            case "runtimebroker": // UWP/系统设置宿主
            case "explorer": case "startmenuexperiencehost": case "shellappruntime":
                return false;
        }

        // 2) 本程序自身及其衍生组件 —— 进程名 = GameLocker / Game Launcher
        if (lower is "gamelocker" or "gamelauncher" or "optiscalerconfig") return false;

        // 3) 修改器自身 / WeMod / FLiNG 家族
        if (lower.Contains("trainer") || lower.Contains("wemod") || lower.Contains("fling")) return false;

        // 4) 常见非游戏桌面软件 —— 命中即放行
        if (lower is
            // 启动器 / 商店 / 平台
            "steam" or "stewebhelper" or "epicgameslauncher" or "uplay" or "upc" or "goggalaxy" or "itch" or "battlenet" or "origin"
            // 浏览器
            or "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi"
            // 聊天
            or "qq" or "wechat" or "discord" or "telegram" or "skype" or "teams" or "slack" or "wemeetapp"
            // 音乐 / 视频
            or "sodamusic" or "neteasecloudmusic" or "qqmusic" or "luna" or "spotify" or "vlc" or "potplayer" or "mpv"
            // 系统设置 / 控制面板
            or "systemsettings" or "control" or "winver" or "regedit" or "taskmgr" or "resmon" or "perfmon"
            // 开发工具
            or "trae cn" or "code" or "pycharm64" or "devenv" or "studio64" or "xcode"
            // 办公
            or "explorer" /* 已在上方兜底 */ or "onenote" or "winword" or "excel" or "powerpnt" or "outlook"
            )
            return false;

        // 5) 窗口类名黑名单 —— 系统托盘气泡 / 通知中心 / 任务小组件 / 输入法候选窗
        if (hwnd != IntPtr.Zero)
        {
            var cls = GetClassName(hwnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "Shell_CompatTrayWnd" or "TrayAlert" or "ToastWindow" or "IMMNotificationWindow"
                or "IndicatorUIWndClass")
                return false;
        }

        // 6) 默认放行：任何其它有窗口的 EXE 都被视为"游戏候选"
        return true;
    }

    /// <summary>
    /// 最近记录的游戏是否仍是"当前在玩的游戏"，三个小组件（修改器 / 游戏存档 / OptiScaler）的入口都先查这个。
    /// 判定：前台就是该游戏；或前台是本启动器（开始菜单模式下必须点/按它才能操作磁贴）；
    /// 或前台只是短暂遮挡游戏的覆盖层/系统外壳（Steam 覆盖、截图录屏工具、UWP 宿主、开始菜单等）。
    /// 用户切到设置/浏览器/商店等真正的其他程序时仍然拒绝，避免操作到错误的游戏。
    /// </summary>
    public static bool IsLastGameActuallyForeground()
    {
        if (string.IsNullOrEmpty(LastExeName)) return false;
        if (!IsGameRunning()) return false;
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            var id = (int)pid;
            if (id == Environment.ProcessId) return true;   // 本启动器在前台：游戏就在它后面
            using var p = Process.GetProcessById(id);
            if (string.Equals(p.ProcessName, LastExeName, StringComparison.OrdinalIgnoreCase)) return true;

            var pass = IsTransientForeground(p.ProcessName);
            DebugLog.Write($"修改器: 前台校验 {p.ProcessName}（期望 {LastExeName}）→ {(pass ? "放行" : "拒绝")}");
            return pass;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"修改器: 前台校验异常 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>短暂遮挡游戏的窗口：Steam / Xbox 覆盖层、截图录屏工具、系统外壳与开始菜单。
    /// 它们在前台不代表用户切到了别的游戏（目标游戏仍由最近记录决定）。</summary>
    private static bool IsTransientForeground(string exe) => exe.ToLowerInvariant() is
        "steamwebhelper" or "gameoverlayui" or "snippingtool" or "screenclippinghost"
        or "applicationframehost" or "shellexperiencehost" or "startmenuexperiencehost"
        or "searchhost" or "textinputhost" or "explorer" or "gamebar" or "xboxgamebarwidgets";

    /// <summary>最近记录的游戏进程是否仍在运行</summary>
    public static bool IsGameRunning()
    {
        if (string.IsNullOrEmpty(LastExeName)) return false;
        try { return Process.GetProcessesByName(LastExeName).Length > 0; }
        catch { return false; }
    }

    /// <summary>用于提示文案的游戏名（优先窗口标题，回退进程名）</summary>
    private static string GameName() =>
        string.IsNullOrWhiteSpace(LastWindowTitle) ? LastExeName : LastWindowTitle;

    /// <summary>当前游戏显示名（供存档定位复用；无游戏进程时为空串）</summary>
    public static string CurrentGameName() => GameName();

    /// <summary>当前游戏可执行文件完整路径（供存档定位复用；取不到返回 null）</summary>
    public static string? CurrentGameExePath()
    {
        var p = GameInstallPath();
        return p.Length == 0 ? null : p;
    }

    // ---- 状态上报 ----

    /// <summary>取回过程中的提示（可能来自后台线程，订阅方自行 marshal 到 UI 线程）</summary>
    public static event Action<TrainerMessage>? StatusChanged;

    /// <summary>
    /// 精确匹配失败但存在模糊候选时触发（后台线程）。交由 UI 层弹窗让用户选择，选中后调用 DownloadChosen；
    /// 不在这里直接弹窗，是为了不给服务层引入 UI 依赖。
    /// </summary>
    public static event Action<IReadOnlyList<TrainerCandidate>>? CandidatesReady;

    private static void Report(string text, bool error = false, double? progress = null) =>
        StatusChanged?.Invoke(new TrainerMessage(text, error, progress));

    /// <summary>下载进度 → 提示条文案（带 MB 数值）+ 小进度条取值；总长度未知时只报体积、不显示进度条</summary>
    private static void ReportDownload(string what, long got, long? total) =>
        Report(total is > 0
                ? $"正在下载{what}… {DownloadProgress.Mb(got)}/{DownloadProgress.Mb(total.Value)} MB（{got * 100 / total.Value}%）"
                : $"正在下载{what}… 已接收 {DownloadProgress.Mb(got)} MB",
            progress: total is > 0 ? (double)got / total.Value : null);

    // ---- 入口 ----

    private static bool _busy;

    /// <summary>点击/ A 键触发：按当前游戏进程自动搜索、下载并打开最新修改器（后台执行，进度经 StatusChanged 上报）</summary>
    public static void Activate() => Start(null);

    /// <summary>用户在候选弹窗里选定后调用：按所选修改器页继续解析、下载并打开</summary>
    public static void DownloadChosen(TrainerCandidate candidate) => Start(candidate);

    private static void Start(TrainerCandidate? chosen)
    {
        if (_busy)
        {
            Report("正在获取修改器，请稍候…", true);
            return;
        }
        _busy = true;
        _ = ActivateAsync(chosen);
    }

    private static async Task ActivateAsync(TrainerCandidate? chosen)
    {
        try
        {
            if (string.IsNullOrEmpty(LastExeName))
            {
                Report("未检测到游戏进程，请先运行游戏再点此磁贴", true);
                return;
            }
            if (!IsGameRunning())
            {
                Report($"游戏 {GameName()} 已退出，请先运行游戏", true);
                return;
            }
            if (!IsLastGameActuallyForeground())
            {
                Report($"请先把游戏「{GameName()}」切到前台再获取修改器", true);
                return;
            }

            DebugLog.Write($"修改器: 触发 exe={LastExeName} 标题={LastWindowTitle}");

            // 已下载过当前游戏的修改器：直接打开，不再联网（要换版本可删掉下载目录里的旧版）
            if (FindDownloaded() is { } cached)
            {
                DebugLog.Write($"修改器: 命中已下载的修改器 {cached}");
                if (TryLaunch(cached) is { } cachedErr)
                {
                    Report($"无法运行修改器：{cachedErr}", true);
                    return;
                }
                Report("已打开已下载的修改器");
                HideLauncher();
                return;
            }

            string? pageUrl;
            if (chosen is { } pick)
            {
                pageUrl = pick.Url; // 用户已在候选弹窗里选定，直接用该修改器页
            }
            else
            {
                Report($"正在搜索 {GameName()} 的修改器…");
                var (best, candidates) = await SearchAsync();
                if (best is null)
                {
                    if (candidates.Count > 0)
                    {
                        // 精确匹配失败：把模糊候选交给 UI 弹窗让用户自己挑。风灵月影列表的写法常与
                        // 窗口标题差很远（中文标题尤其），靠字符串自动选准是不可能的，交给人最稳。
                        DebugLog.Write($"修改器: 无精确匹配，提供 {candidates.Count} 条模糊候选供用户选择");
                        Report($"没有完全匹配的修改器，请从 {candidates.Count} 条相似结果中选择");
                        CandidatesReady?.Invoke(candidates);
                        return;
                    }
                    DebugLog.Write("修改器: 列表页未匹配到候选（看上面 links / 最高分 两行日志）");
                    Report($"未找到 {GameName()} 对应的修改器", true);
                    return;
                }
                pageUrl = best;
            }

            Report("正在获取最新修改器下载地址…");
            var link = await ResolveDownloadAsync(pageUrl);
            if (link is null)
            {
                Report("未找到可下载的修改器版本", true);
                return;
            }

            Report("正在下载修改器…", progress: 0);
            var file = await DownloadAsync(link.Value.Url, pageUrl, link.Value.Name);
            if (file is null)
            {
                Report("修改器下载失败，请检查网络后重试", true);
                return;
            }
            RememberDownload(file); // 登记：下次点此磁贴直接打开

            Report("下载完成，正在打开修改器…");
            if (TryLaunch(file) is { } err)
            {
                Report($"无法运行修改器：{err}", true);
                return;
            }

            Report("已打开最新修改器");
            HideLauncher();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"修改器: 异常 {ex.GetType().Name}: {ex.Message}\n{ex}");
            Report("修改器获取失败：" + ex.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>收起启动器：常驻置顶侧边栏会遮挡修改器窗口（HideToTray 需回 UI 线程执行）</summary>
    private static void HideLauncher()
    {
        var main = App.Main;
        main?.DispatcherQueue.TryEnqueue(main.HideToTray);
    }

    /// <summary>启动修改器（以 exe 所在目录为工作目录）；成功返回 null，失败返回错误信息</summary>
    private static string? TryLaunch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? TrainersDir
            });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---- 已下载登记 / 目录 / 关闭 ----

    /// <summary>游戏可执行名(小写) → 已下载修改器 exe（相对 trainers 目录）</summary>
    private static readonly string MapFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "trainer-map.json");

    private static Dictionary<string, string> LoadMap()
    {
        try
        {
            if (File.Exists(MapFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MapFile)) ?? [];
        }
        catch { /* 登记损坏：当作空表 */ }
        return [];
    }

    private static void SaveMap(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapFile)!);
            File.WriteAllText(MapFile, JsonSerializer.Serialize(map));
        }
        catch { /* 落盘失败不影响本次使用 */ }
    }

    /// <summary>当前游戏是否已下载过修改器；有则返回 exe 全路径（文件已被删则清掉登记）</summary>
    private static string? FindDownloaded()
    {
        if (string.IsNullOrEmpty(LastExeName)) return null;
        var key = LastExeName.ToLowerInvariant();
        var map = LoadMap();
        if (!map.TryGetValue(key, out var rel)) return null;

        var path = Path.Combine(TrainersDir, rel);
        if (File.Exists(path)) return path;

        map.Remove(key);
        SaveMap(map);
        return null;
    }

    /// <summary>登记「当前游戏 → 已下载修改器」（下次点击直接打开）</summary>
    private static void RememberDownload(string exePath)
    {
        if (string.IsNullOrEmpty(LastExeName)) return;
        var map = LoadMap();
        map[LastExeName.ToLowerInvariant()] = Path.GetRelativePath(TrainersDir, exePath);
        SaveMap(map);
    }

    /// <summary>打开存放修改器的目录（资源管理器）；目录不存在时先建出来</summary>
    public static void OpenTrainersFolder()
    {
        try
        {
            Directory.CreateDirectory(TrainersDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{TrainersDir}\"",
                UseShellExecute = true
            });
        }
        catch { /* 打不开就算了，不打断编辑流程 */ }
    }

    /// <summary>关闭正在运行的修改器（长按磁贴 / Y 键），返回关闭数量。
    /// 判定：进程模块在 trainers 目录下、或登记表里出现过、或进程名含 trainer/fling。</summary>
    public static int CloseTrainer()
    {
        var known = new HashSet<string>(
            LoadMap().Values.Select(v => Path.GetFullPath(Path.Combine(TrainersDir, v))),
            StringComparer.OrdinalIgnoreCase);

        var killed = 0;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId) continue;
                    var path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch { /* 受保护进程读不到路径 */ }

                    var isTrainer =
                        (path.Length > 0 && (known.Contains(path) ||
                                             path.StartsWith(TrainersDir, StringComparison.OrdinalIgnoreCase)))
                        || p.ProcessName.Contains("trainer", StringComparison.OrdinalIgnoreCase)
                        || p.ProcessName.Contains("fling", StringComparison.OrdinalIgnoreCase);
                    if (!isTrainer) continue;

                    p.Kill();
                    killed++;
                }
                catch { /* 已退出/无权限：跳过 */ }
            }
        }
        return killed;
    }

    // ---- 搜索 / 解析 / 下载 ----

    /// <summary>
    /// 抓取 A-Z 列表页 → 打分取最优；最优低于阈值时另给一份低阈值候选列表供用户选择。
    /// 匹配用的名字有三个来源：窗口标题、进程名、Steam 安装目录名 —— 中文标题（如「破门而入：行动小队」）
    /// 分词后为空，只有安装目录名（Door Kickers - Action Squad）能提供英文名，等于自动完成了「中文→英文」。
    /// </summary>
    private static async Task<(string? Best, List<TrainerCandidate> Candidates)> SearchAsync()
    {
        using var resp = await Http.GetAsync(AllTrainersUrl);
        var html = await resp.Content.ReadAsStringAsync();
        DebugLog.Write($"修改器: 列表页 status={(int)resp.StatusCode} len={html.Length}");
        resp.EnsureSuccessStatusCode(); // 与 GetStringAsync 一致：非 2xx 抛错

        var entries = ParseTrainerLinks(html);
        DebugLog.Write($"修改器: 列表页解析到 links={entries.Count}");

        var keys = SearchKeys();
        var best = MatchBest(entries, keys, out var score, out var bestName);
        DebugLog.Write($"修改器: 匹配 名称候选=[{string.Join(" / ", keys)}] → " +
            (best is null ? "无命中" : bestName) + $"（最高分={score}，阈值 40）");
        return (best, best is null ? RankCandidates(entries, keys) : []);
    }

    /// <summary>用于匹配的名字：窗口标题、进程名、Steam 安装目录名（去空、去重）</summary>
    private static List<string> SearchKeys()
    {
        var keys = new List<string> { LastWindowTitle.Trim(), LastExeName.Trim() };
        var dir = InstallDirName();
        if (dir.Length > 0) keys.Add(dir);
        return keys.Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Steam 安装目录名（&lt;库&gt;\steamapps\common\&lt;目录&gt;）；非 Steam 游戏返回空串</summary>
    private static string InstallDirName()
    {
        const string marker = @"\steamapps\common\";
        var path = GameInstallPath();
        var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        return path[(idx + marker.Length)..]
            .Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    /// <summary>正则解析列表页中的 (名称, 修改器页绝对地址)，按地址去重</summary>
    private static List<(string Name, string Url)> ParseTrainerLinks(string html)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ListLinkRegex.Matches(html))
        {
            var url = m.Groups[1].Value;
            if (url.StartsWith('/')) url = "https://flingtrainer.com" + url; // 相对地址补全
            var name = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (name.Length == 0 || !seen.Add(url)) continue;
            list.Add((name, url));
        }
        return list;
    }

    /// <summary>
    /// 逐个候选打分取最优：归一化与任一名字相等 100 / 双向包含 60 / 命中词元 ×20，候选词元多于游戏词元时按超出数降权；
    /// 最高分低于 40 视为未匹配（阈值可避免"一个通用词"就误配）。
    /// </summary>
    private static string? MatchBest(
        List<(string Name, string Url)> entries, IReadOnlyList<string> keys,
        out int bestScore, out string bestName)
    {
        var (gameTokens, norms) = Tokenize(keys);

        string? bestUrl = null;
        bestName = "";
        bestScore = 0;
        foreach (var (name, url) in entries)
        {
            var cand = StripTrainerWord(name);
            var norm = Norm(cand);
            if (norm.Length < 4) continue; // 过短的候选（如 "AI"）易误配
            var candTokens = Tokens(cand);
            if (candTokens.Count == 0) continue;

            var score = 0;
            if (norms.Contains(norm)) score += 100;
            // 双向包含都是很强的信号：窗口标题常常只是简称（"Dawnwalker"），而列表里是全名
            // （"The Blood of Dawnwalker"）；反过来列表名也可能只是标题的一部分，两个方向都要认。
            if (norms.Any(n => n.Contains(norm) || norm.Contains(n))) score += 60;
            score += candTokens.Count(t => gameTokens.Contains(t)) * 20;
            score -= Math.Max(0, candTokens.Count - gameTokens.Count) * 10;

            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = url;
                bestName = name;
            }
        }
        return bestScore >= 40 ? bestUrl : null;
    }

    /// <summary>把若干个名字汇总为「词元集合 + 归一化集合」，供精确打分与候选排序复用</summary>
    private static (HashSet<string> Tokens, List<string> Norms) Tokenize(IReadOnlyList<string> keys)
    {
        var tokens = new HashSet<string>();
        var norms = new List<string>();
        foreach (var k in keys)
        {
            foreach (var t in Tokens(k)) tokens.Add(t);
            var n = Norm(k);
            if (n.Length > 0) norms.Add(n);
        }
        return (tokens, norms);
    }

    /// <summary>
    /// 精确匹配失败时的低阈值候选（词元重合 ×20 + 双向包含 40，只留得分 &gt; 0 的，取前 15 条）。
    /// 目的不是自动选对，而是挑出「最像的几条」交人工确认。得分会折算成 0-100 的匹配度百分比：
    /// 以「本查询能拿到的满分」为分母（名称全等 100 + 双向包含 60 + 每个游戏词元命中 20），故数值有绝对含义，
    /// 而非仅列表内的相对高低。
    /// </summary>
    private static List<TrainerCandidate> RankCandidates(
        List<(string Name, string Url)> entries, IReadOnlyList<string> keys)
    {
        var (gameTokens, norms) = Tokenize(keys);
        var ideal = 160 + 20 * Math.Max(1, gameTokens.Count); // 完美匹配的分数：全等+包含+命中全部词元
        var list = new List<TrainerCandidate>();
        foreach (var (name, url) in entries)
        {
            var cand = StripTrainerWord(name);
            var norm = Norm(cand);
            if (norm.Length < 4) continue;
            var candTokens = Tokens(cand);
            if (candTokens.Count == 0) continue;

            var score = candTokens.Count(t => gameTokens.Contains(t)) * 20;
            if (norms.Any(n => n.Contains(norm) || norm.Contains(n))) score += 40;
            if (score <= 0) continue;
            var percent = Math.Clamp((int)Math.Round(score * 100.0 / ideal), 1, 99);
            list.Add(new TrainerCandidate(name, url, percent, score));
        }
        return list.OrderByDescending(c => c.Score).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                   .Take(15).ToList();
    }

    /// <summary>去掉候选名中单独的 "trainer" 词（列表中名称形如 "Enshrouded Trainer"）</summary>
    private static string StripTrainerWord(string name) =>
        Regex.Replace(name, @"\btrainer\b", " ", RegexOptions.IgnoreCase).Trim();

    /// <summary>归一化：仅保留字母数字并转小写（"Enshrouded™" 与 "enshrouded" 视为相同）</summary>
    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>切词元：按非字母数字切分，剔除长度 1、冠词与 "trainer"（用于词元重合度打分）</summary>
    private static List<string> Tokens(string s) =>
        s.ToLowerInvariant()
         .Split([' ', '-', '_', ':', '.', '™', '®', '©', '!', '?', ',', '\'', '"', '(', ')', '[', ']', '/', '\\', '+', '&'],
                StringSplitOptions.RemoveEmptyEntries)
         .Where(t => t.Length >= 2 && t is not ("the" or "of" or "and" or "trainer"))
         .ToList();

    /// <summary>
    /// 取修改器页里第一条真实下载链接（列表按新版本在前排列，故取第一条即最新版），返回 (下载地址, 版本名)。
    /// 页面可能有多个版本行，只认 /downloads/ 这条真实入口。
    /// </summary>
    private static async Task<(string Url, string Name)?> ResolveDownloadAsync(string trainerPageUrl)
    {
        var html = await Http.GetStringAsync(trainerPageUrl);
        var m = DownloadLinkRegex.Match(html);
        DebugLog.Write($"修改器: 修改器页 len={html.Length} 下载链接解析={(m.Success ? "成功" : "失败")}");
        if (!m.Success) return null;
        var url = m.Groups[1].Value;
        var name = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
        DebugLog.Write($"修改器: 下载地址 {url}（{name}）");
        return (url, string.IsNullOrWhiteSpace(name) ? "Trainer-FLiNG" : name);
    }

    /// <summary>
    /// 下载修改器本体 → 落盘并返回可直接运行的 exe 路径。
    /// 必须带 Referer（该站对 /downloads/ 做了防盗链，缺 Referer 会 403）；拒绝 WeMod 诱导弹页与非 PE 载荷。
    /// 服务端可能直接回内层 exe（MZ），也可能回整个 zip（PK），且 zip 内可能含多个按「商店/位数」区分的变体，
    /// 此时按当前游戏的安装路径与系统位数挑最匹配的一个，其余变体一并解压到同目录备用。
    /// </summary>
    private static async Task<string?> DownloadAsync(string url, string referer, string versionName)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(referer); // 防盗链：无 Referer 会 403
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        DebugLog.Write($"修改器: 下载 status={(int)resp.StatusCode} 最终地址={resp.RequestMessage?.RequestUri}");
        if (!resp.IsSuccessStatusCode) return null;

        // 防诱导弹页：正常入口最终落在 flingtrainer.com 自己的 uploads 上，指向 wemod 即异常
        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
        if (finalUrl.Contains("wemod", StringComparison.OrdinalIgnoreCase))
        {
            DebugLog.Write("修改器: 最终地址指向 wemod，判定为诱导弹页 → 放弃");
            return null;
        }

        // 边收边报进度：修改器包通常几 MB，先落到内存再判 zip/PE（两种载荷都要按字节甄别）
        using var buffer = new MemoryStream();
        await using (var net = await resp.Content.ReadAsStreamAsync())
            await DownloadProgress.CopyAsync(net, resp.Content.Headers.ContentLength, buffer,
                (got, total) => ReportDownload("修改器", got, total));
        var bytes = buffer.ToArray();
        DebugLog.Write($"修改器: 下载完成 bytes={bytes.Length}");
        var dir = Path.Combine(TrainersDir, SanitizeBaseName(versionName));
        Directory.CreateDirectory(dir);

        if (IsZip(bytes)) return ExtractTrainer(bytes, dir);
        if (!IsPe(bytes))
        {
            DebugLog.Write("修改器: 载荷既非 zip 也非 PE → 拒绝落盘");
            return null; // 既不是 zip 也不是 PE：拒绝落盘
        }

        var pePath = Path.Combine(dir, SanitizeBaseName(versionName) + ".exe");
        await File.WriteAllBytesAsync(pePath, bytes);
        return pePath;
    }

    private static bool IsZip(byte[] b) => b.Length >= 2 && b[0] == 0x50 && b[1] == 0x4B;

    private static bool IsPe(byte[] b) => b.Length >= 2 && b[0] == 0x4D && b[1] == 0x5A;

    /// <summary>解压 zip 中全部 exe 变体到 dir，返回被选中启动的那个路径；无 exe 则 null</summary>
    private static string? ExtractTrainer(byte[] zipBytes, string dir)
    {
        var candidates = new List<(string Path, string Name, long Size)>();
        using (var ms = new MemoryStream(zipBytes))
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
        {
            foreach (var e in zip.Entries)
            {
                if (e.Length <= 0) continue;
                if (!e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var entryName = Path.GetFileName(e.FullName); // 去掉可能存在的目录前缀
                var dest = Path.Combine(dir, SanitizeBaseName(entryName) + ".exe");
                using (var src = e.Open())
                using (var dst = File.Create(dest)) src.CopyTo(dst);
                candidates.Add((dest, entryName, e.Length));
            }
        }
        if (candidates.Count == 0) return null;
        return PickBestVariant(candidates).Path;
    }

    /// <summary>
    /// 在多个变体中挑最匹配当前游戏的一个：按变体名里的商店线索（steam/gog/epic/game pass）比对游戏安装路径，
    /// 再按系统位数偏好 64 位；线索相同取体积大的。取不到游戏路径时退化为「64 位偏好 + 体积最大」。
    /// </summary>
    private static (string Path, string Name, long Size) PickBestVariant(
        List<(string Path, string Name, long Size)> candidates)
    {
        var gamePath = GameInstallPath().ToLowerInvariant();
        var x64 = Environment.Is64BitOperatingSystem;

        (string Path, string Name, long Size) best = candidates[0];
        var bestScore = int.MinValue;
        foreach (var c in candidates)
        {
            var n = c.Name.ToLowerInvariant();
            var score = 0;
            if (gamePath.Length > 0)
            {
                if (n.Contains("steam") && gamePath.Contains("steamapps")) score += 5;
                if (n.Contains("gog") && gamePath.Contains("gog")) score += 5;
                if (n.Contains("epic") && gamePath.Contains("epic")) score += 5;
                if ((n.Contains("game pass") || n.Contains("gamepass")) &&
                    (gamePath.Contains("windowsapps") || gamePath.Contains("xbox") || gamePath.Contains("gamepass")))
                    score += 5;
            }
            if (x64)
            {
                if (n.Contains("64")) score += 2;
                if (n.Contains("32")) score -= 1;
            }
            // 同分时体积大的优先（candidates 按 zip 内顺序，比较需带上体积打破平局）
            if (score > bestScore || (score == bestScore && c.Size > best.Size))
            {
                best = c;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>当前游戏进程的安装路径（用于按商店线索挑变体；取不到返回空串）</summary>
    private static string GameInstallPath()
    {
        if (string.IsNullOrEmpty(LastExeName)) return "";
        try
        {
            foreach (var p in Process.GetProcessesByName(LastExeName))
            {
                using (p)
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                    catch { /* 进程受保护/已退出：换下一个 */ }
                }
            }
        }
        catch { /* 枚举失败：返回空，走退化规则 */ }
        return "";
    }

    /// <summary>整理成合法文件/文件夹基名：非法字符替换、去掉扩展名与首尾空白（不做 .exe 补全）</summary>
    private static string SanitizeBaseName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Length == 0 ? "Trainer-FLiNG" : s;
    }

    // ---- Win32 ----

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private static string WindowText(IntPtr hwnd)
    {
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
