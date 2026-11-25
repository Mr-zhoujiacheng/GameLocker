using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GameLocker.Services;

/// <summary>登记过的一个托盘常驻应用。</summary>
internal sealed class TrayAppEntry
{
    /// <summary>可执行文件全路径（用于校验窗口确实属于同一程序）</summary>
    public string ExePath { get; set; } = "";

    /// <summary>窗口类名</summary>
    public string ClassName { get; set; } = "";

    /// <summary>窗口标题（应用把主窗口藏到托盘后标题一般保持不变）</summary>
    public string Title { get; set; } = "";

    /// <summary>显示在菜单里的名字</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// 托盘常驻应用：把"藏在系统托盘里的应用"钉到顶栏，点一下把它的主窗口唤出来。
///
/// 为什么不做"镜像系统托盘图标"：Windows 11 的任务栏托盘已经不是经典 ToolbarWindow32，
/// 实测 UIA 连一个 Toolbar 元素都枚举不到，没有受支持的接口能列出托盘图标。
/// 所以改成"用户登记一次 + 按窗口类名/标题/进程路径追踪"，这也是能长期稳定的做法。
/// </summary>
internal static class TrayAppService
{
    private const int MaxEntries = 16;

    private sealed class Runtime
    {
        public TrayAppEntry Entry { get; init; } = new();
        public IntPtr Window;
        public uint Pid;
        public IntPtr Process;
    }

    private static readonly object Gate = new();
    private static readonly HashSet<string> Switching = new(StringComparer.OrdinalIgnoreCase);
    private static List<Runtime>? _items;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameLocker", "tray-apps.json");

    /// <summary>已登记条目（含实时窗口状态）。</summary>
    public static List<(TrayAppEntry Entry, bool Running)> Snapshot()
    {
        lock (Gate)
        {
            EnsureLoaded();
            Refresh();
            // Running = 进程还活着；窗口可能在托盘里被隐藏，用 WindowOf 单独取
            return _items!.Select(r => (r.Entry, IsProcessAlive(r))).ToList();
        }
    }

    /// <summary>
    /// 登记"当前前台窗口"所属的应用。
    /// 用户先把目标应用的主窗口打开并切到前台，再点这一项，就不用手填类名和标题。
    /// </summary>
    public static string AddForeground()
    {
        return AddWindow(IntPtr.Zero);
    }

    /// <summary>登记指定窗口所属的应用（hwnd 为 0 时取当前前台）。</summary>
    public static string AddWindow(IntPtr preferred)
    {
        lock (Gate)
        {
            EnsureLoaded();

            var hwnd = preferred != IntPtr.Zero ? preferred : GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "没有前台窗口";

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || (int)pid == Environment.ProcessId) return "前台是本程序自己，请先切到目标应用";

            var exe = ExePathOf(pid);
            if (string.IsNullOrEmpty(exe)) return "读不到前台窗口的进程路径";

            var cls = ClassOf(hwnd);
            var title = TitleOf(hwnd);
            if (string.IsNullOrEmpty(cls)) return "读不到窗口类名";

            if (_items!.Any(r => string.Equals(r.Entry.ExePath, exe, StringComparison.OrdinalIgnoreCase)))
                return "这个应用已经登记过了";

            if (_items.Count >= MaxEntries) return $"最多登记 {MaxEntries} 个";

            _items.Add(new Runtime
            {
                Entry = new TrayAppEntry
                {
                    ExePath = exe,
                    ClassName = cls,
                    Title = title,
                    DisplayName = Path.GetFileNameWithoutExtension(exe)
                },
                Window = hwnd,
                Pid = pid,
                Process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid)
            });

