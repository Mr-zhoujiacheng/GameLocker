using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GameLocker.Models;
using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>
/// 小黄鸭（Lossless Scaling，Steam appid 993090）联动小组件的领域逻辑：
/// 检测安装位置（手动指定优先，其次按 Steam 库自动查找）、启动/唤醒、查询运行状态，
/// 以及"开关插帧"——启动小黄鸭并最小化后，把前台切回按 A 之前的那款游戏，
/// 再发送小黄鸭的默认补帧热键（Ctrl+Alt+S）；再按一次 A 即发送同一热键关闭插帧。
/// 不读写小黄鸭的配置与画质档；游戏需为无边框窗口化，插帧才会生效。
/// </summary>
public static class LosslessScalingService
{
    /// <summary>Steam 商店 appid（Lossless Scaling）</summary>
    private const string SteamAppId = "993090";

    /// <summary>小黄鸭默认补帧热键：Ctrl+Alt+S</summary>
    private const int ScaleHotkeyMods = KeySendService.ModCtrl | KeySendService.ModAlt;
    private const int ScaleHotkeyKey = 0x53;

    /// <summary>开关插帧的结果（供调用方决定提示文案）</summary>
    public enum ScaleResult
    {
        /// <summary>未检测到小黄鸭：既没手动指定，Steam 库里也没找到</summary>
        NotInstalled,
        /// <summary>启动失败（路径不可执行 / Steam 未安装）</summary>
        LaunchFailed,
        /// <summary>没找到可缩放的游戏窗口：只打开并最小化小黄鸭，不发送热键</summary>
        NoGame,
        /// <summary>游戏窗口切不回前台（被前台锁定拒绝）：不发送热键，避免缩放到错误窗口</summary>
        FocusFailed,
        /// <summary>已发送热键：插帧已开启</summary>
        FrameGenOn,
        /// <summary>已发送热键：插帧已关闭</summary>
        FrameGenOff
    }

    private static string? _detectedExe;
    private static bool _detectedOnce;
    private static int _activating;

    /// <summary>最近一次由本组件切换的插帧状态（小黄鸭启动时视为关闭；仅用于磁贴提示与文案）</summary>
    public static bool FrameGenOn { get; private set; }

    /// <summary>是否正在执行开关插帧（防重入）</summary>
    public static bool IsActivating => Volatile.Read(ref _activating) == 1;

    /// <summary>
    /// 最近一次打开/收起小黄鸭时是否没能最小化它的窗口。
    /// 小黄鸭若以管理员身份运行（它自己的"以管理员身份启动"选项），普通权限的启动器无法操作它的窗口。
    /// </summary>
    public static bool MinimizeBlocked { get; private set; }

    /// <summary>进程不在运行时把插帧记录清零（磁贴刷新前调用，避免显示过期的"插帧开"）</summary>
    public static void SyncState()
    {
        if (!IsRunning) FrameGenOn = false;
    }

    /// <summary>是否已安装：手动路径存在，或 Steam 库中检测到 LosslessScaling.exe</summary>
    public static bool IsInstalled(LaunchItem item) => DetectExe(item) is not null;

