using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameLocker.Services;

/// <summary>修改器取回过程中的一条进度/结果提示（Error=true 时以警告样式显示）</summary>
public readonly record struct TrainerMessage(string Text, bool Error);

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
    /// 跳过本程序自身、资源管理器外壳、修改器进程本身与无标题窗口，避免「当前游戏」被它们覆盖。
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
            if (exe.Contains("trainer", StringComparison.OrdinalIgnoreCase)) return; // 修改器自身
            if (string.Equals(exe, "explorer", StringComparison.OrdinalIgnoreCase)) return; // 桌面/任务栏
            var title = WindowText(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return; // 无标题的窗口不认作游戏
            LastExeName = exe;
            LastWindowTitle = title;
        }
        catch { /* 进程已退出 / 无权限读取：忽略本次采样 */ }
    }

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

    private static void Report(string text, bool error = false) =>
        StatusChanged?.Invoke(new TrainerMessage(text, error));

    // ---- 入口 ----

    private static bool _busy;

    /// <summary>点击/ A 键触发：按当前游戏进程自动搜索、下载并打开最新修改器（后台执行，进度经 StatusChanged 上报）</summary>
    public static void Activate()
    {
        if (_busy)
        {
            Report("正在获取修改器，请稍候…", true);
            return;
        }
        _busy = true;
        _ = ActivateAsync();
    }

    private static async Task ActivateAsync()
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

            // 已下载过当前游戏的修改器：直接打开，不再联网（要换版本可删掉下载目录里的旧版）
            if (FindDownloaded() is { } cached)
            {
                if (TryLaunch(cached) is { } cachedErr)
                {
                    Report($"无法运行修改器：{cachedErr}", true);
                    return;
                }
                Report("已打开已下载的修改器");
                HideLauncher();
                return;
            }

            Report($"正在搜索 {GameName()} 的修改器…");
            var pageUrl = await SearchTrainerAsync();
            if (pageUrl is null)
            {
                Report($"未找到 {GameName()} 对应的修改器", true);
                return;
            }

            Report("正在获取最新修改器下载地址…");
            var link = await ResolveDownloadAsync(pageUrl);
            if (link is null)
            {
                Report("未找到可下载的修改器版本", true);
                return;
            }

            Report("正在下载修改器…");
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

    /// <summary>在全部修改器 A-Z 列表中搜索最匹配当前游戏的修改器页地址（未匹配到返回 null）</summary>
    private static async Task<string?> SearchTrainerAsync()
    {
        var html = await Http.GetStringAsync(AllTrainersUrl);
        var entries = ParseTrainerLinks(html);
        if (entries.Count == 0) return null;
        return MatchBest(entries, LastWindowTitle, LastExeName);
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
    /// 逐个候选打分取最优：归一化完全相等 100 / 包含 60 / 命中词元 ×20，候选词元多于游戏词元时按超出数降权；
    /// 最高分低于 40 视为未匹配（阈值可避免"一个通用词"就误配）。
    /// </summary>
    private static string? MatchBest(List<(string Name, string Url)> entries, string title, string exe)
    {
        var gameTokens = new HashSet<string>(Tokens(title));
        foreach (var t in Tokens(exe)) gameTokens.Add(t);
        var normTitle = Norm(title);
        var normExe = Norm(exe);

        string? bestUrl = null;
        var bestScore = 0;
        foreach (var (name, url) in entries)
        {
            var cand = StripTrainerWord(name);
            var norm = Norm(cand);
            if (norm.Length < 4) continue; // 过短的候选（如 "AI"）易误配
            var candTokens = Tokens(cand);
            if (candTokens.Count == 0) continue;

            var score = 0;
            if (norm == normExe || norm == normTitle) score += 100;
            if (normTitle.Contains(norm) || normExe.Contains(norm)) score += 60;
            score += candTokens.Count(t => gameTokens.Contains(t)) * 20;
            score -= Math.Max(0, candTokens.Count - gameTokens.Count) * 10;

            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = url;
            }
        }
        return bestScore >= 40 ? bestUrl : null;
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
        if (!m.Success) return null;
        var url = m.Groups[1].Value;
        var name = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
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
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        // 防诱导弹页：正常入口最终落在 flingtrainer.com 自己的 uploads 上，指向 wemod 即异常
        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
        if (finalUrl.Contains("wemod", StringComparison.OrdinalIgnoreCase)) return null;

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var dir = Path.Combine(TrainersDir, SanitizeBaseName(versionName));
        Directory.CreateDirectory(dir);

        if (IsZip(bytes)) return ExtractTrainer(bytes, dir);
        if (!IsPe(bytes)) return null; // 既不是 zip 也不是 PE：拒绝落盘

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

    private static string WindowText(IntPtr hwnd)
    {
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
