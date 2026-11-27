using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Interop.UIAutomationClient;

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
    private static int _opening;

    public static void OpenSystemTray() => _ = OpenSystemTrayAsync();

    public static async Task OpenSystemTrayAsync()
    {
        if (Interlocked.Exchange(ref _opening, 1) != 0) return;
        try
        {
            // 顶部任务栏模式下任务栏保持隐藏，用 Windows 原生快捷键打开托盘。
            var sentB = KeySendService.Send((int)'B', KeySendService.ModWin);
            await Task.Delay(120);
            // 指针尽早离开任务栏折叠箭头：Win+B 会把键盘焦点送到通知区域，
            // 指针还压在那上面时系统会弹"显示隐藏的图标"提示。
            TryPreParkCursor();
            await Task.Delay(130);
            var sentEnter = KeySendService.Send(0x0D, 0);
            var shown = await WaitPanelAsync(1400);
            if (!shown)
            {
                // 偶发：任务栏没把键盘焦点给到通知区域，Enter 打了空——补一次
                KeySendService.Send((int)'B', KeySendService.ModWin);
                await Task.Delay(200);
                KeySendService.Send(0x0D, 0);
                shown = await WaitPanelAsync(1400);
            }

            var parked = false;
            var cleared = 0;
            if (shown)
            {
                var sw = Stopwatch.StartNew();
                // 关键：面板一出现就立刻停指针 + 点一下空白，尽量抢在焦点框画出来之前。
                // 优先用上次缓存的空白点（几毫秒完成）；没有缓存才用 UI 自动化现算（慢一点）。
                parked = TryParkFast() || await Task.Run(TryParkCursorOnPanel);
                if (parked) StartParkWatcher();
                if (parked && ClickAtCursor()) cleared++;
                LogTrayWindows("面板打开后");
                var firstClickMs = sw.ElapsedMilliseconds;
                HideSystemTrayFocusCue();
                // 之后 1.6 秒内两件事一起盯：
                // 1) 系统"显示隐藏的图标"提示是 Win+B 让折叠箭头拿到焦点后弹的，出现时间不定，反复藏；
                // 2) 焦点框若晚到，再补点（最多补到 3 次）。
                for (var i = 0; i < 20; i++)
                {
                    await Task.Delay(80);
                    HideTrayTooltips();
                    if (cleared < 3 && IsPanelIconFocused() && ClickAtCursor()) cleared++;
                }
                DebugLog.Write($"[顶栏] 托盘首点 {firstClickMs}ms");
            }
            else
            {
                RestoreParkedCursorNow();   // 面板没打开：把预停的指针放回去
            }
            DebugLog.Write($"[顶栏] 已请求打开系统托盘（Win+B={sentB} Enter={sentEnter} 面板={shown} 停指针={parked} 清焦点={cleared}）");
        }
        catch (Exception ex) { DebugLog.Write("[顶栏] 打开系统托盘失败：" + ex.Message); }
        finally { Interlocked.Exchange(ref _opening, 0); }
    }

    private static async Task<bool> WaitPanelAsync(int timeoutMs)
    {
        for (var waited = 0; waited < timeoutMs; waited += 15)
        {
            if (IsSystemTrayOpen()) return true;
            await Task.Delay(15);
        }
        return IsSystemTrayOpen();
    }

    /// <summary>
    /// 把鼠标停到托盘面板的空白处（左侧内边距中线），让指针离开任务栏折叠箭头，
    /// 系统就不会再弹"显示隐藏的图标"。返回是否停好；落点必须先经 UI 自动化确认不是图标。
    /// </summary>
    /// <summary>托盘面板窗口矩形（没打开返回 false）。</summary>
    private static bool TryGetPanelRect(out RECT rect)
    {
        rect = default;
        var hwnd = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;
        return GetWindowRect(hwnd, out rect);
    }

    private static bool IsInsidePanel(RECT panel, tagRECT element) =>
        element.left >= panel.Left - 2 && element.top >= panel.Top - 2 &&
        element.right <= panel.Right + 2 && element.bottom <= panel.Bottom + 2;

    private static bool TryParkCursorOnPanel()
    {
        if (!TryGetPanelRect(out var rect)) return false;
        if (!GetCursorPos(out var origin)) return false;

        var icons = IconRects(rect);
        var midY = rect.Top + (rect.Bottom - rect.Top) / 2;
        var midX = rect.Left + (rect.Right - rect.Left) / 2;
        var candidates = new (int X, int Y)[]
        {
            (rect.Left + 8, midY),                                  // 左侧内边距中线
            (midX, rect.Top + 8),                                   // 顶部内边距中线
            (midX, rect.Bottom - 8),                                // 底部内边距中线
            (rect.Left + 8, rect.Bottom - 8),                       // 左下角内边距
        };

        foreach (var (x, y) in candidates)
        {
            if (!IsBlankPanelPoint(rect, icons, x, y)) continue;
            if (!SetCursorPos(x, y)) continue;
            RememberBlankPoint(rect, icons, x, y);
            _parkOrigin = origin;
            _parkPoint = new POINT { X = x, Y = y };
            return true;
        }
        return false;
    }

    // ---- 空白落点缓存：托盘面板的布局基本固定，缓存后第二次起可以几毫秒内停好指针并点下去 ----

    private static bool _hasBlankCache;
    private static RECT _cachedPanel;
    private static List<tagRECT> _cachedIcons = [];
    private static POINT _cachedBlank;

    private static void RememberBlankPoint(RECT panel, List<tagRECT> icons, int x, int y)
    {
        _cachedPanel = panel;
        _cachedIcons = icons;
        _cachedBlank = new POINT { X = x, Y = y };
        _hasBlankCache = true;
    }

    /// <summary>用缓存的空白点快速停指针（面板位置或尺寸变了就放弃，走慢路径重算）。</summary>
    private static bool TryParkFast()
    {
        if (!_hasBlankCache) return false;
        if (!TryGetPanelRect(out var rect)) return false;
        if (Math.Abs(rect.Left - _cachedPanel.Left) > 8 || Math.Abs(rect.Top - _cachedPanel.Top) > 8 ||
            Math.Abs(rect.Right - _cachedPanel.Right) > 8 || Math.Abs(rect.Bottom - _cachedPanel.Bottom) > 8)
            return false;
        if (!IsBlankPanelPoint(rect, _cachedIcons, _cachedBlank.X, _cachedBlank.Y)) return false;
        if (_preParked)
        {
            // 打开前已经把指针停好了，这里不要再抢一次（否则会把恢复位置记成面板里的坐标）
            _preParked = false;
            return true;
        }
        if (!GetCursorPos(out var origin)) return false;
        if (!SetCursorPos(_cachedBlank.X, _cachedBlank.Y)) return false;

        _parkOrigin = origin;
        _parkPoint = _cachedBlank;
        return true;
    }

    /// <summary>面板确认已打开后开始盯着它：一旦关闭就把指针放回原处。</summary>
    private static void StartParkWatcher()
    {
        _parkTimer?.Dispose();
        // 80ms 一次：面板关掉的一瞬间键盘焦点会回到折叠箭头，系统提示可能立刻弹出，
        // 探测得越早，关掉它的时间窗越短。
        _parkTimer = new Timer(_ => RestoreParkedCursor(), null, 80, 80);
    }

    /// <summary>
    /// 打开面板之前先把指针挪到缓存的空白点：Win+B 会把键盘焦点送到通知区域，
    /// 指针压在折叠箭头上时系统就会弹"显示隐藏的图标"提示，早点挪走可以避免触发。
    /// </summary>
    private static bool TryPreParkCursor()
    {
        if (!_hasBlankCache || _preParked) return false;
        if (!GetCursorPos(out var origin)) return false;
        if (!SetCursorPos(_cachedBlank.X, _cachedBlank.Y)) return false;
        _parkOrigin = origin;
        _parkPoint = _cachedBlank;
        _preParked = true;
        return true;
    }

    private static bool _preParked;

    /// <summary>
    /// 把系统任务栏的气泡提示（tooltips_class32，比如"显示隐藏的图标"）先藏起来。
    /// 只隐藏"当前正在显示"的那个，下次悬停系统会重新弹，不影响用户正常使用。
    /// </summary>
    private static int HideTrayTooltips()
    {
        var hidden = 0;
        try
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(taskbar, out var taskbarPid);

            var buffer = new StringBuilder(64);
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                buffer.Clear();
                if (GetClassName(hwnd, buffer, buffer.Capacity) <= 0) return true;
                var cls = buffer.ToString();
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != taskbarPid) return true;

                if (!GetWindowRect(hwnd, out var r)) return true;
                var w = r.Right - r.Left;
                var h = r.Bottom - r.Top;
                // 只处理"气泡提示"尺寸的小窗（宽 40~700、高 16~300），避免误伤开始菜单等大面板
                if (w < 40 || w > 700 || h < 16 || h > 300) return true;
                // 必须在屏幕右上角托盘区域内（防止误伤别处的弹出菜单）
                var screenW = GetSystemMetrics(0);
                if (r.Right <= screenW - 900 || r.Top > 320) return true;

                // 系统"显示隐藏的图标"提示在 Win11 上是 explorer 的 XAML 弹出窗（Xaml_WindowedPopupClass），
                // 老式的是 tooltips_class32，两种都处理。
                var isTooltip = cls.Contains("tooltip", StringComparison.OrdinalIgnoreCase)
                    || cls.Equals("Xaml_WindowedPopupClass", StringComparison.OrdinalIgnoreCase)
                    || cls.Equals("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase);
                if (!isTooltip) return true;

                if (ShowWindow(hwnd, SW_HIDE))
                {
                    hidden++;
                    if (hidden <= 2) DebugLog.Write($"[顶栏] 隐藏气泡 {cls} {w}x{h} @{r.Left},{r.Top}");
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 提示藏不掉也不影响打开托盘
        }
        return hidden;
    }

    private const int SW_HIDE = 0;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>诊断用：关闭/打开瞬间把"右上角托盘区域"里所有可见顶层窗口（不限进程）记一行。</summary>
    private static void LogTrayWindows(string tag)
    {
        try
        {
            var screenW = GetSystemMetrics(0);
            var areaLeft = Math.Max(0, screenW - 900);
            var sb = new StringBuilder();
            var buffer = new StringBuilder(128);

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (!GetWindowRect(hwnd, out var r)) return true;
                if (r.Right <= areaLeft || r.Bottom <= 0 || r.Top > 300) return true;
                buffer.Clear();
                GetClassName(hwnd, buffer, buffer.Capacity);
                GetWindowThreadProcessId(hwnd, out var pid);
                var name = "";
                try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
                sb.Append($"[{buffer}/{name}#{pid} {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}] ");
                return true;
            }, IntPtr.Zero);

            DebugLog.Write($"[托盘诊断] {tag} {sb}");
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    /// <summary>面板没打开时立刻把指针放回去（不走轮询）。</summary>
    private static void RestoreParkedCursorNow()
    {
        try
        {
            if (GetCursorPos(out var current) &&
                Math.Abs(current.X - _parkPoint.X) <= 6 && Math.Abs(current.Y - _parkPoint.Y) <= 6)
            {
                SetCursorPos(_parkOrigin.X, _parkOrigin.Y);
            }
            _parkTimer?.Dispose();
            _parkTimer = null;
        }
        catch
        {
        }
    }

    private static POINT _parkOrigin;
    private static POINT _parkPoint;
    private static Timer? _parkTimer;

    /// <summary>面板关掉后把指针放回原处；用户自己动过鼠标就不再插手。</summary>
    private static void RestoreParkedCursor()
    {
        try
        {
            if (IsSystemTrayOpen())
            {
                HideTrayTooltips();   // 面板开着期间也顺手盯着那个提示
                return;
            }

            if (GetCursorPos(out var current) &&
                Math.Abs(current.X - _parkPoint.X) <= 6 && Math.Abs(current.Y - _parkPoint.Y) <= 6)
            {
                SetCursorPos(_parkOrigin.X, _parkOrigin.Y);
            }
            _parkTimer?.Dispose();
            _parkTimer = null;
            // 关闭瞬间键盘焦点会回到任务栏折叠箭头，系统会再弹一次"显示隐藏的图标"
            LogTrayWindows("面板关闭时");
            StartTooltipBurst();
        }
        catch
        {
            // 轮询线程上的异常不能外抛，否则会带走进程
        }
    }

    private static Timer? _tooltipBurst;

    /// <summary>在指定时长内反复把系统气泡提示藏掉（提示出现的时间不固定，单次藏不可靠）。</summary>
    private static void StartTooltipBurst(int milliseconds = 1800)
    {
        _tooltipBurst?.Dispose();
        var deadline = Environment.TickCount64 + milliseconds;
        var hidden = 0;
        _tooltipBurst = new Timer(_ =>
        {
            try
            {
                hidden += HideTrayTooltips();
                if (Environment.TickCount64 < deadline) return;
                _tooltipBurst?.Dispose();
                _tooltipBurst = null;
                if (hidden > 0) DebugLog.Write($"[顶栏] 关闭托盘后隐藏系统气泡提示 {hidden} 次");
            }
            catch
            {
                // 计时器线程上的异常不能外抛
            }
        }, null, 0, 80);
    }

    /// <summary>
    /// 在当前指针位置轻点一下，让 WinUI 从"键盘模式"切到"指针模式"（撤掉焦点框）。
    /// 点之前先用 UI 自动化确认落点确实是面板空白：不是空白就不点，避免误触图标或点到面板外把它关掉。
    /// </summary>
    private static bool ClickAtCursor()
    {
        try
        {
            if (!TryGetPanelRect(out var rect)) return false;
            if (!GetCursorPos(out var point)) return false;
            if (!IsBlankPanelPoint(rect, IconRects(rect), point.X, point.Y)) return false;

            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>托盘面板里当前是否有"图标"拿到了键盘焦点（有的话就会画那圈选中框）。</summary>
    private static bool IsPanelIconFocused()
    {
        try
        {
            if (!TryGetPanelRect(out var rect)) return false;

            var focused = new CUIAutomation().GetFocusedElement();
            if (focused is null) return false;
            if (!IsIconControlType(focused.CurrentControlType)) return false;
            return IsInsidePanel(rect, focused.CurrentBoundingRectangle);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 面板里所有托盘图标的屏幕矩形。用于挑一个"绝不会点到图标"的空白落点：
    /// 面板是 XAML 岛，ElementFromPoint 拿到的常是"主机弹出窗口"，靠它判断不了空白，
    /// 所以改成"面板矩形内、且不在任何图标矩形里"。
    /// </summary>
    private static List<tagRECT> IconRects(RECT panel)
    {
        var result = new List<tagRECT>();
        try
        {
            var hwnd = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
            if (hwnd == IntPtr.Zero) return result;

            var automation = new CUIAutomation();
            var root = automation.ElementFromHandle(hwnd);
            if (root is null) return result;

            var all = root.FindAll(TreeScope.TreeScope_Descendants, automation.CreateTrueCondition());
            if (all is null) return result;

            var count = Math.Min(all.Length, 200);
            for (var i = 0; i < count; i++)
            {
                var element = all.GetElement(i);
                if (element is null) continue;
                if (!IsIconControlType(element.CurrentControlType)) continue;

                var rect = element.CurrentBoundingRectangle;
                if (rect.right <= rect.left || rect.bottom <= rect.top) continue;
                if (!IsInsidePanel(panel, rect)) continue;
                result.Add(rect);
            }
        }
        catch
        {
            // 读不到就当没有图标，交给调用方的几何判断
        }
        return result;
    }

    /// <summary>点是否在面板内、但不在任何图标上（图标矩形外扩 4px 当安全边界）。</summary>
    private static bool IsBlankPanelPoint(RECT panel, List<tagRECT> icons, int x, int y)
    {
        const int inset = 6;   // 离面板圆角边缘留一点，避免点在透明区
        if (x < panel.Left + inset || x > panel.Right - inset) return false;
        if (y < panel.Top + inset || y > panel.Bottom - inset) return false;

        foreach (var icon in icons)
        {
            if (x >= icon.left - 4 && x <= icon.right + 4 &&
                y >= icon.top - 4 && y <= icon.bottom + 4) return false;
        }
        return true;
    }

    private static bool IsIconControlType(int controlType) => controlType
        is UIA_ButtonControlTypeId or UIA_ListItemControlTypeId or UIA_MenuItemControlTypeId
        or UIA_ImageControlTypeId or UIA_HyperlinkControlTypeId;

    private const int UIA_ButtonControlTypeId = 50000;
    private const int UIA_HyperlinkControlTypeId = 50005;
    private const int UIA_ImageControlTypeId = 50006;
    private const int UIA_ListItemControlTypeId = 50007;
    private const int UIA_MenuItemControlTypeId = 50011;

    /// <summary>把面板的"键盘焦点提示"关掉（UISF_HIDEFOCUS），窗口没出现就返回 false 让调用方稍后再试。</summary>
    private static bool HideSystemTrayFocusCue()
    {
        var hwnd = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;

        const uint WM_CHANGEUISTATE = 0x0127;
        const int UIS_SET = 1;
        const int UISF_HIDEFOCUS = 0x1;
        var action = (uint)(UIS_SET | (UISF_HIDEFOCUS << 16));
        SendMessageTimeout(hwnd, WM_CHANGEUISTATE, new UIntPtr(action), IntPtr.Zero,
            0x0002, 100, out _);
        // 托盘面板是 XAML 岛，真正的渲染窗口可能是子窗口，一并发一遍
        EnumChildWindows(hwnd, (child, _) =>
        {
            SendMessageTimeout(child, WM_CHANGEUISTATE, new UIntPtr(action), IntPtr.Zero,
                0x0002, 100, out _);
            return true;
        }, IntPtr.Zero);
        return true;
    }

    /// <summary>系统托盘溢出面板当前是否打开。</summary>
    public static bool IsSystemTrayOpen()
    {
        var hwnd = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
        return hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
    }

    /// <summary>关闭系统托盘溢出面板（焦点在面板上时 Esc 即可）。</summary>
    public static void CloseSystemTray()
    {
        KeySendService.Send(0x1B, 0);
        // 关闭后键盘焦点回到任务栏折叠箭头，系统会再弹"显示隐藏的图标"，这里提前开始清理
        StartTooltipBurst();
    }

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
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, UIntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out IntPtr result);

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
