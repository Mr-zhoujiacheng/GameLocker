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
    /// 关闭该磁贴对应的程序：优先关闭启动时到前台的那个窗口（WM_CLOSE，优雅退出）；
    /// 无记录时按类型兜底匹配窗口。返回关闭的窗口数（0=未在运行）。
    /// </summary>
    public static int Close(LaunchItem item)
    {
        // 文件夹磁贴由系统共享 explorer.exe 承载：启动记录的 PID 主窗口是任务栏而非目标窗口，
        // 不能走记录路径，直接按窗口标题匹配关闭对应文件夹窗口。
        if (item.Kind == LaunchKind.Folder || IsDirectoryTarget(item.Target))
            return CloseExplorerWindows(item.Target);

        if (_records.TryRemove(item.Target, out var rec))
        {
            // 1) 启动时捕获到的窗口仍在、且仍属于当初记录的进程 → 直接关闭该窗口
            //    （校验所属进程防句柄复用误关别的窗口；外壳窗口由 TryPostWmClose 拒绝）
            if (rec!.Hwnd != 0 && IsWindow(rec.Hwnd))
            {
                GetWindowThreadProcessId(rec.Hwnd, out uint curPid);
                if ((int)curPid == rec.Pid && TryPostWmClose(rec.Hwnd))
                    return 1;
            }
            // 2) 只有 PID：取其主窗口关闭，否则结束进程树
            if (rec.Pid != 0 && rec.Pid != System.Environment.ProcessId
                && TryGetProcess(rec.Pid, out var p)
                && !string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                if (p.MainWindowHandle != 0 && TryPostWmClose(p.MainWindowHandle))
                    return 1;
                try { p.Kill(entireProcessTree: true); return 1; }
                catch { /* 无权限/已退出 */ }
            }
        }
        // 3) 兜底：按类型匹配现有窗口（跨会话/捕获失败时）
        return CloseByMatch(item);
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
        for (int i = 0; i < 30; i++) // 最多约 3 秒
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

    // ---- 兜底匹配 ----

    private static int CloseByMatch(LaunchItem item) => item.Kind switch
    {
        LaunchKind.Url => CloseWindowsWithTitle(HostOf(item.Target)),
        LaunchKind.Folder => CloseExplorerWindows(item.Target),
        _ => CloseWindowsByProcessName(ExeLeafOf(item.Target))
    };

    /// <summary>按进程名关闭其所有可见顶层窗口（优雅 WM_CLOSE）</summary>
    private static int CloseWindowsByProcessName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        int count = 0;
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!TryGetProcess((int)pid, out var p)) continue;
            if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase))
            {
                if (TryPostWmClose(hwnd)) count++;
            }
        }
        return count;
    }

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
