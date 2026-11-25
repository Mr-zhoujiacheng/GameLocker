using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 手柄操作底栏的入口与门控。
///
/// 为什么单独做一条通道：MainWindow 的 <see cref="GamepadService.ButtonPressed"/> 只在启动器
/// 已呼出时派发（<see cref="GamepadService.RoutingEnabled"/>），底栏恰恰是在启动器收起后
/// 还要用的界面。这里改用 <see cref="GamepadService.ButtonDown"/>（始终触发），
/// 但只在"手柄确实该归底栏"时接管：
/// ① 设置里开启了「手柄操作底栏」；
/// ② 底栏可见；
/// ③ 启动器页面当前未接管手柄；
/// ④ 前台不是全屏游戏/游戏库程序（避免在游戏里按 A 把底栏应用拉起来）。
/// 弹层（应用组/文件夹/右键菜单/开始菜单列表）打开时优先交给弹层自己导航。
/// </summary>
internal static class DockGamepad
{
    private static bool _initialized;
    private static volatile bool _focusMode;

    public static bool FocusMode => _focusMode;

    public static bool ToggleFocusMode()
    {
        _focusMode = !_focusMode;
        StopRepeat();
        return _focusMode;
    }

    public static void ClearFocusMode()
    {
        _focusMode = false;
        StopRepeat();
    }

    /// <summary>方向键长按连发（GamepadService 的连发只走 RoutingEnabled 的页面通道，底栏要自己来）。</summary>
    private static readonly object RepeatGate = new();
    private static Timer? _repeatTimer;
    private static GamepadButtons _repeatDir;
    private static long _repeatNextMs;
    private const int RepeatDelayMs = 420;
    private const int RepeatIntervalMs = 130;

    /// <summary>订阅手柄按下事件（进程内只订阅一次；由 DockHost 初始化时调用）。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        GamepadService.ButtonDown += OnButtonDown;
    }

    /// <summary>手柄按下（GamepadService 轮询线程）。</summary>
    private static void OnButtonDown(GamepadButtons pressed)
    {
        try
        {
            if (pressed == GamepadButtons.None) return;
            if (!CanRouteDockInput())
            {
                StopRepeat();
                return;
            }

            // 底栏弹层优先：它自己维护选中项，未识别的键也不再往下传，防止误触底栏
            if (DockPopupWindow.IsOpen)
            {
                StopRepeat();
                DockPopupWindow.HandleGamepad(pressed);
                return;
            }

            var handled = DockHost.HandleGamepad(pressed);
            var dir = pressed & (GamepadButtons.DPadLeft | GamepadButtons.DPadRight);
            if (handled && dir != GamepadButtons.None) StartRepeat(dir);
            else StopRepeat();
        }
        catch (Exception ex)
        {
            DebugLog.Write("[底栏] 手柄处理失败：" + ex.Message);
        }
    }

    private static void StartRepeat(GamepadButtons dir)
    {
        lock (RepeatGate)
        {
            _repeatDir = dir;
            _repeatNextMs = Environment.TickCount64 + RepeatDelayMs;
            _repeatTimer ??= new Timer(RepeatTick, null, RepeatIntervalMs, RepeatIntervalMs);
        }
    }

    private static void StopRepeat()
    {
        lock (RepeatGate)
        {
            _repeatDir = GamepadButtons.None;
            _repeatTimer?.Dispose();
            _repeatTimer = null;
        }
    }

    private static void RepeatTick(object? _)
    {
        GamepadButtons dir;
        lock (RepeatGate)
        {
            dir = _repeatDir;
            if (dir == GamepadButtons.None) return;
            if (Environment.TickCount64 < _repeatNextMs) return;
            _repeatNextMs = Environment.TickCount64 + RepeatIntervalMs;
        }

        // 松手 / 底栏消失 / 启动器被呼出 / 设置关闭 → 停止连发
        var current = App.Main?.Gamepad.Current ?? GamepadButtons.None;
        if ((current & dir) == 0 || DockPopupWindow.IsOpen || !CanRouteDockInput())
        {
            StopRepeat();
            return;
        }

        DockHost.HandleGamepad(dir);
    }

    internal static bool CanRouteDockInput()
    {
        var gameDockRevealed = DockHost.GamepadInputRevealed;
        return SettingsService.Current.DockGamepadEnabled && DockHost.IsVisible
            && (gameDockRevealed || App.Main?.OwnsPageGamepadInput != true)
            && (gameDockRevealed || ForegroundAllowsDock());
    }

    /// <summary>
    /// 前台是否允许底栏接管手柄：桌面/本进程窗口直接放行；全屏游戏、游戏引擎窗口、
    /// 游戏库目录里的程序一律不接管（那里按 A/方向是游戏输入）；游戏态底栏显式呼出时除外。
    /// </summary>
    private static bool ForegroundAllowsDock()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return true; // 桌面上暂时没有前台窗口

        GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0) return true;
        if (pid == (uint)Environment.ProcessId) return true; // 自己的弹层/窗口

        var cls = ClassNameOf(fg);
        var exe = ProcessPathOf(pid);
        if (GameWindowDetector.IsGameForSystemBars(fg, pid)) return false;
        if (GameWindowDetector.IsDesktopApp(Path.GetFileNameWithoutExtension(exe), cls)) return true;
        if (GameWindowDetector.IsFullscreenGame(fg, pid)) return false;

        if (!string.IsNullOrEmpty(cls) && GameWindowDetector.IsGameWindowClass(cls)) return false;

        if (exe.Length > 0 && GameWindowDetector.IsGameLibraryPath(exe)) return false;

        return true;
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }
        catch { return ""; }
    }

    private static string ProcessPathOf(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName ?? "";
        }
        catch { return ""; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
}
