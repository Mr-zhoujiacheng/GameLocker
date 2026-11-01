using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLocker.Models;

namespace GameLocker.Services;

public enum LaunchOutcome { Launched, Activated, Failed }
public sealed record LaunchResult(LaunchOutcome Outcome, string Message = "")
{
    public bool Success => Outcome != LaunchOutcome.Failed;
}

/// <summary>进程身份、窗口恢复与托盘唤醒；不强显隐藏窗口，不重复启动已运行程序。</summary>
public static class LaunchService
{
    private static readonly string DiagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "launch-diag.log");
    private static readonly object LogLock = new();
    internal static void Diag(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DiagFile)!);
                if (File.Exists(DiagFile) && new FileInfo(DiagFile).Length > 2_000_000)
                    File.Move(DiagFile, DiagFile + ".old", true);
                File.AppendAllText(DiagFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private sealed record ProcessIdentity(int Pid, long Started, string Path);
    private sealed class LaunchRecord
    {
        public readonly Dictionary<int, ProcessIdentity> Processes = new();
    }
    private sealed record TargetState(HashSet<int> Pids, bool Uncertain);
    private static readonly ConcurrentDictionary<string, LaunchRecord> Records = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<LaunchResult> LaunchOrActivateAsync(LaunchItem item, CancellationToken token)
    {
        if (!InFlight.TryAdd(item.Target, 0)) return new(LaunchOutcome.Failed, "该程序正在处理中，请稍候");
        try
        {
            token.ThrowIfCancellationRequested();
            var shellTarget = item.Kind is LaunchKind.Folder or LaunchKind.Url || IsDirectoryTarget(item.Target);
            var path = ResolveExecutable(item.Target);
            if (shellTarget || (path is null && !IsExecutableTarget(item.Target)))
            {
                var existing = FindShellWindow(item);
                if (existing != 0) return await ActivateResultAsync(existing, token);
                token.ThrowIfCancellationRequested();
                Start(item, null);
                for (var i = 0; i < 65; i++)
                {
                    await Task.Delay(200, token);
                    existing = FindShellWindow(item);
                    if (existing == 0) continue;
                    var activated = await ActivateResultAsync(existing, token);
                    return activated.Success ? new(LaunchOutcome.Launched) : activated;
                }
                return new(LaunchOutcome.Failed, "已发出打开请求，但未能确认目标窗口；无法验证最大化状态");
            }

            var state = CollectTargetProcesses(item, path);
            token.ThrowIfCancellationRequested();
            var hwnd = FindProcessWindow(state.Pids);
            Diag($"打开 {item.Name}: pids=[{string.Join(",", state.Pids)}] uncertain={state.Uncertain} hwnd=0x{hwnd.ToInt64():X}");
            if (hwnd != 0) return await ActivateResultAsync(hwnd, token);
            if (state.Pids.Count > 0) return await WakeTrayAsync(item, path, token);
            if (state.Uncertain || path is null)
                return new(LaunchOutcome.Failed, "无法确认程序身份或读取进程路径，已避免重复启动；请检查快捷方式或权限");

            token.ThrowIfCancellationRequested();
            Start(item, path);
            // 等待启动页切换到稳定窗口，只观察已验证进程，不捕获无关前台窗口。
            IntPtr pendingWindow = 0;
            var stableSince = Stopwatch.StartNew();
            for (var i = 0; i < 65; i++)
            {
                await Task.Delay(200, token);
                state = CollectTargetProcesses(item, path);
                hwnd = FindProcessWindow(state.Pids);
                if (hwnd != pendingWindow)
                {
                    pendingWindow = hwnd;
                    stableSince.Restart();
                }
                if (hwnd == 0 || i < 9 || stableSince.ElapsedMilliseconds < 1000) continue;
                var result = await ActivateResultAsync(hwnd, token);
                return result.Success ? new(LaunchOutcome.Launched) : result;
            }
            return new(LaunchOutcome.Failed, "已发出启动请求，但未检测到可切换窗口；程序可能仍在加载、等待授权或仅驻留托盘");
        }
        catch (OperationCanceledException)
        {
            Diag($"打开 {item.Name}: 超时/取消");
            return new(LaunchOutcome.Failed, "操作超时，已停止后续切换；不会重复启动程序");
        }
        catch (Exception ex)
        {
            Diag($"打开 {item.Name}: {ex.GetType().Name} {ex.Message}");
            return new(LaunchOutcome.Failed, $"打开失败：{ex.Message}");
        }
        finally { InFlight.TryRemove(item.Target, out _); }
    }

    private static async Task<LaunchResult> WakeTrayAsync(LaunchItem item, string? path, CancellationToken token)
    {
        var names = new[] { item.Name, path is null ? null : Path.GetFileNameWithoutExtension(path) };
        // 单击/默认动作无效后再试一次双击；每轮都先检查，避免窗口已经出现后再点回托盘。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var state = CollectTargetProcesses(item, path);
            var hwnd = FindProcessWindow(state.Pids);
            if (hwnd != 0) return await ActivateResultAsync(hwnd, token);
            var clicked = TrayIconService.Click(token, attempt == 1, path, state.Pids, names);
            Diag($"托盘 {item.Name}: attempt={attempt + 1} clicked={clicked}");
            if (!clicked) continue;
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(150, token);
                state = CollectTargetProcesses(item, path);
                hwnd = FindProcessWindow(state.Pids);
                if (hwnd != 0) return await ActivateResultAsync(hwnd, token);
            }
        }
        return new(LaunchOutcome.Failed, "程序已运行，但托盘图标未能恢复窗口；请手动打开一次，或检查双方权限是否一致");
    }

    private static void Start(LaunchItem item, string? path)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = item.Target, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Maximized
        });
        var record = new LaunchRecord();
        if (process is not null && ReadIdentity(process.Id) is { } identity)
            record.Processes[identity.Pid] = identity;
        Records[item.Target] = record;
        Diag($"启动 {item.Name}: pid={process?.Id} executable={path is not null}");
    }

    private static TargetState CollectTargetProcesses(LaunchItem item, string? path)
    {
        var record = Records.GetOrAdd(item.Target, _ => new LaunchRecord());
        var pids = new HashSet<int>();
        var uncertain = false;
        lock (record)
        {
            foreach (var known in record.Processes.Values.ToArray())
            {
                var current = ReadIdentity(known.Pid);
                if (current == known) pids.Add(known.Pid);
                else record.Processes.Remove(known.Pid);
            }
            var leaf = path is null ? Path.GetFileNameWithoutExtension(item.Target) : Path.GetFileNameWithoutExtension(path);
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId || pids.Contains(process.Id)) continue;
                        if (!process.ProcessName.Equals(leaf, StringComparison.OrdinalIgnoreCase)) continue;
                        var identity = ReadIdentity(process.Id);
                        if (identity is null) { uncertain = true; continue; }
                        if (path is null) { uncertain = true; continue; }
                        if (!identity.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                        pids.Add(identity.Pid);
                        record.Processes[identity.Pid] = identity;
                    }
                    catch { /* 无关进程可能已退出；只有同名候选身份不可读才标为不确定。 */ }
                }
            }
            // 父 PID + 父启动时间校验，避免 PID 复用认错后代进程。
            var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot != new IntPtr(-1))
            {
                try
                {
                    var entries = new List<(int Pid, int Parent)>();
                    var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                    if (Process32FirstW(snapshot, ref entry))
                        do { entries.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID)); }
                        while (Process32NextW(snapshot, ref entry));
                    bool added;
                    do
                    {
                        added = false;
                        foreach (var e in entries)
                        {
                            if (pids.Contains(e.Pid) || e.Pid == Environment.ProcessId || !pids.Contains(e.Parent)) continue;
                            var parent = record.Processes[e.Parent];
                            if (ReadIdentity(parent.Pid) != parent) continue;
                            var child = ReadIdentity(e.Pid);
                            if (child is null || child.Started < parent.Started) continue;
                            pids.Add(child.Pid);
                            record.Processes[child.Pid] = child;
                            added = true;
                        }
                    } while (added);
                }
                finally { CloseHandle(snapshot); }
            }
            return new(pids, uncertain);
        }
    }

    private static ProcessIdentity? ReadIdentity(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return null;
        try
        {
            var text = new System.Text.StringBuilder(32768);
            var size = text.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, text, ref size)
                || !GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
            return new(pid, created, Path.GetFullPath(text.ToString()));
        }
        catch { return null; }
        finally { CloseHandle(handle); }
    }

    private static string? ResolveExecutable(string target)
    {
        try
        {
            if (Path.GetExtension(target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                target = DropImportService.ResolveShortcutTarget(target) ?? "";
            if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (!Path.IsPathFullyQualified(target) || !File.Exists(target)) return null;
            return Path.GetFullPath(target);
        }
        catch { return null; }
    }
    private static bool IsExecutableTarget(string target) =>
        Path.GetExtension(target).ToLowerInvariant() is ".exe" or ".lnk";

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    private static bool IsUsableWindow(IntPtr hwnd) => hwnd != 0 && IsWindow(hwnd)
        && IsWindowVisible(hwnd) && !IsCloaked(hwnd) && !IsShellWindow(hwnd);

    private static IntPtr FindProcessWindow(HashSet<int> pids)
    {
        IntPtr best = 0;
        double bestScore = double.MinValue;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid) || !IsUsableWindow(hwnd)) return true;
            var style = GetWindowLongPtrW(hwnd, -20).ToInt64();
            if ((style & 0x80) != 0) return true;
            var area = WindowArea(hwnd);
            if (area <= 0 && !IsIconic(hwnd)) return true;
            // 尺寸仅参与排序；小对话框也能被恢复，不用面积阈值剔除。
            var taskbarWindow = (style & 0x40000) != 0;
            var windowStyle = GetWindowLongPtrW(hwnd, -16).ToInt64();
            var mainFrame = (windowStyle & 0x00C00000) == 0x00C00000
                || (windowStyle & 0x00050000) != 0;
            var score = Math.Min(area, 100_000_000d)
                + (GetWindow(hwnd, 4) == 0 ? 1_000_000_000d : 0)
                + (taskbarWindow ? 2_000_000_000d : 0)
                + (mainFrame ? 4_000_000_000d : 0);
            Diag($"    [候选窗] hwnd=0x{hwnd.ToInt64():X} style=0x{windowStyle:X} ex=0x{style:X} area={area} score={score}");
            if (score > bestScore) { bestScore = score; best = hwnd; }
            return true;
        }, 0);
        if (best == 0) return 0;
        var popup = GetLastActivePopup(best);
        // 只有主窗口被模态对话框禁用时才跟随弹窗，避免 Electron 浮窗抢走主界面。
        if (popup != best && (GetWindowLongPtrW(best, -16).ToInt64() & 0x08000000) != 0
            && IsUsableWindow(popup))
        {
            GetWindowThreadProcessId(popup, out var pid);
            if (pids.Contains((int)pid)) return popup;
        }
        return best;
    }

    private static async Task<LaunchResult> ActivateResultAsync(IntPtr hwnd, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (!IsUsableWindow(hwnd)) break;
            if (IsHungAppWindow(hwnd)) return new(LaunchOutcome.Failed, "目标程序暂时无响应，未强制操作其窗口");
            // 最大化主窗口，模态对话框仍保留焦点与原尺寸；不强显托盘隐藏窗。
            var main = GetAncestor(hwnd, 3);
            if (!IsUsableWindow(main)) main = hwnd;
            var canMaximize = (GetWindowLongPtrW(main, -16).ToInt64() & 0x10000) != 0;
            if (canMaximize)
            {
                if (!IsZoomed(main) || IsIconic(main)) ShowWindowAsync(main, 3);
            }
            else if (IsIconic(main)) ShowWindowAsync(main, 9);
            if (hwnd != main && IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);
            SetForegroundWindow(hwnd);
            await Task.Delay(180, token);
            var fg = GetForegroundWindow();
            var success = fg == hwnd || (IsUsableWindow(fg) && GetAncestor(fg, 3) == GetAncestor(hwnd, 3));
            var maximized = !canMaximize || (IsZoomed(main) && !IsIconic(main));
            Diag($"激活 hwnd=0x{hwnd.ToInt64():X} attempt={attempt + 1} foreground=0x{fg.ToInt64():X} success={success} canMaximize={canMaximize} maximized={IsZoomed(main)}");
            if (success && maximized) return new(LaunchOutcome.Activated);
        }
        return new(LaunchOutcome.Failed, "窗口存在，但未能完成前台切换或最大化；请检查程序权限或手动点击任务栏");
    }

    public static int Minimize(LaunchItem item)
    {
        var shell = item.Kind is LaunchKind.Url or LaunchKind.Folder || IsDirectoryTarget(item.Target);
        var windows = shell ? new[] { FindShellWindow(item) }
            : ProcessWindows(CollectTargetProcesses(item, ResolveExecutable(item.Target)).Pids);
        var count = 0;
        foreach (var hwnd in windows)
            if (IsUsableWindow(hwnd) && (IsIconic(hwnd) || ShowWindowAsync(hwnd, 6))) count++;
        return count;
    }
    private static IntPtr[] ProcessWindows(HashSet<int> pids)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains((int)pid) && IsUsableWindow(h)) list.Add(h);
            return true;
        }, 0);
        return list.ToArray();
    }

    private static IntPtr FindShellWindow(LaunchItem item)
    {
        // 文件夹/URL 由共享进程承载，保持标题匹配路径，但不写入程序身份记录。
        string?[] keys = item.Kind == LaunchKind.Url ? [HostOf(item.Target)]
            : [NormalizeLeaf(item.Target), GetDisplayName(item.Target)];
        IntPtr best = 0;
        double area = 0;
        EnumWindows((h, _) =>
        {
            if (!IsUsableWindow(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;
            var identity = ReadIdentity((int)pid);
            var folder = item.Kind != LaunchKind.Url;
            if (folder && (identity is null || !Path.GetFileName(identity.Path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))) return true;
            var title = WindowText(h);
            if (!keys.Any(k => !string.IsNullOrWhiteSpace(k) && title.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;
            var current = WindowArea(h);
            if (current > area) { best = h; area = current; }
            return true;
        }, 0);
        return best;
    }
    private static double WindowArea(IntPtr h) => GetWindowRect(h, out var r)
        ? Math.Max(0, r.Right - r.Left) * (double)Math.Max(0, r.Bottom - r.Top) : 0;
    private static bool IsShellWindow(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }
    private static string WindowText(IntPtr h)
    {
        if (IsHungAppWindow(h)) return "";
        var sb = new System.Text.StringBuilder(1024);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }
    private static bool IsDirectoryTarget(string target) => Directory.Exists(target);
    private static string? HostOf(string url)
    {
        try { var host = new Uri(url).Host; return host.StartsWith("www.") ? host[4..] : host; }
        catch { return null; }
    }
    private static string? NormalizeLeaf(string path)
    {
        try { var full = Path.GetFullPath(path).TrimEnd('\\', '/'); return Path.GetFileName(full) is { Length: > 0 } leaf ? leaf : full; }
        catch { return null; }
    }
    private static string? GetDisplayName(string path) => NormalizeLeaf(path)?.ToLowerInvariant() switch
    {
        "downloads" => "下载", "documents" => "文档", "pictures" => "图片", "music" => "音乐",
        "videos" => "视频", "desktop" => "桌面", "favorites" => "收藏夹", "links" => "快速启动",
        "saved games" => "已保存的游戏", "searches" => "搜索", "3d objects" => "三维对象", "contacts" => "联系人", _ => null
    };

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr param);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, System.Text.StringBuilder text, ref int size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder text, int size);
}