    /// <summary>小黄鸭进程是否正在运行</summary>
    public static bool IsRunning
    {
        get
        {
            try
            {
                var ps = Process.GetProcessesByName("LosslessScaling");
                try { return ps.Length > 0; }
                finally { foreach (var p in ps) p.Dispose(); }
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 解析可执行文件路径：手动指定优先（支持直接给目录），否则按 Steam 安装信息查找并缓存。
    /// </summary>
    public static string? DetectExe(LaunchItem item)
    {
        var manual = item.LosslessPath;
        if (!string.IsNullOrWhiteSpace(manual))
        {
            var path = manual.Trim();
            try
            {
                if (Directory.Exists(path)) path = Path.Combine(path, "LosslessScaling.exe");
                if (File.Exists(path)) return path;
            }
            catch { /* 非法路径：继续走自动检测 */ }
        }

        if (_detectedOnce) return _detectedExe;
        _detectedOnce = true;
        _detectedExe = DetectFromSteam();
        DebugLog.Write($"小黄鸭: 自动检测结果={_detectedExe ?? "(未安装)"}");
        return _detectedExe;
    }

    /// <summary>清除自动检测缓存（编辑页改了手动路径后调用，让磁贴立即反映状态）</summary>
    public static void InvalidateCache()
    {
        _detectedOnce = false;
        _detectedExe = null;
    }

    // ---- 配置（Game Profiles）----

    /// <summary>小黄鸭用户配置：游戏配置名 + 绑定的可执行文件（Path 为空=全局配置）</summary>
    public sealed record ProfileInfo(string Title, string? Path);

    /// <summary>小黄鸭的 Settings.xml（保存全部 Game Profiles）</summary>
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lossless Scaling", "Settings.xml");

    /// <summary>读取小黄鸭的配置列表（按 Settings.xml 顺序；未安装/读取失败返回空）</summary>
    public static IReadOnlyList<ProfileInfo> ReadProfiles()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return [];
            var doc = XDocument.Load(SettingsPath);
            return doc.Root?.Element("GameProfiles")?.Elements("Profile")
                .Select(p => new ProfileInfo(
                    ((string?)p.Element("Title"))?.Trim() ?? "",
                    ((string?)p.Element("Path"))?.Trim()))
                .Where(p => p.Title.Length > 0)
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lossless] 读取配置失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 把选中的配置参数应用到当前游戏：优先写进与游戏 exe/标题匹配的配置，
    /// 没有匹配项则写进「默认」配置。Title/Path 保留目标自己的，AutoScale 系列也不复制，
    /// 避免小黄鸭在发送热键前就自动开启缩放（那样 A 键反而会把它关掉）。
    /// 写文件须在小黄鸭启动前完成，运行中的实例不会重读配置。
    /// </summary>
    public static bool ApplyProfile(string title, ForegroundWatcherService.ForegroundApp? app)
    {
        if (string.IsNullOrWhiteSpace(title) || app is null) return false;
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var doc = XDocument.Load(SettingsPath);
            var profiles = doc.Root?.Element("GameProfiles")?.Elements("Profile").ToList();
            if (profiles is null || profiles.Count == 0) return false;

            var selected = profiles.FirstOrDefault(p => string.Equals(
                ((string?)p.Element("Title"))?.Trim(), title, StringComparison.OrdinalIgnoreCase));
            if (selected is null) return false; // 配置已在小黄鸭里被删除

            var target = FindProfileForGame(profiles, app)
                ?? profiles.FirstOrDefault(p => string.Equals(
                    ((string?)p.Element("Title"))?.Trim(), "默认", StringComparison.OrdinalIgnoreCase));
            if (target is null) return false;
            if (ReferenceEquals(target, selected)) return true; // 小黄鸭本来就会用这个配置

            // 目标配置的 Title/Path/AutoScale 保持原样，其余参数整体替换为选中配置的
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Title", "Path", "AutoScale", "AutoScaleDelay"
            };
            foreach (var e in target.Elements().Where(e => !keep.Contains(e.Name.LocalName)).ToList())
                e.Remove();
            foreach (var e in selected.Elements().Where(e => !keep.Contains(e.Name.LocalName)))
                target.Add(new XElement(e));

            SaveSettings(doc);
            DebugLog.Write($"小黄鸭: 应用配置「{title}」→「{(string?)target.Element("Title")}」（游戏={app.ProcessName}）");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"小黄鸭: 应用配置失败「{title}」：{ex.Message}");
            return false;
        }
    }

    /// <summary>找出与前台游戏匹配的配置：Path 可是完整路径、exe 文件名或窗口标题（与小黄鸭自身规则一致）</summary>
    private static XElement? FindProfileForGame(
        List<XElement> profiles, ForegroundWatcherService.ForegroundApp app)
    {
        var exeName = app.ProcessName + ".exe";
        foreach (var p in profiles)
        {
            var path = ((string?)p.Element("Path"))?.Trim();
            if (string.IsNullOrEmpty(path)) continue;
            if (!string.IsNullOrWhiteSpace(app.ExePath)
                && string.Equals(path, app.ExePath, StringComparison.OrdinalIgnoreCase))
                return p;
            var file = FileNameOf(path);
            if (string.Equals(file, exeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, app.ProcessName, StringComparison.OrdinalIgnoreCase))
                return p;
            if (string.Equals(path, app.Title, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    /// <summary>把 Path 里的文件名取出来（配置里可能只写 exe 名，也可能是完整路径）</summary>
    private static string FileNameOf(string path)
    {
        try { return Path.GetFileName(path.Replace('/', '\\')); }
        catch { return ""; }
    }

    /// <summary>保存 Settings.xml（缩进与编码保持小黄鸭可读；写失败向上抛给调用方记录日志）</summary>
    private static void SaveSettings(XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false)
        };
        using var stream = File.Create(SettingsPath);
        using var writer = XmlWriter.Create(stream, settings);
        doc.Save(writer);
    }

    /// <summary>启动/唤醒小黄鸭：手动指定或检测到的 exe 直接启动，否则交给 Steam 协议拉起</summary>
    public static bool Launch(LaunchItem item)
    {
        var exe = DetectExe(item);
        if (exe is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                return true;
            }
            catch (Exception ex) { Debug.WriteLine($"[Lossless] 启动失败: {ex.Message}"); }
        }

        // 回退：Steam 已安装本游戏时用协议启动（exe 路径找不到时仍可拉起）
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{SteamAppId}",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lossless] Steam 协议启动失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 开关插帧（须在 UI 线程调用）：先记下目标游戏窗口 → 未运行时启动小黄鸭并等它初始化 →
    /// 收起启动器与小黄鸭窗口 → 把游戏切回前台 → 发送 Ctrl+Alt+S。每次按下都在开/关之间切换。
    /// 没找到游戏窗口时只打开小黄鸭，不发热键（热键只会缩放当前前台窗口）。
    /// </summary>
    public static async Task<ScaleResult> ActivateAsync(LaunchItem item)
    {
        if (Interlocked.Exchange(ref _activating, 1) == 1) return ScaleResult.FrameGenOn;
        try
        {
            if (!IsInstalled(item)) return ScaleResult.NotInstalled;

            // 必须在启动小黄鸭之前确定目标游戏窗口：小黄鸭一启动就会抢占前台，
            // 前台记录随之变成小黄鸭自己，之后再取会把热键发给小黄鸭（表现为"只打开了小黄鸭"）。
            var app = ForegroundWatcherService.Current;
            var target = FindTargetGameWindow(app, out var targetName);
            var running = IsRunning;

            // 选中的配置要在小黄鸭启动前写进 Settings.xml；已在运行时它不会重读，留待下次启动生效。
            if (!running && target != IntPtr.Zero && !string.IsNullOrWhiteSpace(item.LosslessProfile))
                ApplyProfile(item.LosslessProfile, app);

            if (!running)
            {
                var launchedAt = Environment.TickCount64; // 从发起启动开始计时（含冷启动全过程）
                if (!Launch(item)) return ScaleResult.LaunchFailed;
                if (!await WaitRunningAsync(12000)) return ScaleResult.LaunchFailed;
                FrameGenOn = false; // 刚启动的小黄鸭尚未开启插帧

                // 等主窗口出现后，再留约 2 秒给小黄鸭初始化热键监听（冷启动通常要几秒）
                var windowReady = await WaitWindowReadyAsync(8000);
                if (windowReady) await Task.Delay(2000);
                var elapsed = Environment.TickCount64 - launchedAt;
                if (elapsed < 4000) await Task.Delay((int)(4000 - elapsed));
                DebugLog.Write($"小黄鸭: 启动就绪 窗口={windowReady} 用时={Environment.TickCount64 - launchedAt}ms 目标={targetName}");
            }

            // 没有可缩放的游戏窗口：热键发了也没用（小黄鸭只会缩放前台窗口），只打开并最小化
            if (target == IntPtr.Zero || !IsWindow(target))
            {
                MinimizeBlocked = !MinimizeWindows();
                DebugLog.Write($"小黄鸭: 未找到游戏窗口（目标={targetName}），已打开并最小化（最小化失败={MinimizeBlocked}），不发送热键");
                return ScaleResult.NoGame;
            }

            // 收起启动器与小黄鸭主窗口，再把游戏切回前台（小黄鸭缩放的是当前前台窗口）
            App.Main?.HideToTray();
            MinimizeBlocked = !MinimizeWindows();
            await Task.Delay(150);
            BringToForeground(target);
            await Task.Delay(450);

            // 前台必须真的是游戏：否则热键会让小黄鸭去缩放别的窗口（例如它自己）
            if (GetForegroundWindow() != target)
            {
                BringToForeground(target);
                await Task.Delay(300);
            }
            if (GetForegroundWindow() != target)
            {
                DebugLog.Write($"小黄鸭: 切回游戏窗口失败（目标={targetName}），未发送热键");
                return ScaleResult.FocusFailed;
            }

            var injected = KeySendService.Send(ScaleHotkeyKey, ScaleHotkeyMods);
            FrameGenOn = !FrameGenOn;
            DebugLog.Write($"小黄鸭: 已发送插帧热键 Ctrl+Alt+S，injected={injected}，目标={targetName}，最小化失败={MinimizeBlocked}，state={(FrameGenOn ? "开" : "关")}");
            return FrameGenOn ? ScaleResult.FrameGenOn : ScaleResult.FrameGenOff;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lossless] 开关插帧失败: {ex.Message}");
            return ScaleResult.LaunchFailed;
        }
        finally { Volatile.Write(ref _activating, 0); }
    }

    /// <summary>等待小黄鸭出现任一可见窗口（轮询 200ms，最多 timeoutMs）</summary>
    private static async Task<bool> WaitWindowReadyAsync(int timeoutMs)
    {
        var waited = 0;
        while (waited < timeoutMs)
        {
            if (RunningProcessIds().Any(pid => FindMainWindow(pid) != IntPtr.Zero)) return true;
            await Task.Delay(200);
            waited += 200;
        }
        return false;
    }

    /// <summary>启动小黄鸭并最小化（已运行则直接最小化）；用于 Y 键"打开小黄鸭"</summary>
    public static async Task<bool> LaunchMinimizedAsync(LaunchItem item)
    {
        if (!IsRunning)
        {
            if (!Launch(item)) return false;
            if (!await WaitRunningAsync(10000)) return false;
            FrameGenOn = false; // 刚启动的小黄鸭尚未开启插帧
        }
        MinimizeBlocked = !MinimizeWindows();
        return true;
    }

    /// <summary>
    /// 打开小黄鸭主窗口（编辑页"新建配置"用）：未运行则启动并等窗口出现，再切到前台。
    /// 不最小化，方便在小黄鸭里新建/编辑配置；配置由小黄鸭自己写回 Settings.xml。
    /// </summary>
    public static async Task<bool> OpenAsync(LaunchItem item)
    {
        if (!IsInstalled(item)) return false;
        if (!IsRunning)
        {
            if (!Launch(item)) return false;
            if (!await WaitRunningAsync(15000)) return false;
            await WaitWindowReadyAsync(8000);
        }
        foreach (var pid in RunningProcessIds())
        {
            var main = FindMainWindow(pid);
            if (main != IntPtr.Zero)
            {
                BringToForeground(main);
                break;
            }
        }
        return true;
    }

    /// <summary>
    /// 只最小化小黄鸭的主体窗口，返回是否全部成功。缩放/插帧生效后它会创建类名为
    /// LosslessScaling / LosslessScalingBlk 的覆盖层窗口，那些窗口绝不能收起，否则插帧画面会立刻消失。
    /// 小黄鸭以管理员身份运行时，普通权限进程调用 ShowWindow 会被 UIPI 拒绝（失败即返回 false）。
    /// </summary>
    private static bool MinimizeWindows()
    {
        var ok = true;
        foreach (var pid in RunningProcessIds())
        {
            var main = FindMainWindow(pid);
            if (main == IntPtr.Zero || IsIconic(main)) continue;
            if (!ShowWindow(main, SW_MINIMIZE)) ok = false;
        }
        return ok;
    }

    /// <summary>运行中的小黄鸭进程号列表</summary>
    private static List<int> RunningProcessIds()
    {
        var ids = new List<int>();
        try
        {
            var ps = Process.GetProcessesByName("LosslessScaling");
            foreach (var p in ps)
            {
                ids.Add(p.Id);
                p.Dispose();
            }
        }
        catch { }
        return ids;
    }

    /// <summary>等待小黄鸭进程出现（轮询 250ms，最多 timeoutMs）</summary>
    private static async Task<bool> WaitRunningAsync(int timeoutMs)
    {
        var waited = 0;
        while (waited < timeoutMs)
        {
            if (IsRunning) return true;
            await Task.Delay(250);
            waited += 250;
        }
        return IsRunning;
    }

    /// <summary>
    /// 启动前解析目标游戏窗口：取最近记录的前台应用（排除启动器自身与小黄鸭），
    /// 再用"像游戏"的黑名单校验，避免把热搜键发给浏览器/聊天工具这类窗口。
    /// </summary>
    private static IntPtr FindTargetGameWindow(
        ForegroundWatcherService.ForegroundApp? app, out string label)
    {
        label = "(未记录)";
        if (app is null || app.ProcessId == Environment.ProcessId) return IntPtr.Zero;
        if (string.Equals(app.ProcessName, "LosslessScaling", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        var hwnd = FindMainWindow(app.ProcessId);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return IntPtr.Zero;
        if (!TrainerService.IsGameLikeProcess(hwnd, app.ProcessName)) return IntPtr.Zero;
        label = $"{app.ProcessName} hwnd={hwnd}";
        return hwnd;
    }

    /// <summary>把指定窗口切回前台（最小化则先还原；SetForegroundWindow 被前台锁定拒绝时附加线程兜底）</summary>
    private static bool BringToForeground(IntPtr target)
    {
        if (target == IntPtr.Zero || !IsWindow(target)) return false;
        if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
        BringWindowToTop(target);
        if (SetForegroundWindow(target) && GetForegroundWindow() == target) return true;

        // 前台锁定：临时把本线程附加到当前前台线程，再设置前台窗口
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try { SetForegroundWindow(target); }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }

        if (GetForegroundWindow() == target) return true;
        // 最后兜底：SwitchToThisWindow（相当于 Alt+Tab 切换）
        try { SwitchToThisWindow(target, true); }
        catch { }
        return GetForegroundWindow() == target;
    }

    /// <summary>枚举该进程的可见顶层窗口：取标题最长的一个（通常就是游戏主窗口）</summary>
    private static IntPtr FindMainWindow(int pid)
    {
        var best = IntPtr.Zero;
        var bestLen = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var winPid);
            if ((int)winPid != pid || !IsWindowVisible(h)) return true;
            var len = GetWindowTextLengthW(h);
            if (len <= 0) return true;
            if (len > bestLen) { bestLen = len; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // ---- Steam 安装检测 ----

    /// <summary>按 Steam 安装信息查找 LosslessScaling.exe：注册表 SteamPath → libraryfolders.vdf → 各库清单</summary>
    private static string? DetectFromSteam()
    {
        var clientRoot = SteamClientRoot();
        if (clientRoot is null) return null;

        var libraries = new List<string>();
        void AddLib(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\');
                if (!libraries.Contains(full, StringComparer.OrdinalIgnoreCase)) libraries.Add(full);
            }
            catch { }
        }

        AddLib(clientRoot);
        foreach (var lib in ReadLibraryFolders(clientRoot)) AddLib(lib);

        foreach (var lib in libraries)
        {
            // ① 清单给出安装目录名（最可靠，游戏可自定义安装目录）
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{SteamAppId}.acf");
            var installDir = ReadInstalldir(manifest);
            if (installDir is not null)
            {
                var exe = Path.Combine(lib, "steamapps", "common", installDir, "LosslessScaling.exe");
                if (File.Exists(exe)) return exe;
            }
            // ② 常见默认目录名兜底
            var fallback = Path.Combine(lib, "steamapps", "common", "LosslessScaling", "LosslessScaling.exe");
            if (File.Exists(fallback)) return fallback;
        }
        return null;
    }

    /// <summary>Steam 客户端安装根（注册表；找不到返回 null）</summary>
    private static string? SteamClientRoot()
    {
        foreach (var (key, name) in SteamRegistryKeys)
        {
            try
            {
                if (Registry.GetValue(key, name, null) is not string raw || raw.Length == 0) continue;
                var path = Path.GetFullPath(raw.Replace('/', '\\')).TrimEnd('\\');
                if (Directory.Exists(path)) return path;
            }
            catch { /* 键不存在 / 无权限：试下一个 */ }
        }
        return null;
    }

    private static readonly (string Key, string Name)[] SteamRegistryKeys =
    [
        (@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
        (@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"),
        (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
    ];

    /// <summary>libraryfolders.vdf 里的全部库路径（只保留存在的目录）</summary>
    private static List<string> ReadLibraryFolders(string clientRoot)
    {
        var list = new List<string>();
        try
        {
            var vdf = Path.Combine(clientRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return list;
            var text = File.ReadAllText(vdf);
            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var path = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                    list.Add(path);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Lossless] 读取 libraryfolders.vdf 失败: {ex.Message}"); }
        return list;
    }

    /// <summary>从 appmanifest_<appid>.acf 读取 installdir</summary>
    private static string? ReadInstalldir(string manifest)
    {
        try
        {
            if (!File.Exists(manifest)) return null;
            var text = File.ReadAllText(manifest);
            var m = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    // ---- Win32 ----

    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
