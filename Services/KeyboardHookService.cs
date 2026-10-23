using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）：监听"键盘呼出键"。
/// 命中（主键按下且修饰键精确匹配）时立即触发 <see cref="Triggered"/> 并吞掉该按键——无需长按。
/// 钩子必须挂在自己的消息循环线程上，故单独开后台线程安装。
/// 捕获键位期间用 <see cref="SetSuspended"/> 暂停判定，避免把正在捕获的按键吞掉。
///
/// 另外承担"替代 Windows 开始菜单"的键鼠接管（<see cref="StartMenuMode"/> = true 时生效）：
/// 吞掉 Win 键按下，抬起时触发 <see cref="StartMenuToggle"/>；按住 Win 又按其它键时
/// 补发一段带标记的 Win 按下，保证 Win+D / Win+E 等组合键仍然可用；
/// 另装 WH_MOUSE_LL 吞掉任务栏开始按钮的点击（按下与抬起都吞，避免开始菜单闪现）。
///
/// 触屏点击不在 WH_MOUSE_LL 的可控范围内（explorer 直接消费触摸，不提升为鼠标消息），
/// 故在开始按钮矩形上叠一个不可见覆盖窗（分层窗口 alpha=1：肉眼不可见但能接收输入，
/// alpha=0 则会被命中测试穿透），TOPMOST 压在任务栏之上，左键按下/抬起即触发 <see cref="StartMenuToggle"/>。
/// 覆盖窗随任务栏位置每秒刷新一次，并在每次前台变化时重新置顶（任务栏自身也是 TOPMOST）。
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    /// <summary>监听的键盘虚拟键码（0=不监听）</summary>
    public int Key { get; set; } = 0x76; // F7

    /// <summary>需精确匹配的修饰键标志（Ctrl=1/Shift=2/Alt=4/Win=8，见 KeySendService.Mod*）</summary>
    public int Mods { get; set; }

    private volatile bool _startMenuMode;

    /// <summary>是否处于"替代 Windows 开始菜单"模式：接管 Win 键与任务栏开始按钮。
    /// 赋值会唤醒钩子线程刷新开始按钮覆盖窗（跨线程，故用 PostMessage 而非直接调用）</summary>
    public bool StartMenuMode
    {
        get => _startMenuMode;
        set
        {
            if (_startMenuMode == value) return;
            _startMenuMode = value;
            var cover = _cover;
            if (cover != IntPtr.Zero) PostMessage(cover, WM_APP_REFRESH_COVER, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>命中时触发（在钩子线程上，调用方需自行 marshal 到 UI 线程）</summary>
    public event Action? Triggered;

    /// <summary>开始菜单模式：Win 键单击 / 开始按钮被点击（在钩子线程上，调用方需自行 marshal 到 UI 线程）</summary>
    public event Action? StartMenuToggle;

    /// <summary>开始菜单模式：系统开始菜单被打开（开始按钮触屏点击等钩子抓不到的路径）。
    /// 触发前已把该窗口隐藏，调用方只需切换本启动器显隐（在钩子线程上，需自行 marshal）</summary>
    public event Action? StartMenuOpened;

    private volatile bool _suspended;
    private volatile bool _running;
    private volatile bool _suppressUp;
    private uint _threadId;
    private IntPtr _hook;
    private IntPtr _mouseHook;
    private IntPtr _winEvent;
    private IntPtr _winEventShow;
    private IntPtr _winEventCreate;
    private HOOKPROC? _proc;
    private HOOKPROC? _mouseProc;
    private WinEventProc? _winEventProc;
    private Thread? _thread;

    /// <summary>标记自己注入的事件（写入 dwExtraInfo），钩子据此放行，避免补发的 Win 键被自己吞掉</summary>
    private static readonly IntPtr InjectMarker = (IntPtr)0x474C4B53; // "GLKS"

    private bool _winHeld;            // 物理 Win 键正被按住（按下已被吞掉）
    private bool _winCombo;           // 本次 Win 按住期间已判定为组合键（已补发 Win 按下）
    private bool _suppressStartClick; // 开始按钮按下已被吞，抬起也要一并吞掉

    private RECT _startBtnRect;       // 任务栏开始按钮的屏幕矩形（缓存）
    private uint _startBtnResolvedAt; // 上次解析该矩形的 TickCount（0=未解析）

    // ---- 开始按钮覆盖窗（触屏拦截）----
    private IntPtr _cover;            // 不可见（alpha=1）TOPMOST 覆盖窗，压住任务栏开始按钮
    private WndProc? _coverProc;      // 防 GC 回收
    private bool _coverDown;          // 覆盖窗上左键已按下（抬起时触发切换）

    // ---- 呼出瞬间的"开始菜单封锁"（防止原版菜单先画出来）----
    private Timer? _blockTimer;   // 50ms 轮询，封锁期内结束开始/搜索宿主进程
    private long _blockUntil;     // 封锁截止 TickCount64
    private long _diagUntil;      // 诊断日志截止 TickCount64（呼出后记录大窗口事件）

    /// <summary>暂停/恢复判定（捕获键位期间暂停，避免钩子把用户正在按的键吞掉）</summary>
    public void SetSuspended(bool value) => _suspended = value;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "GameLocker.KeyHook" };
        _thread.Start();
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        _blockTimer?.Dispose();
        _blockTimer = null;
        // 投递 WM_QUIT 唤醒消息循环，让钩子线程退出并卸载钩子
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private void ThreadProc()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback; // 保持委托存活，避免被 GC 回收导致回调跳飞
        // 开机自启时本进程可能早于资源管理器/任务栏就绪，钩子偶尔装不上。
        // 失败不要直接退出（否则开机后 Win 键、开始按钮、边缘手势的接管全部失效），改为持续重试。
        while (_running && (_hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0)) == IntPtr.Zero)
        {
            DebugLog.Write("键盘钩子安装失败，2 秒后重试");
            Thread.Sleep(2000);
        }
        if (!_running)
        {
            _threadId = 0;
            return;
        }
        // 鼠标钩子（开始按钮拦截）失败不影响键盘功能
        _mouseProc = MouseCallback;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        // 前台变化监听 + 窗口显示监听：触屏点开始按钮不会产生鼠标钩子消息（explorer 自己处理触摸），
        // 改为监听"系统开始/搜索窗口出现"这一信号，命中即刻结束其宿主进程。
        // 显示事件比前台事件更早，能赶在菜单画出第一帧之前把它按掉。
        _winEventProc = WinEventCallback;
        _winEvent = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _winEventShow = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero,
            _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _winEventCreate = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_CREATE, IntPtr.Zero,
            _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        CreateCover();
        DebugLog.Write($"输入接管已就绪：键盘钩子={_hook != IntPtr.Zero} 鼠标钩子={_mouseHook != IntPtr.Zero} " +
                       $"前台监听={_winEvent != IntPtr.Zero} 显示监听={_winEventShow != IntPtr.Zero} " +
                       $"创建监听={_winEventCreate != IntPtr.Zero} 覆盖窗={_cover != IntPtr.Zero}");
        // 版本校验用：掌机日志里能看到实际运行的构建时间，避免"装的是旧版"却查不到
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            DebugLog.Write($"开始菜单接管：模式={_startMenuMode} 程序={exePath} " +
                           $"构建={File.GetLastWriteTime(exePath):yyyy-MM-dd HH:mm:ss}");
        }
        catch { /* 读不到文件时间不影响接管 */ }

        while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_cover != IntPtr.Zero)
        {
            DestroyWindow(_cover);
            _cover = IntPtr.Zero;
        }
        if (_winEvent != IntPtr.Zero)
        {
            UnhookWinEvent(_winEvent);
            _winEvent = IntPtr.Zero;
        }
        if (_winEventShow != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventShow);
            _winEventShow = IntPtr.Zero;
        }
        if (_winEventCreate != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventCreate);
            _winEventCreate = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _threadId = 0;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _running && !_suspended)
        {
            var msg = (int)wParam;
            var isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            var isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vk = (int)data.vkCode;
                var injected = data.dwExtraInfo == InjectMarker;

                // ---- 开始菜单模式：接管 Win 键 ----
                if (StartMenuMode && !injected && (vk == VK_LWIN || vk == VK_RWIN))
                {
                    if (isDown)
                    {
                        _winHeld = true;
                        _winCombo = false;
                        return (IntPtr)1; // 吞掉按下，系统开始菜单不会弹出
                    }
                    _winHeld = false;
                    // 组合键：补发的 Win 按下已送达系统，这里放行抬起与之配对
                    if (_winCombo) return CallNextHookEx(_hook, nCode, wParam, lParam);
                    StartMenuToggle?.Invoke();
                    return (IntPtr)1;
                }
                // Win 按住期间又按下其它键 → 判定为组合键：补发一段带标记的 Win 按下使组合成立
                if (StartMenuMode && _winHeld && isDown && !_winCombo && !injected)
                {
                    _winCombo = true;
                    KeySendService.SendKeyOnly(VK_LWIN, up: false, InjectMarker);
                }
                // 自己注入的事件一律放行（否则补发的 Win 键会被再次吞掉）
                if (injected) return CallNextHookEx(_hook, nCode, wParam, lParam);

                if (Key != 0 && vk == Key)
                {
                    if (isDown && CurrentMods() == Mods)
                    {
                        _suppressUp = true;         // 抬起也要一并吞掉，保持前后台一致
                        Triggered?.Invoke();
                        return (IntPtr)1;
                    }
                    if (isUp && _suppressUp)
                    {
                        _suppressUp = false;
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>创建开始按钮覆盖窗（隐藏，由 <see cref="ApplyCover"/> 按模式显隐与定位）</summary>
    private void CreateCover()
    {
        _coverProc = CoverWndProc;
        var hInst = GetModuleHandle(null);
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_coverProc),
            hInstance = hInst,
            lpszClassName = "GLStartCover"
        };
        RegisterClassEx(ref cls); // 类已注册时失败也无妨，直接用同名类建窗

        var h = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "GLStartCover", "", WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (h == IntPtr.Zero) return;
        // alpha=1：肉眼不可见，但命中测试仍返回本窗口（alpha=0 会被穿透，点击直达任务栏）
        SetLayeredWindowAttributes(h, 0, 1, LWA_ALPHA);
        _cover = h;
        ApplyCover();
    }

    /// <summary>按开始菜单模式与开始按钮当前矩形显隐/定位覆盖窗（仅可在钩子线程调用）。
    /// 同时装 1 秒定时器，让任务栏移动/尺寸变化后覆盖窗自动跟上</summary>
    private void ApplyCover()
    {
        if (_cover == IntPtr.Zero) return;
        if (!_startMenuMode)
        {
            KillTimer(_cover, (uint)CoverTimerId);
            if (_coverDown)
            {
                _coverDown = false;
                ReleaseCapture();
            }
            ShowWindow(_cover, SW_HIDE);
            return;
        }
        // 覆盖窗需要跟随任务栏移动，故每次强制重解析（枚举任务栏子窗口开销可忽略）
        var r = ResolveStartButtonRect(force: true);
        if (r.Right <= r.Left || r.Bottom <= r.Top)
        {
            // 任务栏自动隐藏/未就绪时暂时让开。开机自启常早于任务栏创建，
            // 这里必须先挂上定时器持续重试，否则覆盖窗会永远保持隐藏（开机后开始按钮失效）。
            ShowWindow(_cover, SW_HIDE);
            SetTimer(_cover, (uint)CoverTimerId, 1000, IntPtr.Zero);
            return;
        }
        SetWindowPos(_cover, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetTimer(_cover, (uint)CoverTimerId, 1000, IntPtr.Zero);

        // 兜底：某些触屏上滑路径不产生前台/显示事件，每秒顺带检查一次前台是不是开始/搜索 UI
        var fg = GetForegroundWindow();
        if (IsStartMenuProcessWindow(fg) && IsWindowVisible(fg))
        {
            KillStartUiProcesses(fg);
            StartMenuOpened?.Invoke();
        }
    }

    /// <summary>把覆盖窗重新压到任务栏之上（任务栏本身也是 TOPMOST，前台变化可能重建 Z 序）</summary>
    private void ReassertCoverTop()
    {
        if (_cover == IntPtr.Zero || !_startMenuMode) return;
        SetWindowPos(_cover, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
    }

    private IntPtr CoverWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_REFRESH_COVER:
                ApplyCover();
                return IntPtr.Zero;
            case WM_TIMER:
                if (wParam == CoverTimerId) ApplyCover();
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
                if (!_startMenuMode) break;
                _coverDown = true;
                SetCapture(hwnd); // 保证抬起一定回到本窗口（即使手指滑出按钮）
                return IntPtr.Zero; // 吞掉，绝不让点击到达任务栏
            case WM_LBUTTONUP:
                if (!_startMenuMode) break;
                if (_coverDown)
                {
                    _coverDown = false;
                    ReleaseCapture();
                    StartMenuToggle?.Invoke(); // 钩子线程，调用方自行 marshal
                }
                return IntPtr.Zero;
            // 触摸屏：先到的是指针消息。消费掉可阻止系统再提升为鼠标消息，
            // 也保证触摸事件完全落在本窗口、不会路由到任务栏
            case WM_POINTERDOWN:
                if (!_startMenuMode) break;
                _coverDown = true;
                return IntPtr.Zero;
            case WM_POINTERUP:
                if (!_startMenuMode) break;
                if (_coverDown)
                {
                    _coverDown = false;
                    StartMenuToggle?.Invoke();
                }
                return IntPtr.Zero;
            case WM_POINTERCANCEL:
                _coverDown = false;
                return IntPtr.Zero;
            case WM_CAPTURECHANGED:
                _coverDown = false;
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 前台/窗口显示回调：开始菜单模式下一旦出现系统开始/搜索界面，立刻结束其宿主进程。
    /// 宿主不是已知进程时（某些系统版本把它挂在别的进程里）退化为隐藏 + ESC。
    /// 同时呼出后的 8 秒内记录所有大窗口事件，用于定位掌机上究竟是哪个进程画出了开始菜单。
    /// </summary>
    private void WinEventCallback(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime)
    {
        if (!_running || _suspended) return;
        if (eventId == EVENT_SYSTEM_FOREGROUND) ReassertCoverTop();
        if (!_startMenuMode) return;
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (Environment.TickCount64 < Interlocked.Read(ref _diagUntil))
            LogDiagEvent(eventId, hwnd);
        if (!IsStartUiWindow(hwnd)) return;
        // 创建事件的窗口此时还没显示，不做可见性要求；显示/前台事件要求真的可见
        if (eventId != EVENT_OBJECT_CREATE && !IsWindowVisible(hwnd)) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        var exe = ProcessImageName(pid);
        DebugLog.Write($"开始菜单: 拦截系统开始/搜索界面 event={eventId} hwnd={hwnd} pid={pid} exe={exe}");
        if (exe.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            KillStartUiProcesses(hwnd);
        }
        else
        {
            // 未知宿主（例如被 explorer 自己承载）：不能杀 explorer，退回 ESC + 隐藏
            HideForeignStartWindow(hwnd);
        }
        // 创建事件只做拦截：此时系统还没打算"打开"菜单，不能顺手把启动器拉出来
        if (eventId != EVENT_OBJECT_CREATE) StartMenuOpened?.Invoke();
    }

    /// <summary>开始界面窗口识别：已知宿主进程，或任何进程的 CoreWindow 且标题是开始/搜索</summary>
    private static bool IsStartUiWindow(IntPtr hwnd)
    {
        if (IsStartMenuProcessWindow(hwnd)) return true;
        var cls = GetClassNameString(hwnd);
        if (!string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
            return false;
        var title = WindowText(hwnd);
        return title is "开始" or "搜索" or "Start" or "Search";
    }

    /// <summary>非开始/搜索宿主进程的窗口：ESC 让它自行收起，再隐藏，避免杀系统进程</summary>
    private static void HideForeignStartWindow(IntPtr hwnd)
    {
        try
        {
            if (GetForegroundWindow() == hwnd)
            {
                KeySendService.SendKeyOnly(VK_ESCAPE, up: false, InjectMarker);
                KeySendService.SendKeyOnly(VK_ESCAPE, up: true, InjectMarker);
            }
        }
        catch { /* 注入失败：直接隐藏 */ }
        ShowWindow(hwnd, SW_HIDE);
        DebugLog.Write($"开始菜单: 未知宿主窗口已隐藏 hwnd={hwnd}");
    }

    /// <summary>诊断：记录呼出后出现的大窗口（>=200x200），便于判断开始菜单到底由谁绘制</summary>
    private static void LogDiagEvent(uint eventId, IntPtr hwnd)
    {
        try
        {
            GetWindowRect(hwnd, out var r);
            if (r.Right - r.Left < 200 || r.Bottom - r.Top < 200) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return;
            DebugLog.Write($"诊断窗口: event={eventId} pid={pid} exe={ProcessImageName(pid)} " +
                           $"cls={GetClassNameString(hwnd)} title={WindowText(hwnd)} " +
                           $"rect={r.Left},{r.Top},{r.Right},{r.Bottom} visible={IsWindowVisible(hwnd)}");
        }
        catch { /* 诊断日志失败不影响拦截 */ }
    }

    /// <summary>
    /// 呼出/切换启动器时开启约 2.5 秒封锁期：每 50ms 结束一次开始/搜索宿主进程。
    /// 触屏上滑时 explorer 会同时拉起开始菜单，窗口 100ms 内就会画出来，
    /// 靠"出现后再隐藏/结束"必然闪一帧；提前把这个进程按死，菜单就没有机会显示。
    /// </summary>
    public void BeginStartUiBlockWatch(int durationMs = 2500)
    {
        if (!_startMenuMode) return;
        Interlocked.Exchange(ref _blockUntil, Environment.TickCount64 + durationMs);
        Interlocked.Exchange(ref _diagUntil, Environment.TickCount64 + 8000);
        _blockTimer ??= new Timer(_ => BlockWatchTick(), null, 0, 50);
        _blockTimer.Change(0, 50);
    }

    private void BlockWatchTick()
    {
        if (!_startMenuMode || Environment.TickCount64 > Interlocked.Read(ref _blockUntil))
        {
            _blockTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }
        KillNamedProcesses("StartMenuExperienceHost");
        KillNamedProcesses("SearchHost");
        SweepStartUiWindows();
    }

    /// <summary>
    /// 兜底扫描：某些系统版本把开始界面挂在别的进程（例如 explorer）里，
    /// 按窗口类 CoreWindow + 标题开始/搜索 识别，宿主是已知进程就结束，否则 ESC + 隐藏。
    /// 只查窗口类名和标题，开销很小，可在封锁期内 50ms 跑一次。
    /// </summary>
    private static void SweepStartUiWindows()
    {
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = GetClassNameString(h);
            if (!string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
                return true;
            if (WindowText(h) is not ("开始" or "搜索" or "Start" or "Search")) return true;
            GetWindowThreadProcessId(h, out var pid);
            var exe = ProcessImageName(pid);
            if (exe.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
                || exe.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase))
                KillProcess(pid);
            else
                HideForeignStartWindow(h);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>结束系统开始/搜索界面宿主进程（触发窗口的进程 + 当前所有宿主实例）</summary>
    private static void KillStartUiProcesses(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0) KillProcess(pid);
        KillNamedProcesses("StartMenuExperienceHost");
        KillNamedProcesses("SearchHost");
    }

    private static void KillNamedProcesses(string name)
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                KillProcess((uint)p.Id);
        }
        catch { /* 进程枚举失败：忽略本次 */ }
    }

    private static void KillProcess(uint pid)
    {
        if (pid == 0) return;
        var h = OpenProcess(PROCESS_TERMINATE, false, pid);
        if (h == IntPtr.Zero)
        {
            DebugLog.Write($"开始菜单: 打开进程失败 pid={pid} err={Marshal.GetLastWin32Error()}");
            return;
        }
        try
        {
            if (!TerminateProcess(h, 0))
                DebugLog.Write($"开始菜单: 结束进程失败 pid={pid} err={Marshal.GetLastWin32Error()}");
        }
        finally { CloseHandle(h); }
    }

    /// <summary>指定窗口是否属于系统开始 / 搜索 UI。</summary>
    public static bool IsStartMenuProcessWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        var exe = ProcessImageName(pid);
        // Win11 24H2 起开始菜单界面由 SearchHost 承载（窗口标题「搜索」），
        // 旧 build 仍是 StartMenuExperienceHost（标题「开始」），两者都要认。
        return exe.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>进程可执行文件名（取不到返回空串）</summary>
    private static string ProcessImageName(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var buf = new StringBuilder(512);
            uint n = (uint)buf.Capacity;
            if (!QueryFullProcessImageName(h, 0, buf, ref n)) return "";
            var full = buf.ToString(0, (int)n);
            var slash = full.LastIndexOf('\\');
            return slash >= 0 ? full[(slash + 1)..] : full;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>鼠标钩子：开始菜单模式下吞掉任务栏开始按钮的点击，抬起时触发呼出/收起</summary>
    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _running && !_suspended && StartMenuMode)
        {
            var msg = (int)wParam;
            if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (data.dwExtraInfo != InjectMarker)
                {
                    if (IsStartButtonClick(data.pt))
                    {
                        if (msg == WM_LBUTTONDOWN)
                        {
                            _suppressStartClick = true;
                            return (IntPtr)1;
                        }
                        if (_suppressStartClick)
                        {
                            _suppressStartClick = false;
                            StartMenuToggle?.Invoke();
                            return (IntPtr)1;
                        }
                    }
                    else if (msg == WM_LBUTTONDOWN) _suppressStartClick = false;
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 点击点是否落在任务栏开始按钮上。
    /// Win11 的任务栏改由 XAML 绘制：开始按钮窗口（类名 "Start"、标题"开始"）位置仍然正确，
    /// 但被设为隐藏——WindowFromPoint 只会返回覆盖整条任务栏的 XAML 宿主窗口
    /// （Windows.UI.Composition.DesktopWindowContentBridge），因此按类名判定永远不会命中。
    /// 这里改为在任务栏子窗口里找到 "Start" 窗口取它的矩形做命中测试；
    /// 同时保留类名判定，兼容能直接被 WindowFromPoint 命中的情况（Win10）。
    /// </summary>
    private bool IsStartButtonClick(POINT pt)
    {
        if (IsStartButtonClass(WindowFromPoint(pt))) return true;
        var r = StartButtonRect();
        return r.Right > r.Left && pt.x >= r.Left && pt.x < r.Right
                               && pt.y >= r.Top && pt.y < r.Bottom;
    }

    /// <summary>任务栏上开始按钮窗口的屏幕矩形（5 秒内缓存，避免每次点击都枚举任务栏子窗口）</summary>
    private RECT StartButtonRect() => ResolveStartButtonRect(force: false);

    private RECT ResolveStartButtonRect(bool force)
    {
        var now = (uint)Environment.TickCount;
        if (!force && _startBtnResolvedAt != 0 && now - _startBtnResolvedAt < 5000) return _startBtnRect;
        _startBtnResolvedAt = now == 0 ? 1 : now;
        var b = StartButtonBounds();
        _startBtnRect = new RECT { Left = b.x, Top = b.y, Right = b.x + b.w, Bottom = b.y + b.h };
        return _startBtnRect;
    }

    /// <summary>
    /// 任务栏开始按钮的屏幕矩形（取不到时宽高为 0）。
    /// 供主窗口按开始按钮位置决定面板的水平对齐（按钮居中→面板居中，按钮靠左→面板贴左）。
    /// 任务栏重建的瞬间枚举可能失败或拿到空矩形，这时返回上次的有效值，
    /// 避免面板在"居中/靠边"之间来回跳。
    /// </summary>
    public static (int x, int y, int w, int h) StartButtonBounds()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return _lastStartBtn;
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(tray, (h, _) =>
        {
            if (found == IntPtr.Zero && GetClassNameString(h) == "Start") found = h;
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero || !GetWindowRect(found, out var r)) return _lastStartBtn;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return _lastStartBtn;
        _lastStartBtn = (r.Left, r.Top, w, h);
        return _lastStartBtn;
    }

    /// <summary>上次解析到的有效开始按钮矩形（任务栏重建期间回落用）</summary>
    private static (int x, int y, int w, int h) _lastStartBtn;

    /// <summary>窗口本身或往上数层的父窗口是否为开始按钮（类名 "Start"）</summary>
    private static bool IsStartButtonClass(IntPtr hwnd)
    {
        for (var i = 0; i < 4 && hwnd != IntPtr.Zero; i++)
        {
            if (GetClassNameString(hwnd) == "Start") return true;
            hwnd = GetParent(hwnd);
        }
        return false;
    }

    private static string GetClassNameString(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var buf = new StringBuilder(256);
        var n = GetClassName(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString(0, n) : "";
    }

    /// <summary>窗口标题（取不到返回空串）</summary>
    private static string WindowText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var buf = new StringBuilder(256);
        var n = GetWindowText(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString(0, n) : "";
    }

    /// <summary>当前按住的修饰键（左/右任一）</summary>
    private static int CurrentMods()
    {
        var m = 0;
        if (IsDown(VK_CONTROL)) m |= KeySendService.ModCtrl;
        if (IsDown(VK_SHIFT)) m |= KeySendService.ModShift;
        if (IsDown(VK_MENU)) m |= KeySendService.ModAlt;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= KeySendService.ModWin;
        return m;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // ---- Win32 P/Invoke ----

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int SW_HIDE = 0;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_TERMINATE = 0x0001;

    // ---- 开始按钮覆盖窗 ----
    private const uint WM_APP_REFRESH_COVER = 0x8000 + 1;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_CAPTURECHANGED = 0x0215;
    private const uint WM_POINTERDOWN = 0x0246;
    private const uint WM_POINTERUP = 0x0247;
    private const uint WM_POINTERCANCEL = 0x0248;
    private const int CoverTimerId = 1;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate IntPtr HOOKPROC(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventId, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint msTime);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HOOKPROC lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
        StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    // ---- 覆盖窗相关 ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y,
        int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, uint nIdEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, uint uIdEvent);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