            Save();
            DebugLog.Write($"[顶栏] 登记托盘应用：{exe} class={cls} title={title}");
            return $"已登记「{Path.GetFileNameWithoutExtension(exe)}」";
        }
    }

    public static void Remove(TrayAppEntry entry)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var match = _items!.FirstOrDefault(r =>
                string.Equals(r.Entry.ExePath, entry.ExePath, StringComparison.OrdinalIgnoreCase));
            if (match is null) return;

            if (match.Process != IntPtr.Zero) CloseHandle(match.Process);
            _items.Remove(match);
            Save();
        }
    }

    /// <summary>仅在窗口可见、未最小化且已在前台时收起；否则恢复窗口。</summary>
    public static async Task<string> ToggleAsync(TrayAppEntry entry, CancellationToken token = default)
    {
        IntPtr window;
        uint pid;
        lock (Gate)
        {
            EnsureLoaded();
            Refresh();
            var match = Find(entry);
            if (match is null) return "没有登记这个应用";
            window = match.Window;
            if (window == IntPtr.Zero && !IsProcessAlive(match) && !UsesOwnTrayRestore(entry.ExePath)) return "应用没有在运行";
            pid = match.Pid;
            if (!Switching.Add(entry.ExePath)) return "";
        }

        try
        {
            token.ThrowIfCancellationRequested();
            // A hidden tray window has app-managed state. Showing its HWND skips that state transition.
            if (window == IntPtr.Zero || !IsWindowVisible(window))
            {
                var visibleWindow = FindVisibleTrayWindow(entry, pid);
                if (visibleWindow == IntPtr.Zero) return await WakeFromTrayAsync(entry, pid, token);
                window = visibleWindow;
                GetWindowThreadProcessId(window, out pid);
            }
            if (!MatchesProcess(window, pid)) return "应用窗口已关闭";
            if (IsHungAppWindow(window)) return $"{entry.DisplayName} 窗口未响应，请稍后再试。";

            var minimized = IsIconic(window);
            var hide = ShouldMinimize(true, minimized, IsForeground(window));
            // 异步发送显示命令，不让底栏等另一个进程的窗口线程。
            if ((hide || minimized) && !ShowWindowAsync(window, hide ? SW_MINIMIZE : SW_RESTORE))
                return $"无法切换 {entry.DisplayName} 的窗口";

            var deadline = Environment.TickCount64 + 1200;
            var focusRequested = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!MatchesProcess(window, pid)) return "应用窗口已关闭";
                var shown = IsWindowVisible(window) && !IsIconic(window);
                if (hide ? !shown : shown && IsForeground(window))
                {
                    DebugLog.Write($"[底栏] {(hide ? "收起" : "唤出")}托盘应用 {entry.DisplayName} hwnd=0x{window.ToInt64():X}");
                    return "";
                }

                if (Environment.TickCount64 >= deadline)
                    return !hide && shown ? $"{entry.DisplayName} 已显示，但未切到前台。" : $"{entry.DisplayName} 窗口未及时响应。";

                if (!hide && shown && !focusRequested)
                {
                    focusRequested = true;
                    SetForegroundWindow(window);
                }
                await Task.Delay(40, token);
            }
        }
        finally
        {
            lock (Gate) Switching.Remove(entry.ExePath);
        }
    }

    internal static bool ShouldMinimize(bool visible, bool minimized, bool foreground) =>
        visible && !minimized && foreground;

    internal static bool UsesOwnTrayRestore(string? path) =>
        !string.IsNullOrEmpty(path) && string.Equals(Path.GetFileName(path), "QQ.exe", StringComparison.OrdinalIgnoreCase);

    internal static void RequestNativeOpen(string path)
    {
        // The desktop entry lets QQ's single-instance handling wake its existing UI.
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? ""
        });
    }

    private static async Task<string> WakeFromTrayAsync(TrayAppEntry entry, uint pid, CancellationToken token)
    {
        var ownRestore = UsesOwnTrayRestore(entry.ExePath);
        var taskbar = FindWindow("Shell_TrayWnd", null);
        // The taskbar replacement can hide the notification area from UI Automation.
        var tryTrayFirst = !ownRestore || IsWindowVisible(taskbar);
        if (tryTrayFirst && await TryTrayIconAsync(entry, pid, token)) return "";

        if (ownRestore && Path.IsPathFullyQualified(entry.ExePath) && File.Exists(entry.ExePath))
        {
            token.ThrowIfCancellationRequested();
            DebugLog.Write($"[底栏] 请求应用原生入口恢复：{entry.DisplayName}");
            await Task.Run(() => RequestNativeOpen(entry.ExePath), token);
            if (await WaitForTrayRestoreAsync(entry, pid, token)) return "";
        }

        if (!tryTrayFirst && await TryTrayIconAsync(entry, pid, token)) return "";
        return $"未能从托盘唤出 {entry.DisplayName}，请尝试 Windows 托盘中的原应用图标。";
    }

    private static async Task<bool> TryTrayIconAsync(TrayAppEntry entry, uint pid, CancellationToken token)
    {
        var pids = pid == 0 ? new HashSet<int>() : new HashSet<int> { (int)pid };
        using var trayTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var trayToken = trayTimeout.Token;
        trayTimeout.CancelAfter(3500);
        try
        {
            var clicked = await Task.Run(() => TrayIconService.Click(trayToken, false,
                entry.ExePath, pids, entry.DisplayName, Path.GetFileNameWithoutExtension(entry.ExePath)), trayToken)
                .WaitAsync(trayToken);
            return clicked && await WaitForTrayRestoreAsync(entry, pid, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private static async Task<bool> WaitForTrayRestoreAsync(TrayAppEntry entry, uint pid, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + 2400;
        do
        {
            token.ThrowIfCancellationRequested();
            var window = FindVisibleTrayWindow(entry, pid);
            if (window != IntPtr.Zero && !IsIconic(window) && !IsHungAppWindow(window) && IsForeground(window))
            {
                DebugLog.Write($"[底栏] 应用已自行从托盘恢复：{entry.DisplayName}");
                return true;
            }
            await Task.Delay(100, token);
        } while (Environment.TickCount64 < deadline);
        return false;
    }

    private static IntPtr FindVisibleTrayWindow(TrayAppEntry entry, uint knownPid)
    {
        var window = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            if (!IsCandidateWindow(candidate) || !string.Equals(ClassOf(candidate), entry.ClassName, StringComparison.Ordinal)
                || !string.Equals(TitleOf(candidate), entry.Title, StringComparison.Ordinal)) return true;
            GetWindowThreadProcessId(candidate, out var owner);
            if (owner == 0 || (owner != knownPid && !string.Equals(ExePathOf(owner), entry.ExePath, StringComparison.OrdinalIgnoreCase))) return true;
            window = candidate;
            return !IsForeground(candidate);
        }, IntPtr.Zero);
        return window;
    }

    private static bool IsForeground(IntPtr window)
    {
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && (foreground == window || GetAncestor(foreground, GA_ROOT) == window);
    }

    private static bool MatchesProcess(IntPtr window, uint pid) =>
        GetWindowThreadProcessId(window, out var owner) != 0 && owner == pid;

    /// <summary>按 exe 路径找已登记条目（底栏的合成条目只带路径）。</summary>
    public static TrayAppEntry? FindEntry(string exePath)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _items!.FirstOrDefault(r =>
                string.Equals(r.Entry.ExePath, exePath, StringComparison.OrdinalIgnoreCase))?.Entry;
        }
    }

    /// <summary>该条目当前跟踪到的窗口（没有则 0）；底栏用它画运行指示与窗口预览。</summary>
    public static IntPtr WindowOf(TrayAppEntry entry)
    {
        lock (Gate)
        {
            EnsureLoaded();
            Refresh();
            var window = Find(entry)?.Window ?? IntPtr.Zero;
            return IsWindowVisible(window) ? window : IntPtr.Zero;
        }
    }

    /// <summary>
    /// 可登记的窗口列表：遍历所有可见的顶层应用窗口，排除本程序、已登记应用与系统外壳窗口。
    /// 用户从菜单里挑一个，就不需要先去把那个窗口切到前台。
    /// </summary>
    public static List<(string Label, IntPtr Hwnd)> EnumerateAppWindows()
    {
        lock (Gate)
        {
            EnsureLoaded();
            Refresh();

            var registered = new HashSet<string>(
                _items!.Select(r => r.Entry.ExePath), StringComparer.OrdinalIgnoreCase);
            var list = new List<(string, IntPtr)>();

            EnumWindows((hwnd, _) =>
            {
                if (list.Count >= 60) return false;
                if (!IsCandidateWindow(hwnd)) return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || (int)pid == Environment.ProcessId) return true;

                var exe = ExePathOf(pid);
                if (string.IsNullOrEmpty(exe) || registered.Contains(exe)) return true;

                var title = TitleOf(hwnd);
                if (title.Length == 0) return true;

                list.Add(($"{title}  [{Path.GetFileName(exe)}]", hwnd));
                return true;
            }, IntPtr.Zero);

            return list;
        }
    }

    private static Runtime? Find(TrayAppEntry entry)
        => _items!.FirstOrDefault(r =>
            string.Equals(r.Entry.ExePath, entry.ExePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>可见、无所有者、非工具窗、有标题、未被 DWM cloak 的顶层窗口才算候选。</summary>
    private static bool IsCandidateWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;

        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        return ClassOf(hwnd) is not ("Progman" or "WorkerW" or "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow");
    }

    /// <summary>打开 Windows 自己的通知区域浮出窗口（Win+B 再 Enter）。</summary>
    public static void OpenSystemTray()
    {
        try
        {
            // 顶部任务栏模式下任务栏是隐藏的，不要再把它 ShowWindow 出来：
            // 看门狗 150ms 后会把任务栏重新藏掉，连带把刚打开的托盘面板一起带走。
            var sentB = KeySendService.Send((int)'B', KeySendService.ModWin);
            Thread.Sleep(260);
            var sentEnter = KeySendService.Send(0x0D, 0);
            DebugLog.Write($"[顶栏] 已请求打开系统托盘（Win+B={sentB} Enter={sentEnter}）");
        }
        catch (Exception ex) { DebugLog.Write("[顶栏] 打开系统托盘失败：" + ex.Message); }
    }

    /// <summary>系统托盘溢出面板当前是否打开。</summary>
    public static bool IsSystemTrayOpen()
    {
        var hwnd = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
        return hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
    }

    /// <summary>关闭系统托盘溢出面板（焦点在面板上时 Esc 即可）。</summary>
    public static void CloseSystemTray() => KeySendService.Send(0x1B, 0);

    // ---- 运行时追踪 ----

    /// <summary>进程死了就清空；窗口失效就按类名+标题+exe 重新找一遍。</summary>
    private static void Refresh()
    {
        foreach (var item in _items!)
        {
            if (item.Process != IntPtr.Zero && !IsProcessAlive(item))
            {
                if (item.Process != IntPtr.Zero) CloseHandle(item.Process);
                item.Process = IntPtr.Zero;
                item.Pid = 0;
                item.Window = IntPtr.Zero;
                continue;
            }

            if (WindowStillValid(item)) continue;

            // 进程句柄还没建立（例如从磁盘读回来的登记项）也要走一遍查找：
            // FindWindow 会按 exe 路径重新 OpenProcess 并绑定窗口
            item.Window = IntPtr.Zero;
            FindWindow(item);
        }
    }

    private static bool IsProcessAlive(Runtime item)
        => item.Process != IntPtr.Zero && WaitForSingleObject(item.Process, 0) == WAIT_TIMEOUT;

    private static bool WindowStillValid(Runtime item)
    {
        if (item.Window == IntPtr.Zero || !IsWindow(item.Window)) return false;
        GetWindowThreadProcessId(item.Window, out var pid);
        return pid == item.Pid && ClassOf(item.Window) == item.Entry.ClassName;
    }

    /// <summary>按类名枚举顶层窗口，再用标题与 exe 路径确认是同一个程序的那个窗口。</summary>
    private static void FindWindow(Runtime item)
    {
        var current = IntPtr.Zero;
        while ((current = FindWindowEx(IntPtr.Zero, current, item.Entry.ClassName, null)) != IntPtr.Zero)
        {
            var title = TitleOf(current);
            if (!string.Equals(title, item.Entry.Title, StringComparison.Ordinal)) continue;

            GetWindowThreadProcessId(current, out var pid);
            if (pid == 0) continue;
            if (item.Pid != 0 && pid != item.Pid) continue;

            var exe = ExePathOf(pid);
            if (!string.Equals(exe, item.Entry.ExePath, StringComparison.OrdinalIgnoreCase)) continue;

            if (item.Process == IntPtr.Zero)
            {
                item.Process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (item.Process == IntPtr.Zero) continue;
                item.Pid = pid;
            }

            item.Window = current;
            return;
        }
    }

    // ---- 持久化 ----

    private static void EnsureLoaded()
    {
        if (_items is not null) return;

        try
        {
            if (File.Exists(FilePath))
            {
                var saved = JsonSerializer.Deserialize<List<TrayAppEntry>>(File.ReadAllText(FilePath)) ?? [];
                _items = saved.Take(MaxEntries).Select(e => new Runtime { Entry = e }).ToList();
            }
            else _items = [];
        }
        catch (Exception ex)
        {
            DebugLog.Write("[顶栏] 读取托盘应用失败：" + ex.Message);
            _items = [];
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var list = _items!.Select(r => r.Entry).ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { DebugLog.Write("[顶栏] 保存托盘应用失败：" + ex.Message); }
    }

    // ---- Win32 ----

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        // 标题里的换行会让后续比对失败
        return buffer.ToString().Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string ExePathOf(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return "";

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return "";
            return buffer.ToString();
        }
        finally { CloseHandle(handle); }
    }

    private const uint SYNCHRONIZE = 0x00100000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const uint GA_ROOT = 2;
    private const int DWMWA_CLOAKED = 14;
    private const byte VK_LWIN = 0x5B;
    private const byte VK_RETURN = 0x0D;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
