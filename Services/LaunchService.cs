using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>启动/关闭快捷项。关闭基于"启动时到前台的窗口"，兼容程序/计算器(UWP)/文件夹(explorer)/网站(浏览器)。</summary>
public static class LaunchService
{
    public static void Launch(LaunchItem item)
    {
        var psi = new ProcessStartInfo
        {
            FileName = item.Target,
            UseShellExecute = true
        };
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"启动失败 {item.Target}: {ex.Message}");
        }

        // 记录本次启动，稍后异步捕获到前台的窗口句柄（用于 Y 关闭）
        if (proc is not null)
        {
            _records[item.Target] = new LaunchRecord { Pid = proc.Id };
            _ = CaptureForegroundAsync(item.Target, proc.Id);
        }
    }

    /// <summary>
    /// 关闭该磁贴对应的程序：先收集本次启动/名称匹配到的进程，向它们的所有可见窗口发 WM_CLOSE；
    /// 若无可见窗口（已缩到托盘）则关主窗口或结束进程树。返回关闭的窗口数（0=未在运行）。
    /// </summary>
    public static int Close(LaunchItem item)
    {
        // 文件夹磁贴由系统共享 explorer.exe 承载：启动记录的 PID 主窗口是任务栏而非目标窗口，
        // 不能走记录路径，直接按窗口标题匹配关闭对应文件夹窗口。
        if (item.Kind == LaunchKind.Folder || IsDirectoryTarget(item.Target))
            return CloseExplorerWindows(item.Target);

        // 网站磁贴：有启动记录时关记录窗口，没有则按域名匹配浏览器窗口
        if (item.Kind == LaunchKind.Url)
            return TryCloseRecordWindow(item.Target) ? 1 : CloseWindowsWithTitle(HostOf(item.Target));

        var pids = CollectTargetPids(item);
        if (pids.Count == 0) return 0;

        // 一次关闭该应用的所有窗口（主窗口 + 迷你播放器/歌词窗等），避免关一半
        var posted = 0;
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains((int)pid)) continue;
            if (TryPostWmClose(hwnd)) posted++;
        }

        if (posted == 0)
        {
            // 没有可见窗口（已缩到托盘）：按主窗口再试一次，仍不行就结束进程树
            foreach (var pid in new List<int>(pids))
            {
                if (pid == Environment.ProcessId || !TryGetProcess(pid, out var p)) continue;
                if (string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.MainWindowHandle != 0 && TryPostWmClose(p.MainWindowHandle)) { posted++; continue; }
                try { p.Kill(entireProcessTree: true); posted++; }
                catch { /* 无权限/已退出 */ }
            }
        }

        if (posted > 0) EscalateHiddenApp(pids);
        return posted;
    }

    /// <summary>关掉启动记录里捕获到的窗口；成功返回 true</summary>
    private static bool TryCloseRecordWindow(string target)
    {
        if (!_records.TryRemove(target, out var rec)) return false;
        if (rec!.Hwnd != 0 && IsWindow(rec.Hwnd))
        {
            GetWindowThreadProcessId(rec.Hwnd, out uint curPid);
            if ((int)curPid == rec.Pid && TryPostWmClose(rec.Hwnd)) return true;
        }
        if (rec.Pid != 0 && rec.Pid != Environment.ProcessId
            && TryGetProcess(rec.Pid, out var p)
            && !string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            if (p.MainWindowHandle != 0 && TryPostWmClose(p.MainWindowHandle)) return true;
            try { p.Kill(entireProcessTree: true); return true; } catch { }
        }
        return false;
    }

    // ---- 启动记录 ----

    private sealed class LaunchRecord
    {
        public IntPtr Hwnd;
        public int Pid;
    }

    private static readonly ConcurrentDictionary<string, LaunchRecord> _records = new();

    /// <summary>启动后轮询前台窗口：出现非本进程、非启动器自身的可见顶层窗口即记录其句柄。</summary>
    private static async Task CaptureForegroundAsync(string target, int startedPid)
    {
        var ownPid = Environment.ProcessId;
        for (int i = 0; i < 100; i++) // 最多约 10 秒：启动器型应用（先起 launcher 再拉起真身）需要更久
        {
            await Task.Delay(100);
            var fg = GetForegroundWindow();
            if (fg == 0 || !IsWindowVisible(fg)) continue;
            if (IsShellWindow(fg)) continue; // 桌面/任务栏：启动器隐藏后短暂切前台，记录后关闭会误触发"关闭 Windows"
            GetWindowThreadProcessId(fg, out uint pid);
            if ((int)pid == ownPid) continue; // 仍是启动器自己（未隐藏/未切前台）
            if (IsShellProcess((int)pid)) continue; // 外壳进程 explorer：文件夹磁贴另有标题匹配关闭路径
            // 记录：优先匹配本次启动进程；否则记录当前前台窗口所属进程
            var rec = _records.TryGetValue(target, out var r) ? r : new LaunchRecord();
            rec.Hwnd = fg;
            rec.Pid = (int)pid;
            _records[target] = rec;
            return;
        }
    }

    // ---- 目标进程匹配 ----

    /// <summary>收集该磁贴对应的进程：启动记录 + 进程名/启动器前缀/窗口标题匹配。
    /// 启动器型应用（SodaMusicLauncher → SodaMusic）与跨会话重启后都靠这一层兜底。</summary>
    private static HashSet<int> CollectTargetPids(LaunchItem item)
    {
        var ownPid = Environment.ProcessId;
        var pids = new HashSet<int>();

        if (_records.TryRemove(item.Target, out var rec))
        {
            // 捕获窗口所属进程优先（启动器场景下它就是真身进程），再补启动 PID
            if (rec!.Hwnd != 0 && IsWindow(rec.Hwnd))
            {
                GetWindowThreadProcessId(rec.Hwnd, out uint pid);
                if ((int)pid == rec.Pid && rec.Pid != ownPid) pids.Add(rec.Pid);
            }
            if (rec.Pid != 0 && rec.Pid != ownPid) pids.Add(rec.Pid);
        }

        var leaf = ExeLeafOf(item.Target);
        var titleKey = item.Name?.Trim();

        // 按进程名收集（不要求有可见窗口）：应用缩到托盘后 Y 键也能一次关掉
        if (!string.IsNullOrEmpty(leaf))
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != ownPid && !pids.Contains(p.Id) && MatchesApp(p.ProcessName, leaf, "", null))
                        pids.Add(p.Id);
                }
                catch { /* 受保护进程取不到名字 */ }
                finally { p.Dispose(); }
            }
        }

        // 再按窗口标题补齐（启动器 exe 名与真身完全不同、只能靠磁贴名称认亲的情况）
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            var id = (int)pid;
            if (id == ownPid || IsShellProcess(id) || pids.Contains(id)) continue;
            if (!TryGetProcess(id, out var p)) continue;
            if (MatchesApp(p.ProcessName, leaf, WindowText(hwnd), titleKey)) pids.Add(id);
        }
        return pids;
    }

    /// <summary>进程是否属于这个磁贴：1) 进程名 = exe 名；2) 启动器与被启动程序共享前缀
    /// （SodaMusicLauncher / SodaMusic）；3) 窗口标题与磁贴名称完全相同。</summary>
    private static bool MatchesApp(string processName, string? exeLeaf, string windowTitle, string? displayName)
    {
        if (!string.IsNullOrEmpty(exeLeaf))
        {
            if (string.Equals(processName, exeLeaf, StringComparison.OrdinalIgnoreCase)) return true;
            // 启动器与真身互为前缀（SodaMusicLauncher ↔ SodaMusic、WeChat ↔ WeChatAppEx）
            if (processName.Length >= 4 && exeLeaf.Length >= 4
                && (processName.StartsWith(exeLeaf, StringComparison.OrdinalIgnoreCase)
                    || exeLeaf.StartsWith(processName, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return !string.IsNullOrEmpty(displayName)
               && string.Equals(windowTitle.Trim(), displayName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>关闭后复查：应用把关闭拦截成"缩到托盘"（进程仍在、已无可见窗口）时强制结束，
    /// 避免需要按两次 Y；若还留着窗口（例如"确认退出"对话框）就交给用户决定。</summary>
    private static void EscalateHiddenApp(IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            foreach (var pid in new List<int>(pids))
            {
                if (pid == Environment.ProcessId) continue;
                if (!TryGetProcess(pid, out var p)) continue;
                if (IsProtectedProcess(p.ProcessName) || HasVisibleUserWindow(pid)) continue;
                try { p.Kill(entireProcessTree: true); } catch { /* 无权限/已退出 */ }
            }
        });
    }

    /// <summary>该进程是否还有"用户可见"的窗口：有标题、非工具窗口。
    /// 托盘应用的隐藏主窗/无标题辅助窗不算，否则永远等不到强制结束。</summary>
    private static bool HasVisibleUserWindow(int pid)
    {
        var found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if ((int)p != pid || !IsWindowVisible(h)) return true;
            if (WindowText(h).Trim().Length == 0) return true;
            var ex = GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            found = true;
            return false; // 找到即可停止枚举
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>系统进程 / 外壳进程，不允许被强制结束</summary>
    private static bool IsProtectedProcess(string name) => name.ToLowerInvariant() is
        "explorer" or "svchost" or "dwm" or "csrss" or "winlogon" or "services" or "system"
        or "applicationframehost" or "shellexperiencehost" or "startmenuexperiencehost"
        or "searchhost" or "textinputhost" or "gamebar";


    /// <summary>关闭资源管理器中对应文件夹窗口（只关这一个目录窗口，不动其他 explorer 窗口）。
    /// 标题可能是目录名或本地化显示名（如 Downloads → "下载"），两者都匹配；盘符根匹配 "(X:)"。</summary>
    private static int CloseExplorerWindows(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return 0;
        var leaf = NormalizeLeaf(folderPath);
        if (string.IsNullOrEmpty(leaf)) return 0;
        // 已知文件夹的本地化显示名（资源管理器标题用显示名）
        var display = GetDisplayName(folderPath);
        Debug.WriteLine($"[Close] folder leaf='{leaf}' display='{display}'");

        // 盘符根（如 "D:"）：窗口标题为 "本地磁盘 (D:)" 等
        var isDrive = leaf.Length == 2 && leaf[1] == ':' && char.IsLetter(leaf[0]);
        int count = 0;
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!TryGetProcess((int)pid, out var p)) continue;
            if (!string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase)) continue;
            var title = WindowText(hwnd);
            Debug.WriteLine($"[Close] explorer 窗口标题: '{title}'");
            var hit = isDrive
                ? title.Contains($"({leaf})", StringComparison.OrdinalIgnoreCase)
                : title.Contains(leaf, StringComparison.OrdinalIgnoreCase)
                  || (!string.IsNullOrEmpty(display) && title.Contains(display, StringComparison.OrdinalIgnoreCase));
            if (hit)
            {
                if (TryPostWmClose(hwnd)) count++;
            }
        }
        return count;
    }

    /// <summary>关闭标题包含指定子串的窗口（网站磁贴：匹配含域名的浏览器窗口）</summary>
    private static int CloseWindowsWithTitle(string? sub)
    {
        if (string.IsNullOrEmpty(sub)) return 0;
        int count = 0;
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            var t = WindowText(hwnd);
            if (t.Contains(sub, StringComparison.OrdinalIgnoreCase) && TryPostWmClose(hwnd))
                count++;
        }
        return count;
    }

    // ---- 目标解析 ----

    /// <summary>程序磁贴 → 进程名（exe 文件名去扩展名；.lnk 解析真实目标）</summary>
    private static string? ExeLeafOf(string target)
    {
        try
        {
            var path = Path.GetFullPath(target);
            if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var real = DropImportService.ResolveShortcutTarget(path);
                if (!string.IsNullOrEmpty(real)) path = real;
            }
            var name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }

    /// <summary>URL → 主机名（去掉 www.），用于匹配浏览器窗口标题</summary>
    private static string? HostOf(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        catch { return null; }
    }

    /// <summary>文件夹路径 → 目录名（资源管理器窗口标题即目录名）</summary>
    private static string? NormalizeLeaf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            var name = Path.GetFileName(full);
            // 盘符根（如 D:\）标题为 "数据盘 (D:)" 等，兜底用盘符匹配
            return string.IsNullOrEmpty(name) ? full : name;
        }
        catch { return null; }
    }

    private static bool TryGetProcess(int pid, out Process process)
    {
        try { process = Process.GetProcessById(pid); return true; }
        catch { process = null!; return false; }
    }

    /// <summary>目标路径是否为目录（Kind 被误存为 App 的文件夹磁贴也走 explorer 标题匹配；含"."的目录名不受影响）</summary>
    private static bool IsDirectoryTarget(string target)
    {
        try { return Directory.Exists(target); }
        catch { return false; }
    }

    /// <summary>常见用户目录的本地化显示名（英文目录名 → 资源管理器中文标题）；非已知目录返回 null</summary>
    private static string? GetDisplayName(string folderPath)
    {
        try
        {
            var leaf = Path.GetFileName(Path.GetFullPath(folderPath).TrimEnd('\\', '/'));
            return leaf.ToLowerInvariant() switch
            {
                "downloads" => "下载", "documents" => "文档", "pictures" => "图片",
                "music" => "音乐", "videos" => "视频", "desktop" => "桌面",
                "favorites" => "收藏夹", "links" => "快速启动", "saved games" => "已保存的游戏",
                "searches" => "搜索", "3d objects" => "三维对象", "contacts" => "联系人",
                _ => null
            };
        }
        catch { return null; }
    }

    // ---- Win32 ----

    private const uint WM_CLOSE = 0x0010;

    /// <summary>向窗口发送 WM_CLOSE（优雅关闭）。系统外壳窗口一律拒绝：对桌面 Progman/WorkerW 发 WM_CLOSE
    /// 会直接弹出"关闭 Windows"关机界面，这正是"关闭程序却出现系统关机"的根因。</summary>
    private static bool TryPostWmClose(IntPtr hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || IsShellWindow(hwnd)) return false;
        return PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>是否为系统外壳窗口（桌面 / 任务栏）：其 WM_CLOSE 会触发系统关机或退出外壳，必须排除</summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var cls = WindowClass(hwnd);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>该 PID 是否为 Windows 外壳进程（explorer.exe）</summary>
    private static bool IsShellProcess(int pid)
        => TryGetProcess(pid, out var p) && string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);

    private static string WindowClass(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) <= 0 ? "" : sb.ToString();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    /// <summary>枚举所有可见、有标题的顶层窗口句柄</summary>
    private static List<IntPtr> EnumVisibleTopWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowTextLengthW(h) > 0) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string WindowText(IntPtr hwnd)
    {
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
