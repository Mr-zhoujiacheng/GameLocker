using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 判定"前台窗口是不是正在全屏运行的游戏"，用于进入游戏后最小化其他窗口。
/// 旧规则（"不在黑名单 + 窗口大于主屏一半"）会把浏览器、资源管理器、播放器等普通程序当成游戏，
/// 这里改为两级判定：
/// 1) 窗口必须真正盖满它所在的显示器（不是主屏一半，也不是"最大化"后只盖住工作区）；
/// 2) 必须出现一条游戏强证据：D3D 独占全屏 / 游戏库路径 / 游戏引擎窗口类 / 本启动器启动过的进程。
/// 没有强证据时只保留兜底：无边框全屏 + 系统报告存在全屏程序 + 进程明显不是桌面软件。
/// </summary>
internal static class GameWindowDetector
{
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const int DWMWA_CLOAKED = 14;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>用户自定义排除名单：%APPDATA%\GameLocker\game-exclude.txt，一行一个进程名（可带 .exe，# 开头为注释）</summary>
    private static readonly string ExcludeFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "game-exclude.txt");
    private static HashSet<string> _userExcluded = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _userExcludedAt = DateTime.MinValue;

    // SHQueryUserNotificationState 的返回值
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    /// <summary>游戏库 / 平台安装目录特征（仅作正向证据，命中即认为是游戏目录）</summary>
    private static readonly string[] GameLibraryMarkers =
    {
        @"\steamapps\common\", @"\steamapps\", @"\steamlibrary\",
        @"\epic games\", @"\gog games\", @"\gog galaxy\games\",
        @"\ubisoft game launcher\games\", @"\ubisoft game launcher\",
        @"\origin games\", @"\ea games\", @"\electronic arts\",
        @"\wegameapps\", @"\xboxgames\",
        @"\games\", @"\game files\", @"\游戏\", @"\游戏库\", @"\单机游戏\"
    };

    /// <summary>主流游戏引擎/框架的窗口类名（精确匹配，不含编辑器）</summary>
    private static readonly string[] GameWindowClasses =
    {
        "UnityWndClass", "UnrealWindow", "SDL_app", "SDL2", "GLFW30", "GLFW20",
        "Valve001", "CryENGINE", "GameMaker", "GameMakerWindow", "QuakeWindow"
    };

    /// <summary>兜底判定时视为桌面软件（避免浏览器/播放器/聊天/工具的全屏界面被误判为游戏）</summary>
    private static readonly string[] DesktopAppTokens =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore", "arc", "browser",
        "sogou", "360se", "360chrome", "ucbrowser", "maxthon", "chromium",
        "potplayer", "mpc", "kmplayer", "gomplayer", "vlc", "mpv",
        "bilibili", "iqiyi", "youku", "qqplayer", "qqmusic", "qqlive", "tplayer", "wmplayer",
        "video.ui", "zunevideo", "zunemusic", "mediaplayer", "media.player",
        "netflix", "disney", "primevideo", "amazonvideo", "comet",
        "photos", "microsoft.photos", "ms-photos",
        "spotify", "neteasecloudmusic", "cloudmusic", "kugou", "kuwo",
        "snippingtool", "screenclippinghost", "obs", "obs64", "fraps", "rtss",
        "clash", "v2ray", "xray", "mihomo", "verge",
        "qq", "tencent", "wechat", "weixin", "wxwork", "wework", "wemeet", "feishu", "lark",
        "dingtalk", "telegram", "discord", "slack", "teams", "zoom", "skype",
        "steam", "epicgameslauncher", "goggalaxy", "uplay", "upc", "battlenet", "origin",
        "eaapp", "eadesktop", "wegame", "xbox", "gamingservices", "riotclient", "riotclientservices",
        "itch", "bethesda", "rockstar", "galaxyclient",
        "thunder", "xunlei", "idman", "baidunetdisk", "aliyun", "onedrive", "dropbox",
        "wps", "winword", "excel", "powerpnt", "outlook", "onenote", "notion",
        "photoshop", "illustrator", "acrobat", "reader",
        "everything", "7zfm", "winrar", "todesk", "sunlogin", "anydesk", "rustdesk",
        "parsec", "moonlight", "steamlink", "gamebar", "xboxgamebar"
    };

    /// <summary>前台窗口是否是正在全屏运行的游戏。</summary>
    public static bool IsFullscreenGame(IntPtr hwnd, uint pid)
    {
        if (hwnd == IntPtr.Zero || pid == 0) return false;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) return false;

        var exeFile = KeyboardHookService.ProcessImageName(pid);
        if (exeFile.Length == 0) return false;
        var exe = Path.GetFileNameWithoutExtension(exeFile);
        // 共享黑名单仍然生效：系统外壳、浏览器、聊天、办公、启动器等一律否决。
        if (!TrainerService.IsGameLikeProcess(hwnd, exe)) return false;

        // 必须真正盖满所在显示器（副屏同样适用），"最大化"后只盖住工作区不算。
        if (!CoversOwnMonitor(hwnd)) return false;

        var className = WindowClass(hwnd);
        // 桌面软件即使有全屏/游戏引擎类名（如 Steam 大屏幕模式的 SDL 窗口）也不算游戏
        if (IsDesktopApp(exe, className)) return false;

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        var fullscreenState = QueryNotificationState();
        var path = KeyboardHookService.ProcessImagePath(pid);

        // 正向证据：任一成立即认定是游戏，避免副屏/特殊分辨率下被漏判。
        var d3dFullscreen = fullscreenState == QUNS_RUNNING_D3D_FULL_SCREEN;
        var inGameLibrary = path.Length > 0 && IsGameLibraryPath(path);
        var engineWindow = IsGameWindowClass(className);
        var launchedByUs = LaunchService.IsTrackedProcess((int)pid);
        if (d3dFullscreen || inGameLibrary || engineWindow || launchedByUs)
        {
            DebugLog.Write($"游戏窗口: 认定 exe={exe} class={className} " +
                           $"d3d={d3dFullscreen} library={inGameLibrary} tracked={launchedByUs}");
            return true;
        }

        // 兜底：真正无边框全屏 + 系统处于全屏状态 + 不是已知桌面软件。
        // 仅靠"无边框 + 整屏"会误伤视频播放器/浏览器全屏，故必须叠加系统状态与进程类型。
        var borderless = (style & WS_CAPTION) != WS_CAPTION && (style & WS_THICKFRAME) == 0;
        var fullscreenApp = fullscreenState is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN;
        if (borderless && fullscreenApp && !IsDesktopApp(exe, className))
        {
            DebugLog.Write($"游戏窗口: 兜底认定 exe={exe} class={className} state={fullscreenState}");
            return true;
        }
        return false;
    }

    /// <summary>窗口矩形是否覆盖其所属显示器的整个屏幕（允许 2px 取整误差与略微超出）。</summary>
    internal static bool CoversOwnMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return false;
        if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return false;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var screen = info.rcMonitor;
        return rect.Left <= screen.Left + 2 && rect.Top <= screen.Top + 2
            && rect.Right >= screen.Right - 2 && rect.Bottom >= screen.Bottom - 2;
    }

    /// <summary>窗口是否至少占所在显示器的指定宽高比例（用于放过窗口化游戏、排除 Dock/小工具条）。</summary>
    internal static bool CoversMostOfMonitor(IntPtr hwnd, double widthRatio, double heightRatio)
    {
        if (!GetWindowRect(hwnd, out var rect)) return false;
        if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return false;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var screen = info.rcMonitor;
        var mw = screen.Right - screen.Left;
        var mh = screen.Bottom - screen.Top;
        if (mw <= 0 || mh <= 0) return false;
        return rect.Right - rect.Left >= mw * widthRatio && rect.Bottom - rect.Top >= mh * heightRatio;
    }

    internal static bool IsGameLibraryPath(string path)
    {
        var lower = path.ToLowerInvariant();
        foreach (var marker in GameLibraryMarkers)
        {
            if (lower.Contains(marker)) return true;
        }
        return false;
    }

    internal static bool IsGameWindowClass(string className)
    {
        foreach (var known in GameWindowClasses)
        {
            if (string.Equals(className, known, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static bool IsDesktopApp(string exe, string className)
    {
        var lower = exe.ToLowerInvariant();
        if (IsUserExcluded(lower)) return true;
        switch (lower)
        {
            case "explorer": case "applicationframehost": case "shellexperiencehost":
            case "startmenuexperiencehost": case "searchhost": case "textinputhost":
            case "lockapp": case "logonui": case "runtimebroker": case "dwm":
            case "windowsterminal": case "wt": case "cmd": case "powershell": case "pwsh": case "conhost":
            case "notepad": case "notepad++": case "code": case "devenv": case "idea64":
            case "pycharm64": case "sublime_text": case "winword": case "excel":
            case "powerpnt": case "outlook": case "onenote":
                return true;
        }
        foreach (var token in DesktopAppTokens)
        {
            if (lower.StartsWith(token, StringComparison.Ordinal)) return true;
        }
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow";
    }

    /// <summary>读取用户排除名单（按文件修改时间缓存；文件不存在/读取失败时视为空名单）</summary>
    private static bool IsUserExcluded(string exe)
    {
        try
        {
            var info = new FileInfo(ExcludeFile);
            if (!info.Exists) return false;
            if (info.LastWriteTimeUtc != _userExcludedAt)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(ExcludeFile))
                {
                    var name = line.Trim();
                    if (name.Length == 0 || name.StartsWith('#')) continue;
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    set.Add(name);
                }
                _userExcluded = set;
                _userExcludedAt = info.LastWriteTimeUtc;
            }
            return _userExcluded.Contains(exe);
        }
        catch { return false; }
    }

    private static int QueryNotificationState()
    {
        try { return SHQueryUserNotificationState(out var state) == 0 ? state : 0; }
        catch { return 0; }
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string WindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
