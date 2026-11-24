using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 底栏可见的一个窗口：用于"独立窗口显示"模式与窗口预览。
/// <paramref name="Hosted"/> 表示这是 UWP/商店应用：顶层窗口属于 ApplicationFrameHost，
/// ExeName/ExePath 是真正那个应用的（如 SystemSettings.exe），Handle 仍是可以激活的顶层窗口。
/// <paramref name="AppId"/> 是打包应用的包族名（只有 Hosted 窗口有），用于匹配 shell:AppsFolder 目标。
/// </summary>
internal readonly record struct AppWindow(
    int Pid, string ExeName, string ExePath, IntPtr Handle, string Title,
    bool Hosted = false, string AppId = "");

/// <summary>
/// 枚举可见的顶层应用窗口，按 exe 名归组。
/// 只做 GetWindowThreadProcessId + 样式/可见性过滤，不注入、不读别的进程内存。
/// </summary>
internal static class AppWindows
{
    /// <summary>本进程的所有窗口都不参与（否则底栏会把自己算成一个"应用"）。</summary>
    private static readonly int OwnPid = Environment.ProcessId;

    /// <summary>
    /// UWP/商店应用的顶层窗口宿主：真正的应用在它的子窗口 Windows.UI.Core.CoreWindow 里。
    /// 不解析出来的话，这类"系统原生应用"会被当成宿主进程整个丢掉。
    /// </summary>
    private const string FrameHostProcess = "ApplicationFrameHost";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    /// <summary>
    /// Shell 自己的宿主/提示窗口：有的没有 owner 也不是 TOOLWINDOW，单靠样式过滤会漏进底栏。
    /// 例如把鼠标停在系统任务栏上的应用图标时，explorer 会弹出 "主机弹出窗口"
    /// （class=Xaml_WindowedPopupClass），底栏会把它当成一个新启动的"应用"加一个图标。
    /// </summary>
    private static readonly HashSet<string> ShellHostClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MSTaskSwWClass", "MSTaskListWClass",
        "TaskListThumbnailWnd", "tooltips_class32", "Xaml_WindowedPopupClass",
        "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
        "Progman", "WorkerW", "ForegroundStaging", "ApplicationManager_DesktopShellWindow",
        "Windows.UI.Core.CoreWindow",
    };

    /// <summary>target → 进程名。扫描每 250 ms 一拍，每拍都会问到同一批 target，
    /// 特别是 .lnk 还要走一次 Shell 解析，缓存下来才不至于每拍都卡一下。</summary>
    private static readonly ConcurrentDictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// pid → exe 路径缓存：每拍都要为每个窗口查一次进程路径（OpenProcess + QueryFullProcessImageName），
    /// 而它几乎不会变。只在"这一拍还看得到这个 pid"时保留，pid 一消失就丢弃，
    /// 所以 PID 被复用时缓存也已经失效，不会认错进程。
    /// </summary>
    private static readonly Dictionary<int, string> PathCache = [];
    private static readonly object PathCacheLock = new();

    /// <summary>窗口句柄 → AUMID（原生应用取图标要用；COM 调用不算便宜，按窗口缓存）。</summary>
    private static readonly Dictionary<IntPtr, string> AumidCache = [];
    private static readonly object AumidCacheLock = new();

    /// <summary>当前所有可作为"应用窗口"的顶层窗口。</summary>
    /// <param name="allowUntitled">
    /// true 时也把"还没有标题"的可见窗口算进来（运行指示用）：程序刚打开那一下窗口常常先没有标题，
    /// 只按标题过滤会让底栏的运行条晚一两拍才亮。没有标题的窗口要求尺寸像一扇正常窗口。
    /// </param>
    public static List<AppWindow> Enumerate(bool allowUntitled = false)
    {
        var result = new List<AppWindow>();
        // 同一个进程通常有多个窗口，进程路径只查一次；避开 Process.MainModule（受权限影响会抛异常）
        var paths = new Dictionary<int, string>();
        var seenPids = new HashSet<int>();
        var seenHandles = new HashSet<IntPtr>();

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsUsable(hwnd, allowUntitled)) return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                var processId = (int)pid;
                if (processId == OwnPid || processId == 0) return true;
                seenPids.Add(processId);

                var path = CachedPathOf(processId, paths, seenPids);
                var exe = string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path);
                // 带反作弊/受保护的游戏等进程读不到完整路径，但进程名还能读：
                // 这种窗口也要算数，否则底栏的图标和运行条都不会出现。
                if (string.IsNullOrEmpty(exe)) exe = ProcessNameOfPid(processId);
                if (string.IsNullOrEmpty(exe)) return true;

                // 系统原生应用（设置、计算器、照片…）：窗口宿主是 ApplicationFrameHost，
                // 真正的应用在子 CoreWindow 的进程里，换成它的身份才能被底栏认出。
                var hosted = false;
                var appId = "";
                if (string.Equals(exe, FrameHostProcess, StringComparison.OrdinalIgnoreCase)
                    && TryResolveHostedApp(hwnd, paths, seenPids, seenHandles,
                        out var appPid, out var appExe, out var appPath, out var appAumid))
                {
                    processId = appPid;
                    exe = appExe;
                    path = appPath;
                    appId = appAumid;
                    hosted = true;
                }

                result.Add(new AppWindow(processId, exe, path, hwnd, TitleOf(hwnd), hosted, appId));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 枚举窗口失败：" + ex.Message); }

        // 已经消失的进程/窗口从缓存里摘掉（PID/句柄被复用时才不会拿到旧身份）
        lock (PathCacheLock)
        {
            if (PathCache.Count > 0)
            {
                foreach (var stale in PathCache.Keys.Where(pid => !seenPids.Contains(pid)).ToList())
                    PathCache.Remove(stale);
            }
        }
        lock (AumidCacheLock)
        {
            if (AumidCache.Count > 0)
            {
                foreach (var stale in AumidCache.Keys.Where(h => !seenHandles.Contains(h)).ToList())
                    AumidCache.Remove(stale);
            }
        }

        return result;
    }

    /// <summary>带进程内/跨拍缓存的 exe 路径查询。</summary>
    private static string CachedPathOf(int pid, Dictionary<int, string> local, HashSet<int> seenPids)
    {
        if (local.TryGetValue(pid, out var cached)) return cached;

        string? path;
        lock (PathCacheLock) PathCache.TryGetValue(pid, out path);
        if (path is null)
        {
            path = ExePathOf(pid);
            lock (PathCacheLock) PathCache[pid] = path;
        }

        seenPids.Add(pid);
        local[pid] = path;
        return path;
    }

    /// <summary>
    /// 从 UWP 的框架窗口里找出真正应用的进程（ApplicationFrameHost 下的 CoreWindow）。
    /// 找不到时返回 false，调用方继续按宿主处理（至少不会崩）。
    /// </summary>
    private static bool TryResolveHostedApp(IntPtr frame, Dictionary<int, string> local,
        HashSet<int> seenPids, HashSet<IntPtr> seenHandles,
        out int pid, out string exe, out string path, out string appId)
    {
        pid = 0;
        exe = "";
        path = "";
        appId = "";

        var core = FindWindowEx(frame, IntPtr.Zero, CoreWindowClass, null);
        if (core == IntPtr.Zero) return false;

        GetWindowThreadProcessId(core, out var corePidValue);
        var corePid = (int)corePidValue;
        if (corePid == 0 || corePid == OwnPid) return false;

        path = CachedPathOf(corePid, local, seenPids);
        exe = string.IsNullOrEmpty(path) ? ProcessNameOfPid(corePid) : Path.GetFileNameWithoutExtension(path);
        if (exe.Length == 0) return false;

        pid = corePid;
        seenHandles.Add(frame);

        // 完整 AUMID 用来取真实图标；拿不到就退回包族名（至少能让固定项的运行条对上）
        string? cached;
        lock (AumidCacheLock) AumidCache.TryGetValue(frame, out cached);
        if (cached is null)
        {
            cached = AumidOfWindow(frame);
            if (cached.Length == 0) cached = PackageFamilyOf(corePid);
            lock (AumidCacheLock) AumidCache[frame] = cached;
        }
        appId = cached;
        return true;
    }

    /// <summary>
    /// 窗口的 AppUserModelID（原生/商店应用才有）。SHGetPropertyStoreForWindow 可以跨进程读，
    /// GetApplicationUserModelId 只能读本进程的窗口。
    /// </summary>
    private static string AumidOfWindow(IntPtr hwnd)
    {
        IntPtr store = IntPtr.Zero;
        try
        {
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == IntPtr.Zero) return "";

            var vtbl = Marshal.ReadIntPtr(store);
            var getValue = Marshal.GetDelegateForFunctionPointer<PropertyStoreGetValue>(
                Marshal.ReadIntPtr(vtbl, 5 * IntPtr.Size));

            var key = AppUserModelIdKey;
            var value = default(PropVariant);
            if (getValue(store, ref key, ref value) != 0) return "";
            try
            {
                return value.vt == VtLpwstr && value.pointer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.pointer) ?? ""
                    : "";
            }
            finally
            {
                if (value.vt == VtLpwstr) PropVariantClear(ref value);
            }
        }
        catch { return ""; }
        finally { if (store != IntPtr.Zero) Marshal.Release(store); }
    }

    /// <summary>
    /// 进程的包族名（打包应用才有，例如 windows.immersivecontrolpanel_cw5n1h2txyewy）。
    /// 用来把 shell:AppsFolder 的 AUMID 和运行中的窗口对上，原生应用才能点亮运行条。
    /// </summary>
    private static string PackageFamilyOf(int pid)
    {
        if (pid <= 0) return "";
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            uint length = 256;
            var buffer = new StringBuilder(256);
            var result = GetPackageFamilyName(handle, ref length, buffer);
            if (result == ErrorInsufficientBuffer)
            {
                buffer = new StringBuilder((int)length);
                result = GetPackageFamilyName(handle, ref length, buffer);
            }
            return result == 0 ? buffer.ToString().TrimEnd('\0') : "";
        }
        catch { return ""; }
        finally { CloseHandle(handle); }
    }

    /// <summary>shell:AppsFolder\AUMID 形式的 target 里的 AUMID；不是这种 target 返回空串。</summary>
    public static string AppIdOfTarget(string target)
    {
        const string prefix = "shell:AppsFolder\\";
        if (string.IsNullOrWhiteSpace(target)) return "";
        return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? target[prefix.Length..].Trim()
            : "";
    }

    /// <summary>AUMID 里的包族名（"!" 之前那段），也是窗口能拿到的那个标识。</summary>
    public static string PackageFamilyOfAppId(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        var bang = appId.IndexOf('!');
        return bang > 0 ? appId[..bang] : appId;
    }

    /// <summary>当前有窗口、且包族匹配这个 AUMID 的窗口（原生/商店应用用）。</summary>
    public static List<AppWindow> ForAppId(string appId)
    {
        var family = PackageFamilyOfAppId(appId);
        if (family.Length == 0) return [];
        return Enumerate()
            .Where(w => w.AppId.Length > 0 && string.Equals(w.AppId, family, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>与 target（exe 路径或名字）对应的可见窗口。</summary>
    public static List<AppWindow> ForTarget(string target)
    {
        var name = ProcessNameOf(target);
        if (string.IsNullOrEmpty(name)) return [];
        return Enumerate().Where(w => string.Equals(w.ExeName, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>该 target 是否有可见窗口在运行。</summary>
    public static bool IsRunning(string target) => ForTarget(target).Count > 0;

    /// <summary>
    /// 按进程名取主程序 exe 路径。给「窗口由 helper 子进程创建」的应用还原身份用：
    /// 例如 Steam 的界面窗口属于 steamwebhelper.exe，但用户认的是 Steam.exe。
    /// </summary>
    public static string ExePathOfProcess(string processName)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                var path = ExePathOf(process.Id);
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch { }
        return "";
    }


    /// <summary>把某个窗口切到前台；最小化的先还原。</summary>
    public static void Activate(IntPtr hwnd) => BringToFront(hwnd);

    /// <summary>
    /// 把窗口真正置到最前面：还原最小化 → 尝试前台 → 绕过 Windows 前台锁 → 抬到普通窗口最上层。
    /// 返回是否确认切到了前台。
    /// 本进程没有前台权限时 SetForegroundWindow 会被系统静默拒绝，单靠它会出现
    /// "点了图标窗口还在下面、看不见"的情况，所以这里按代价从低到高分几步兜底。
    /// </summary>
    public static bool BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        try
        {
            // 最小化的先还原；被 Hide 到托盘的直接显示出来
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            else if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_SHOW);

            if (IsForeground(hwnd)) return true;
            if (TryForceForeground(hwnd)) return true;

            // 前台锁：把本线程的输入状态临时挂到当前前台窗口线程和目标窗口线程上
            var foreground = GetForegroundWindow();
            var currentThread = GetCurrentThreadId();
            var foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
            var targetThread = GetWindowThreadProcessId(hwnd, out _);
            var attachForeground = foregroundThread != 0 && foregroundThread != currentThread
                                   && AttachThreadInput(currentThread, foregroundThread, true);
            var attachTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread
                               && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                BringWindowToTop(hwnd);
                if (TrySetForeground(hwnd)) return true;
            }
            finally
            {
                if (attachForeground) AttachThreadInput(currentThread, foregroundThread, false);
                if (attachTarget) AttachThreadInput(currentThread, targetThread, false);
            }

            // 再兜底：轻按一下 Alt 解掉前台锁定，然后强制切一次
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SwitchToThisWindow(hwnd, true);
            if (TrySetForeground(hwnd)) return true;

            // 最后：抢不到前台也至少要让人看得见——临时置顶再取消，抬到普通窗口最上层
            RaiseAboveNormalWindows(hwnd);
            return IsForeground(hwnd);
        }
        catch (Exception ex)
        {
            DebugLog.Write("[底栏] 激活窗口失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>第一次没成功时在后台短促重试几次；不阻塞 UI 线程。</summary>
    public static void BringToFrontSoon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(80);
                if (!IsWindow(hwnd)) return;
                if (BringToFront(hwnd)) return;
            }
        });
    }

    /// <summary>该窗口的 Z 序位置（0 = 最上层，只看顶层窗口）；找不到返回 int.MaxValue。</summary>
    public static Dictionary<IntPtr, int> TopLevelZOrder()
    {
        var order = new Dictionary<IntPtr, int>();
        var index = 0;
        for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
            order[hwnd] = index++;
        return order;
    }

    private static bool IsForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return true;
        return foreground != IntPtr.Zero && GetAncestor(foreground, GA_ROOT) == GetAncestor(hwnd, GA_ROOT);
    }

    private static bool TrySetForeground(IntPtr hwnd)
    {
        SetForegroundWindow(hwnd);
        // 前台切换有极短的消息延迟：给两拍时间确认，别把刚成功的那次判成失败
        for (var i = 0; i < 2; i++)
        {
            if (IsForeground(hwnd)) return true;
            Thread.Sleep(8);
        }
        return IsForeground(hwnd);
    }

    /// <summary>
    /// 参考 TaskbarPopup 的 ForceForeground：先把窗口异步提到 Z 序最前，再 SetForegroundWindow；
    /// 需要时把当前线程的输入队列临时挂到前台线程上解掉前台锁。
    /// SWP_ASYNCWINDOWPOS 保证目标窗口卡住时也不会把我们拖住。
    /// </summary>
    private static bool TryForceForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return true;
        var currentThread = GetCurrentThreadId();
        var fgThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
        // 前台程序卡住时不要挂：挂上之后对它的窗口操作会变成同步的，跟着一起卡
        var attached = fgThread != 0 && fgThread != currentThread && !IsHungWindow(foreground)
                       && AttachThreadInput(currentThread, fgThread, true);
        try
        {
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_ASYNCWINDOWPOS);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, fgThread, false);
        }
        return IsForeground(hwnd);
    }

    private static bool IsHungWindow(IntPtr hwnd) => hwnd == IntPtr.Zero || IsHungAppWindow(hwnd);

    /// <summary>临时置顶再取消置顶：不改变窗口样式，只是把它抬到普通窗口的最上层。</summary>
    private static void RaiseAboveNormalWindows(IntPtr hwnd)
    {
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    }

    /// <summary>该窗口之上是否已经没有别的普通窗口（置顶/提示层不算遮挡）。</summary>
    public static bool IsAboveNormalWindows(IntPtr hwnd)
    {
        for (var h = GetWindow(hwnd, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
        {
            if (!IsWindowVisible(h)) continue;
            var exStyle = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOPMOST) != 0) continue;
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) continue;
            var title = TitleOf(h);
            if (title.Length == 0) continue;   // 桌面/宿主等无标题窗口不算遮挡
            return false;
        }
        return true;
    }

    /// <summary>从路径取进程名（不含扩展名），用于和 exe 名比对。</summary>
    public static string ProcessNameOf(string target)
    {
        if (string.IsNullOrEmpty(target)) return "";
        if (NameCache.TryGetValue(target, out var cached)) return cached;

        // 兜底缓存无限增长没有意义：条目本来就很少，超了整表清一次
        if (NameCache.Count > 512) NameCache.Clear();
        var name = ProcessNameCore(target);
        NameCache[target] = name;
        return name;
    }

    private static string ProcessNameCore(string target)
    {
        try
        {
            var path = target;
            // .lnk 快捷方式要解析到真实 exe，否则底栏的运行指示永远判成"未运行"
            if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
                path = DropImportService.ResolveShortcutTarget(path) ?? "";
            if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return "";
            return Path.GetFileNameWithoutExtension(path) ?? "";
        }
        catch { return ""; }
    }

    /// <summary>进程的完整 exe 路径（读不到时返回空串）；底栏把运行中的程序临时加进来要用它取图标/点击目标。</summary>
    private static string ExePathOf(int pid)
    {
        // QueryFullProcessImageName 对提权进程也能拿到路径（LimitedInformation 权限），
        // 比 Process.MainModule 快且不会因为拒绝访问抛异常——运行指示每拍都要走这里。
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                if (QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return buffer.ToString(0, size);
            }
            catch { }
            finally { CloseHandle(handle); }
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName ?? "";
        }
        catch { return ""; }
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetWindowText(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    /// <summary>窗口类名；读不到时返回空串（不抛异常）。</summary>
    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    /// <summary>读不到完整路径时的兜底：进程名不需要打开进程句柄，受保护进程一般也能读到。</summary>
    private static string ProcessNameOfPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch { return ""; }
    }

    private static bool IsUsable(IntPtr hwnd, bool allowUntitled)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;

        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;

        // 从属窗口（有 owner）是弹层/提示/对话框，不是独立应用：Windows 任务栏也不会给它们按钮。
        // 任务栏图标悬停提示、右键菜单都属于这种情况，不能算成"运行中的程序"。
        // 带 WS_EX_APPWINDOW 的窗口即使有 owner 也会上任务栏，仍然保留。
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero && (exStyle & WS_EX_APPWINDOW) == 0) return false;

        // 少数 Shell 宿主窗口既不置 TOOLWINDOW 也没有 owner，按窗口类兜底排除。
        if (ShellHostClasses.Contains(ClassNameOf(hwnd))) return false;

        // 被 DWM 隐藏（虚拟桌面切换、UWP 挂起）的窗口不算
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        // 没有标题的窗口通常是辅助/宿主窗口；但程序刚启动那一下主窗口也常常还没标题，
        // allowUntitled 时把它算进去，运行条才能一开就亮。
        if (TitleOf(hwnd).Length == 0)
        {
            if (!allowUntitled) return false;

            // 最小化的照收；否则要求尺寸像一扇正常窗口，避免 1×1 的隐藏辅助窗口把运行条点亮
            if (IsIconic(hwnd)) return true;
            if (!GetWindowRect(hwnd, out var loose)) return false;
            return loose.Right - loose.Left >= 120 && loose.Bottom - loose.Top >= 80;
        }

        // 最小化窗口仍算一个窗口（允许点图标还原），但它的矩形是 -32000，不参与尺寸判断
        if (IsIconic(hwnd)) return true;

        if (!GetWindowRect(hwnd, out var rect)) return false;
        return rect.Right - rect.Left > 0 && rect.Bottom - rect.Top > 0;
    }

    /// <summary>
    /// 一次进程树快照：pid → (父进程 pid, 进程名)。
    /// 有些应用（Steam、Epic 这类外壳）自己的窗口是由子进程创建的，只看窗口 exe 名会永远判成"未运行"；
    /// 有了父进程链就能把子进程的窗口算到它头上。
    /// </summary>
    public static Dictionary<int, (int Parent, string Name)> ProcessTree()
    {
        var map = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == new IntPtr(-1)) return map;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    map[(int)entry.th32ProcessID] =
                        ((int)entry.th32ParentProcessID, Path.GetFileNameWithoutExtension(entry.szExeFile));
                }
                while (Process32NextW(snapshot, ref entry));
            }
        }
        catch { }
        finally { CloseHandle(snapshot); }

        return map;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>PROPVARIANT 只用到 VT_LPWSTR 这一种（前 8 字节是类型，后 16 字节是联合体）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr pointer;
        public IntPtr pointerHigh;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreGetValue(IntPtr self, ref PropertyKey key, ref PropVariant value);

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5
    };

    private const ushort VtLpwstr = 31;

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IntPtr store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;

    private const uint GA_ROOT = 2;
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_HWNDPREV = 3;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SwitchToThisWindow(IntPtr hwnd, bool altTab);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags,
        StringBuilder name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
}
