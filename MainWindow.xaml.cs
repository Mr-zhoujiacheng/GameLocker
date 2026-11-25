using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using GameLocker.Models;
using GameLocker.Services;
using GameLocker.Controls;

namespace GameLocker;

/// <summary>
/// 主窗口：Frame 容器（MainPage / SettingsPage 导航）+ 全局手柄路由 + 侧边栏布局。
/// 主页按键：方向=导航，A=启动，Y=删除，X=添加，R3=编辑，L3=排列，LB/RB=翻页，View=设置，B=隐藏。
/// 设置页按键：方向=选项，A=切换，View/B=返回。
/// </summary>
public sealed partial class MainWindow : Window
{
    public GamepadService Gamepad { get; } = new();

    /// <summary>全局键盘呼出键钩子：命中即刻呼出窗口（无需长按，仅键盘，可含修饰键组合）</summary>
    public KeyboardHookService KeyboardHook { get; } = new();

    private bool _exiting;
    private bool _allowClose;
    internal bool IsExiting => _exiting;
    private bool _fullscreen;
    private EdgeGestureService? _edgeGesture;

    private readonly DispatcherTimer _foregroundFallback = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _foregroundRefreshQueued;

    /// <summary>手柄是否派发到页面（与 Gamepad.RoutingEnabled 同步；仅窗口已呼出显示时为 true）</summary>
    private volatile bool _routing;
    private long _pageSummonedAt;

    /// <summary>开始菜单模式：失焦后延迟自动收起（点击面板外部即收起）</summary>
    private readonly Microsoft.UI.Xaml.DispatcherTimer _startMenuAutoHide =
        new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// <summary>任务栏位置/尺寸监视：开始菜单模式下任务栏被改边、改高、自动隐藏切换后，
    /// 重新计算面板停靠位置、滑入方向与边缘呼出手势所在边</summary>
    private readonly Microsoft.UI.Xaml.DispatcherTimer _taskbarWatch =
        new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private (SummonEdge edge, int x, int y, int w, int h) _taskbarSeen;
    /// <summary>上次生效的开始按钮归属端：只有它真正变化时才重排，防止面板左右跳</summary>
    private DockSide _menuSide = DockSide.Center;

    /// <summary>开始菜单模式呼出/收起的去抖时间戳：一次操作可能经多条通道同时到达</summary>
    private long _lastStartMenuToggle;

    /// <summary>窗口是否已呼出显示：由 ShowFromTray / HideToTray 维护。
    /// 不用 Window.Visible——AppWindow.Hide() 后它不一定同步翻转；
    /// 更不能用 GetForegroundWindow()——本进程在托盘期间拿不到前台权限，呼出后可能始终不是前台，
    /// 那样门控会永久卡在关闭状态（表现为"呼出后手柄完全失效"）。</summary>
    private bool _shown;

    /// <summary>启动器当前是否处于呼出显示状态（供屏幕翻译的覆盖层避让）</summary>
    public bool IsShown => _shown;

    /// <summary>已整理过的游戏窗口：同一次进入只做一次。</summary>
    private IntPtr _minimizedForGame;
    private IntPtr _gameSwipeSource;
    private readonly OptiOverlayOrder _captureOrder = new();
    private readonly DispatcherTimer _taskbarRevealWatch =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    private void RevealTaskbar()
    {
        // 开始菜单模式：呼出时把任务栏一并显示出来（对齐系统开始菜单的行为），
        // 否则全屏游戏/最大化窗口运行时任务栏不可见，看起来像"开始菜单少了任务栏"。
        if (SettingsService.Current.StartMenuMode && LosslessScalingService.ActiveOptiSession is null)
            ShowSystemTaskbar();
        // 只维护软件自己的 Z 序，不再移动/抬高任务栏，避免双方反复抢占。
        RefreshRevealedTaskbar();
        _taskbarRevealWatch.Start();
    }

    /// <summary>
    /// 开始菜单模式呼出时恢复任务栏显示。任务栏可能是被自动隐藏、被本程序下载模式隐藏，
    /// 或被其他全屏窗口的独占显示遮住；这里只做"恢复可见"，不改用户的自动隐藏配置。
    /// </summary>
    private void ShowSystemTaskbar()
    {
        if (_exiting) return;
        // 「任务栏隐藏」或「顶部任务栏模式」开启时，任何逻辑都不得把它重新显示出来，
        // 否则会和看门狗形成显示/隐藏循环，任务栏一直闪。
        if (TaskbarControl.IsHidden || TopTaskbarMode.IsActive) return;
        try
        {
            if (_dlTaskbar != IntPtr.Zero)
            {
                // 下载模式占用了任务栏：仍处于下载模式的显示窗口期内，交给 EndDownloadOverlay 恢复。
                if (FindWindow("Shell_TrayWnd", null) == _dlTaskbar) return;
                _dlTaskbar = IntPtr.Zero;
            }
            var bar = FindWindow("Shell_TrayWnd", null);
            if (bar == IntPtr.Zero) return;
            if (!IsWindowVisibleForLaunch(bar)) ShowWindowAsync(bar, SW_SHOW);
            // 任务栏保持在启动器下方：两者都置顶时按调用顺序排，这里只用 NOACTIVATE 重申一次，
            // 不抢焦点，避免呼出瞬间键盘焦点被任务栏夺走。
            SetWindowPos(bar, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch (Exception ex) { DebugLog.Write($"任务栏显示失败：{ex.Message}"); }
    }

    private void RefreshRevealedTaskbar() => EnsureLauncherAboveTaskbar();

    /// <summary>侧边栏覆盖任务栏区域：任务栏被 Shell 重新提起时，仅校正双方顺序，不抢键盘焦点。</summary>
    private void EnsureLauncherAboveTaskbar()
    {
        if (!_shown || _exiting || _hwnd == IntPtr.Zero) return;
        // Win32 ownership keeps the launcher above the output without repeatedly raising either window.
        if (_captureOrder.Update(_hwnd, LosslessScalingService.ActiveOptiSession?.HostPid ?? 0)) return;
        if (OnScreenKeyboard.IsVisible || !IsWindowVisibleForLaunch(_hwnd)) return;
        // 开始菜单模式：任务栏必须保持可见（全屏游戏/其他置顶窗口会把它重新盖住）。
        if (SettingsService.Current.StartMenuMode) ShowSystemTaskbar();
        var sidebarTopmost = (GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() & 0x8) != 0;
        var barAbove = false;
        for (var window = GetWindow(_hwnd, 3); window != IntPtr.Zero; window = GetWindow(window, 3))
        {
            if (IsWindowVisibleForLaunch(window) && WindowClass(window) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                barAbove = true;
                break;
            }
        }
        if (!sidebarTopmost || barAbove)
        {
            var ok = SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
            DebugLog.Write($"窗口层级：校正软件置顶 success={ok} taskbarAbove={barAbove}");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    private void HideForMaximizedForeground()
    {
        if (!_shown || _exiting || _summonPending || _animating) return;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd || !IsZoomed(foreground)) return;
        GetWindowThreadProcessId(foreground, out var pid);
        if ((int)pid == OwnProcessId || foreground == _gameSwipeSource || IsActiveCaptureProcess(pid)) return;
        HideToTray();
    }

    private void UpdateFullscreenGame(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || foreground == _hwnd) return;
        GetWindowThreadProcessId(foreground, out var pid);
        if ((int)pid == OwnProcessId) return;
        if (foreground != _minimizedForGame) _minimizedForGame = IntPtr.Zero;
        var target = (foreground, pid);
        var shouldMinimize = _minimizedForGame == IntPtr.Zero && !IsActiveCaptureProcess(pid);
        if (!shouldMinimize && _cpuRegisteredGame == target) return;
        if (!GameWindowDetector.IsFullscreenGame(foreground, pid)) return;
        if (shouldMinimize)
        {
            _minimizedForGame = foreground;
            MinimizeOtherWindows(foreground);
        }
        if (_cpuRegisteredGame != target)
        {
            _cpuRegisteredGame = target;
            GameCpuService.Register((int)pid);
        }
    }

    private static bool IsActiveCaptureProcess(uint pid) => pid != 0 &&
        LosslessScalingService.ActiveOptiSession is { } capture &&
        (pid == capture.GamePid || pid == capture.HostPid);

    private (IntPtr Window, uint Pid) _cpuRegisteredGame;

    /// <summary>最小化除游戏窗口与系统外壳之外的所有可见顶层窗口。</summary>
    private static void MinimizeOtherWindows(IntPtr game)
    {
        var shell = GetShellWindow();
        var tray = FindWindow("Shell_TrayWnd", null);
        var tray2 = FindWindow("Shell_SecondaryTrayWnd", null);
        EnumWindows((h, _) =>
        {
            if (h == game || h == shell || h == tray || h == tray2) return true;
            if (!IsWindowVisibleForLaunch(h)) return true;
            var cls = WindowClass(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "Windows.UI.Core.CoreWindow" or "Shell_CompatTrayWnd")
                return true;
            GetWindowThreadProcessId(h, out var pid);
            if ((int)pid == OwnProcessId) return true;
            if (DwmGetWindowAttribute(h, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            // 工具窗（不占任务栏的托盘/覆盖层）不动，避免误伤系统交互组件。
            if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            ShowWindowAsync(h, SW_MINIMIZE);
            return true;
        }, IntPtr.Zero);
    }

    private static string WindowClass(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private void ReleaseTaskbar() => _taskbarRevealWatch.Stop();

    private bool _edgePolicyReady;
    private bool _edgePolicyChanging;
    private bool _exitAfterEdgePolicy;
    private Action? _afterExitCleanup;
    public string? EdgeGesturePolicyError { get; private set; }

    public async Task<bool> SetEdgeGestureEnabledAsync(bool enabled)
    {
        if (_edgePolicyChanging || _exiting) return false;
        _edgePolicyChanging = true;
        var previous = SettingsService.Current.EdgeGestureSummon;
        try
        {
            var result = await SystemEdgeSwipePolicy.SetAsync(enabled);
            EdgeGesturePolicyError = result.Ok ? null : result.Message;
            if (!result.Ok) return false;
            try
            {
                SettingsService.Update(s => s.EdgeGestureSummon = enabled);
            }
            catch (Exception ex)
            {
                SettingsService.Current.EdgeGestureSummon = previous;
                var rollback = await SystemEdgeSwipePolicy.SetAsync(previous);
                EdgeGesturePolicyError = rollback.Ok
                    ? "设置保存失败，已恢复原系统策略，请检查磁盘后重试"
                    : "设置保存失败且系统策略恢复未完成，请重新授权后重试";
                DebugLog.Write($"边缘策略设置保存失败：{ex}");
                return false;
            }
            _edgePolicyReady = enabled;
            SyncEdgeGesture();
            return true;
        }
        finally
        {
            _edgePolicyChanging = false;
            if (_exitAfterEdgePolicy) ExitApp();
        }
    }

    /// <summary>只有系统策略已确认后才启动边缘监听；布局变化仅调整呼出方向，不重复提权。</summary>
    public void SyncEdgeGesture()
    {
        if (_exiting || _edgeGesture == null) return;
        if (!SettingsService.Current.EdgeGestureSummon || !_edgePolicyReady)
        {
            _edgeGesture.Stop();
            return;
        }
        _edgeGesture.SetEdge(CurrentSummonEdge());
        _edgeGesture.Start();
    }

    /// <summary>是否"满幅悬浮"形态（全屏 / 居中停靠）：这两种模式窗口很宽，
    /// 抽屉面板按窗口宽度的固定比例居中显示，避免横向拉满显得过长（见 MainPage.ApplyDrawerWidth）</summary>
    public bool IsWideOverlay =>
        _fullscreen || SettingsService.Current.SidebarSide == DockSide.Center;

    private async void OnFirstEdgePolicyActivation(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstEdgePolicyActivation;
        try
        {
            var enabled = SettingsService.Current.EdgeGestureSummon;
            if (!await SetEdgeGestureEnabledAsync(enabled) && enabled)
            {
                SettingsService.Current.EdgeGestureSummon = false;
                SettingsService.Save(SettingsService.Current);
                SyncEdgeGesture();
                FloatingToastService.Show(EdgeGesturePolicyError ?? "边缘呼出未启用，请重新授权", warning: true);
            }
        }
        catch (Exception ex) { DebugLog.Write($"启动边缘策略检查失败：{ex}"); }
    }

    private SummonEdge CurrentSummonEdge()
    {
        if (SettingsService.Current.StartMenuMode) return StartMenuEdge();
        // 全屏与居中都是"上方悬浮"形态，边缘手势统一走顶边
        if (_fullscreen || SettingsService.Current.SidebarSide == DockSide.Center) return SummonEdge.Top;
        return SettingsService.Current.SidebarSide == DockSide.Right
            ? SummonEdge.Right
            : SummonEdge.Left;
    }

    /// <summary>
    /// 任务栏的当前位置与屏幕矩形。用 Shell 的 ABM_GETTASKBARPOS 查询（任务栏移到哪条边、
    /// 多高、自动隐藏都能拿到），取不到时退回"整屏减工作区"的推断。
    /// </summary>
    private static (SummonEdge edge, int x, int y, int w, int h) TaskbarState()
    {
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
        {
            var edge = abd.uEdge switch
            {
                ABE_LEFT => SummonEdge.Left,
                ABE_TOP => SummonEdge.Top,
                ABE_RIGHT => SummonEdge.Right,
                _ => SummonEdge.Bottom
            };
            return (edge, abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, abd.rc.Bottom - abd.rc.Top);
        }
        var area = Microsoft.UI.Windowing.DisplayArea.Primary;
        var outer = area.OuterBounds;
        var work = area.WorkArea;
        var fallback = SummonEdge.Bottom;
        if (work.Y > outer.Y) fallback = SummonEdge.Top;
        else if (work.X > outer.X) fallback = SummonEdge.Left;
        else if (work.X + work.Width < outer.X + outer.Width) fallback = SummonEdge.Right;
        return (fallback, work.X, work.Y, work.Width, work.Height);
    }

    /// <summary>启用系统时跟随自绘底栏；否则跟随 Windows 任务栏。</summary>
    private static SummonEdge StartMenuEdge() =>
        SettingsService.Current.StartMenuUsesBottomEdge ? SummonEdge.Bottom : TaskbarState().edge;

    /// <summary>
    /// 开始按钮在任务栏长度方向上的归属：靠左（起始端） / 居中 / 靠右（末端）。
    /// 任务栏横排（顶/底）按水平轴判定，竖排（左/右）按垂直轴判定；
    /// 开始菜单模式下用它决定面板的摆放位置——按钮居中则面板居中，按钮靠某一端则面板同端。
    /// </summary>
    private static DockSide StartButtonSide()
    {
        var bar = TaskbarState();
        var btn = KeyboardHookService.StartButtonBounds();
        if (btn.w <= 0 || btn.h <= 0) return DockSide.Center;
        var vertical = bar.edge is SummonEdge.Left or SummonEdge.Right;
        var start = vertical ? bar.y : bar.x;
        var span = vertical ? bar.h : bar.w;
        if (span <= 0) return DockSide.Center;
        var center = vertical ? btn.y + btn.h / 2.0 : btn.x + btn.w / 2.0;
        var rel = (center - start) / span;
        // 带回差判定：已判定为贴端时放宽保持条件，避免数值在阈值附近抖动导致面板左右跳
        var next = _lastStartSide switch
        {
            DockSide.Left => rel < 0.38 ? DockSide.Left : rel > 0.70 ? DockSide.Right : DockSide.Center,
            DockSide.Right => rel > 0.62 ? DockSide.Right : rel < 0.30 ? DockSide.Left : DockSide.Center,
            _ => rel < 0.30 ? DockSide.Left : rel > 0.70 ? DockSide.Right : DockSide.Center
        };
        _lastStartSide = next;
        return next;
    }

    private static DockSide _lastStartSide = DockSide.Center;

    /// <summary>把长度为 <paramref name="len"/> 的面板放在 [<paramref name="start"/>, start+span] 内做起始端/居中/末端对齐。
    /// 靠端对齐时留 <paramref name="margin"/> 的屏幕边距（完全贴边会把 DWM 圆角切在屏幕边缘之外）</summary>
    private static int AlignAxis(int start, int span, int len, DockSide side, int margin) => side switch
    {
        DockSide.Left => start + margin,
        DockSide.Right => start + span - len - margin,
        _ => start + (span - len) / 2
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRect rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABE_LEFT = 0;
    private const uint ABE_TOP = 1;
    private const uint ABE_RIGHT = 2;

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    public MainWindow()
    {
        InitializeComponent();
        Title = "Game Locker";

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // 标题栏/任务栏图标
        SetWindowIcon();

        // 无边框：ExtendsContentIntoTitleBar 让内容占满窗口，
        // 并在 Win32 样式层移除 WS_CAPTION 等（WinUI 的 SetBorderAndTitleBar 会残留白色标题条）
        ExtendsContentIntoTitleBar = true;
        RemoveTitleBarStyle();

        // 背景色 + 窗口透明度
        ApplyBackground();

        // 侧边栏样式：贴屏幕左/右边缘，高度占满工作区
        ApplySidebarLayout();

        // 触摸收起手势（方向与呼出手势相反）：子页=返回上一级，主页=收起启动器
        HookSwipeBack();

        _taskbarRevealWatch.Tick += (_, _) => RefreshRevealedTaskbar();
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            ExitApp();
        };

        Gamepad.ButtonPressed += OnGamepadButton;
        Gamepad.ConnectionChanged += OnGamepadConnection;
        Gamepad.AllReleased += OnGamepadAllReleased;
        Gamepad.SummonTriggered += () => DispatcherQueue.TryEnqueue(() =>
        {
            if ((Root.Content as ISettingsGamepad)?.IsCapturing == true) return;
            SummonFromHotkey();
        });
        Gamepad.SummonCombo = SettingsService.Current.SummonCombo;
        Gamepad.DockSummonCombo = SettingsService.Current.DockSummonCombo;
        Gamepad.DockSummonTriggered += () => DispatcherQueue.TryEnqueue(() =>
        {
            if ((Root.Content as ISettingsGamepad)?.IsCapturing == true) return;
            _ = SummonDockAsync();
        });
        Gamepad.FullscreenCombo = SettingsService.Current.FullscreenCombo;
        Gamepad.FullscreenTriggered += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_exiting) return;
            // 正在捕获组合键时忽略（避免按住新键位 1 秒把全屏切了）
            if ((Root.Content as ISettingsGamepad)?.IsCapturing == true) return;
            // 全屏组合键：隐藏时唤出并进入全屏；显示时开/关切换
            if (!Visible)
            {
                ShowFromTray();
                if (!_fullscreen) ToggleFullscreen();
            }
            else ToggleFullscreen();
        });
        // 右摇杆持续上推 1 秒才呼出抽屉（防误触）；收起仍由页面按键/触屏立即处理
        Gamepad.DrawerSummonHeld += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (Root.Content is MainPage page) page.SetDrawerSummoned(true);
        });
        GamepadRemapService.Init(); // 订阅原始手柄事件，托盘状态也生效
        Gamepad.Start();

        // 键盘呼出键：命中即刻呼出（对比：手柄呼出键需长按 1 秒）
        SyncKeyboardHook();
        KeyboardHook.Triggered += () => DispatcherQueue.TryEnqueue(SummonFromHotkey);
        // 开始菜单模式：Win 键抬起 / 任务栏开始按钮点击 → 切换本启动器显隐
        KeyboardHook.StartMenuToggle += () => DispatcherQueue.TryEnqueue(ToggleStartMenu);
        // 触屏点开始按钮抓不到鼠标消息 → 由前台监听把系统开始菜单关掉后呼出本启动器。
        // 这里只"确保显示"不切换：否则系统菜单反复抢前台会变成 呼出→收起→再呼出
        KeyboardHook.StartMenuOpened += () => DispatcherQueue.TryEnqueue(EnsureStartMenuShown);
        KeyboardHook.Start();

        // 任务栏被移动/改高后自动跟随（面板停靠位置、滑入方向、边缘手势所在边）；
        // 任务栏图标对齐方式改变时只有开始按钮位置变，也一并跟随
        _taskbarSeen = TaskbarState();
        _menuSide = KeyboardHookService.StartButtonBounds().w > 0 ? StartButtonSide() : _menuSide;
        _taskbarWatch.Tick += (_, _) =>
        {
            if (!SettingsService.Current.StartMenuMode) return;
            var now = TaskbarState();
            var side = StartButtonSide();
            // 只按"停靠边/开始按钮归属端"的实质变化重排，忽略任务栏重建时的像素级抖动，
            // 避免面板在居中与靠边之间来回跳
            if (now.edge == _taskbarSeen.edge && side == _menuSide
                && Math.Abs(now.w - _taskbarSeen.w) < 40 && Math.Abs(now.h - _taskbarSeen.h) < 40) return;
            if (_animating) return; // 动画中不打断，下个 tick 再来
            _taskbarSeen = now;
            _menuSide = side;
            ApplySidebarLayout();
        };
        if (SettingsService.Current.StartMenuMode) _taskbarWatch.Start();

        // 平板边缘手势呼出
        _edgeGesture = new EdgeGestureService();
        _edgeGesture.EdgeTouched += () => DispatcherQueue.TryEnqueue(() => KeyboardHook.BeginStartUiBlockWatch());
        _edgeGesture.SummonTriggered += () => DispatcherQueue.TryEnqueue(() =>
        {
            KeyboardHook.BeginStartUiBlockWatch();
            if (!_shown || GetForegroundWindow() != _hwnd)
            {
                DebugLog.Write("任务栏联动: 边缘手势呼出");
                ShowFromTray();
            }
        });
        // 收起手势（呼出的反方向）：走原始输入触摸通道，与页面内容无关，任何页面都生效（现为"直接最小化"）
        _edgeGesture.DismissTriggered += () => DispatcherQueue.TryEnqueue(OnDismissGesture);
        // 向下滑：返回上一级（主页则最小化）
        _edgeGesture.BackTriggered += () => DispatcherQueue.TryEnqueue(OnBackGesture);

        // 游戏内边缘手势：右上/右下角滑出顶栏（5 秒），底部边缘呼出底栏
        // 由 EdgeGestureService 的触摸/鼠标通道在抬手时转给 GameSwipeService 判定
        GameSwipeService.TopBarPeekRequested += () => DispatcherQueue.TryEnqueue(() => StatusBarHost.Peek());
        GameSwipeService.TopBarHideRequested += () => DispatcherQueue.TryEnqueue(StatusBarHost.HideByGesture);
        GameSwipeService.DockSummonRequested += () => DispatcherQueue.TryEnqueue(SummonDockBySwipe);
        GameSwipeService.DockHideRequested += () => DispatcherQueue.TryEnqueue(HideDockBySwipe);

        // 游戏态：进游戏顶栏收起、底栏让路；离开游戏按各自设置恢复
        GamePresence.Changed += inGame =>
        {
            DockHost.SetGameMode(inGame);
            StatusBarHost.SetGameMode(inGame);
        };
        // 首次激活后检查持久化策略；旧配置开启但尚未授权时弹出 UAC。
        if (SettingsService.Current.EdgeGestureSummon && SystemEdgeSwipePolicy.IsAlreadyBlocked())
        {
            _edgePolicyReady = true;
            SyncEdgeGesture();
        }
        else Activated += OnFirstEdgePolicyActivation;

        ForegroundWatcherService.ForegroundWindowChanged += QueueForegroundRefresh;
        ForegroundWatcherService.StartForegroundEvents();
        _foregroundFallback.Tick += (_, _) => RefreshForeground();
        QueueForegroundRefresh();
        _foregroundFallback.Start();

        // 修改器无精确匹配时，弹窗让用户从相似结果里挑一个
        TrainerService.CandidatesReady += OnTrainerCandidates;
        SaveBackupService.ChooseRestoreAsync = ChooseSaveRestoreAsync;
        SaveBackupService.ConfirmRestartAsync = ConfirmSaveRestartAsync;

        // OptiScaler 点击磁贴后，弹窗显示适配性并询问是否注入
        OptiScalerService.InspectionReady += OnOptiInspection;
        // 没检测到游戏进程时，弹出手动拖入 exe 的注入窗口
        OptiScalerService.ManualRequested += OnOptiManualRequested;

        // 触屏：点弹窗底部放大的提示图标 = 执行对应手柄指令（与物理手柄共用同一处理入口）
        TrainerChoiceHintBar.HintTapped += key => HandleTrainerChoice(GamepadHintBar.ToGamepadButtons(key));
        OptiHintBar.HintTapped += key => HandleOpti(GamepadHintBar.ToGamepadButtons(key));
        OptiManualHintBar.HintTapped += key => HandleOptiManual(GamepadHintBar.ToGamepadButtons(key));
        SaveRestoreHintBar.HintTapped += key => _saveRestoreInput?.Invoke(GamepadHintBar.ToGamepadButtons(key));

        // 根页面在首次唤出时创建，登录自启只初始化常驻入口。

        // 鼠标 / 触摸点击音效：挂在根 Frame 上并接收已被子控件处理的事件，
        // 只有真的点中控件（按钮/开关/下拉/磁贴等）才发声，点空白处保持安静。
        Root.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => _pointerSoundVersion = UiSound.PlayVersion), handledEventsToo: true);
        Root.AddHandler(UIElement.TappedEvent,
            new TappedEventHandler(OnAnyTapped), handledEventsToo: true);

        // 启动器打开时覆盖在桌面上：右键先收起，再补发一次桌面右键，
        // 否则桌面右键会被这层窗口吃掉。
        Root.AddHandler(UIElement.RightTappedEvent,
            new RightTappedEventHandler((_, _) =>
            {
                if (!_shown) return;
                HideToTray();
                _ = Task.Delay(120).ContinueWith(_ => DispatcherQueue.TryEnqueue(
                    () => KeySendService.SendMouse(KeySendService.MouseRight)));
            }), handledEventsToo: true);

        if (SettingsService.Current.StartFullscreen && !SettingsService.Current.StartMenuMode && !_fullscreen)
            ToggleFullscreen();

        // 开始菜单模式：失焦后延迟自动收起（避免呼出瞬间的焦点抖动误收）
        _startMenuAutoHide.Tick += (_, _) =>
        {
            _startMenuAutoHide.Stop();
            if (!SettingsService.Current.StartMenuMode || !_shown) return;
            // 系统开始菜单抢到前台时不要收起，而是把焦点抢回来（否则会切成"呼出又关闭"）；
            // 其他程序成为前台（如刚启动的软件）不再自动收起，开始菜单模式下只能手动隐藏
            if (KeyboardHookService.IsStartMenuProcessWindow(GetForegroundWindow()))
                BringToForeground();
        };

        // 启动：先把窗口移到屏外，App.Activate 显示时不在原位（无虚影），Activated 后滑入
        PrepareSummon();
        Activated += (_, e) =>
        {
            if (_exiting) return;
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                MonitorService.SetAppForeground(true);
                ForegroundWatcherService.SetForeground(true);
                _taskbarWatch.Interval = TimeSpan.FromMilliseconds(1000);
                _startMenuAutoHide.Stop();
                // 本窗口成为前台：隐藏边缘条（否则会吞掉本窗口边缘的触摸）
                _edgeGesture?.SetStripVisible(false);
                // 可见时重申手柄路由；失去前台（Deactivated）不动门控——
                // 否则游戏抢回焦点时会把仍然显示在屏幕上的启动器手柄一并关掉
                if (_shown)
                {
                    SetGamepadRouting(true);
                    // 重新获得前台时恢复置顶（启动其他程序时曾临时取消以免盖住对方）
                    if (SettingsService.Current.StartMenuMode) SetAlwaysOnTop(true);
                }
            }
            else
            {
                MonitorService.SetAppForeground(false);
                ForegroundWatcherService.SetForeground(false);
                _taskbarWatch.Interval = TimeSpan.FromMilliseconds(5000);
                // 显示期间保持置顶；失焦不降级，避免与层级维护定时器反复切换 TOPMOST。
                if (SettingsService.Current.StartMenuMode && _shown)
                    _startMenuAutoHide.Start();
                // 任何其他应用成为前台（含被最小化/遮盖）：恢复边缘条，保证任意前台都能触摸呼出
                _edgeGesture?.SetStripVisible(true);
            }
            if (!_summonPending || e.WindowActivationState == WindowActivationState.Deactivated) return;
            _summonPending = false;
            // 滑入结束再抢一次前台：Activate 是在窗口还在屏外时调的，系统有时因此不给键盘焦点
            AnimateMove(_summonStart, _summonTarget, 260, easeOut: true, OnSummonDone);
        };
    }

    private void QueueForegroundRefresh()
    {
        if (_exiting || _foregroundRefreshQueued) return;
        _foregroundRefreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _foregroundRefreshQueued = false;
            RefreshForeground();
        });
    }

    private void RefreshForeground()
    {
        if (_exiting) return;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != _hwnd && foreground != _gameSwipeSource)
            _gameSwipeSource = IntPtr.Zero;
        GamePresence.Refresh();
        ForegroundWatcherService.RefreshFrameGenTarget();
        TrainerService.CaptureForeground();
        HideForMaximizedForeground();
        UpdateFullscreenGame(foreground);
    }

    private bool _saveRestoreOpen;
    private Storyboard? _saveRestoreIn;
    private Action<GamepadButtons>? _saveRestoreInput;

    private Task<bool> ConfirmSaveRestartAsync(string message)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_trainerChoiceOpen || _optiOpen || _optiManualOpen || _saveRestoreOpen)
                    throw new IOException("请先关闭当前弹窗后重试恢复");
                SaveRestoreTitle.Text = "退出恢复重启？";
                SaveRestoreSubtitle.Text = "恢复方式确认";
                SaveRestoreDescription.Text = message;
                SaveRestoreWarning.Text = "将退出游戏、恢复所选存档，再重新打开游戏。不会强制结束游戏。";
                SaveRestoreList.Children.Clear();
                SaveRestoreHintBar.Hints = "A:确认重启恢复|B:取消";
                _saveRestoreInput = buttons =>
                {
                    if (buttons.HasFlag(GamepadButtons.B) || buttons.HasFlag(GamepadButtons.View))
                    {
                        CloseSaveRestore();
                        completion.TrySetResult(false);
                    }
                    else if (buttons.HasFlag(GamepadButtons.A))
                    {
                        CloseSaveRestore();
                        completion.TrySetResult(true);
                    }
                };
                OpenSaveRestore();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new IOException("无法打开退出恢复确认窗口"));
        return completion.Task;
    }

    private Task<SaveRestoreChoice?> ChooseSaveRestoreAsync(SaveRestoreRequest request)
    {
        var completion = new TaskCompletionSource<SaveRestoreChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await ShowSaveRestoreAsync(request)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new IOException("无法打开备份选择窗口"));
        return completion.Task;
    }

    private Task<SaveRestoreChoice?> ShowSaveRestoreAsync(SaveRestoreRequest request)
    {
        if (_trainerChoiceOpen || _optiOpen || _optiManualOpen || _saveRestoreOpen)
            throw new IOException("请先关闭当前弹窗，再选择存档备份");

        var completion = new TaskCompletionSource<SaveRestoreChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SaveRestoreTitle.Text = "恢复存档";
        SaveRestoreSubtitle.Text = $"{request.GameName} · {request.SourceName} · 共 {request.Versions.Count} 个版本";
        SaveRestoreDescription.Text = "选择要恢复的版本，当前存档会自动备份。";
        SaveRestoreWarning.Text = "A 恢复：请先返回游戏主菜单。\nX 同步：A 键恢复无效时使用，会先退出游戏，再覆盖存档并重启游戏。";
        SaveRestoreHintBar.Hints = "DPAD:选择|A:恢复|X:同步|B:取消";
        SaveRestoreList.Children.Clear();
        var cards = new List<Border>();
        var selectedIndex = 0;
        void SelectVersion(int index)
        {
            selectedIndex = index;
            for (var j = 0; j < cards.Count; j++)
            {
                var selected = j == index;
                cards[j].BorderBrush = new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(255, 76, 194, 255) : Windows.UI.Color.FromArgb(31, 255, 255, 255));
                cards[j].BorderThickness = new Thickness(selected ? 2 : 1.5);
                cards[j].Background = new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(38, 76, 194, 255) : Windows.UI.Color.FromArgb(20, 255, 255, 255));
            }
            if (index >= 0 && index < cards.Count) cards[index].StartBringIntoView();
        }
        for (var i = 0; i < request.Versions.Count; i++)
        {
            var version = request.Versions[i];
            var size = version.Size < 1024 ? $"{version.Size} B" : version.Size < 1024 * 1024
                ? $"{version.Size / 1024d:0.##} KB" : $"{version.Size / 1024d / 1024d:0.##} MB";
            var row = new StackPanel { Spacing = 5, Margin = new Thickness(0, 8, 0, 8) };
            row.Children.Add(new TextBlock
            {
                Text = (i == 0 ? "[最新] " : "") + version.FileName,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
            });
            row.Children.Add(new TextBlock
            {
                Text = $"创建时间：{version.Created:yyyy-MM-dd HH:mm:ss}    大小：{size}",
                FontSize = 13, TextWrapping = TextWrapping.Wrap
            });
            row.Children.Add(new TextBlock
            {
                Text = $"修改时间：{version.Modified:yyyy-MM-dd HH:mm:ss}", FontSize = 12, Opacity = 0.7
            });
            var card = new Border { Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 2, 14, 2) };
            var index = i;
            card.Tapped += (_, e) => { SelectVersion(index); e.Handled = true; };
            ToolTipService.SetToolTip(card, version.Location);
            cards.Add(card);
            SaveRestoreList.Children.Add(card);
        }
        SelectVersion(0);
        _saveRestoreInput = buttons =>
        {
            if (buttons.HasFlag(GamepadButtons.B) || buttons.HasFlag(GamepadButtons.View))
            {
                CloseSaveRestore();
                completion.TrySetResult(null);
                return;
            }
            if (cards.Count == 0) return;
            if (buttons.HasFlag(GamepadButtons.DPadUp)) SelectVersion((selectedIndex - 1 + cards.Count) % cards.Count);
            else if (buttons.HasFlag(GamepadButtons.DPadDown)) SelectVersion((selectedIndex + 1) % cards.Count);
            else if (buttons.HasFlag(GamepadButtons.A) || buttons.HasFlag(GamepadButtons.X))
            {
                var choice = new SaveRestoreChoice(request.Versions[selectedIndex], buttons.HasFlag(GamepadButtons.X));
                CloseSaveRestore();
                completion.TrySetResult(choice);
            }
        };
        OpenSaveRestore();
        return completion.Task;
    }

    private void OnSaveRestoreMaskTapped(object sender, TappedRoutedEventArgs e) =>
        _saveRestoreInput?.Invoke(GamepadButtons.B);

    private void OpenSaveRestore()
    {
        _saveRestoreOpen = true;
        if (!_shown) ShowFromTray();
        else BringToForeground();
        SaveRestorePanel.MaxWidth = Math.Max(0, ((FrameworkElement)Content).ActualWidth - 24);
        SaveRestorePanel.MaxHeight = Math.Max(0, ((FrameworkElement)Content).ActualHeight - 24);
        SaveRestoreOverlay.Visibility = Visibility.Visible;
        var sb = new Storyboard();
        void Animate(DependencyObject target, string property, double from, double to, int duration, bool ease = false)
        {
            var animation = new DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(duration),
                EasingFunction = ease ? new CubicEase { EasingMode = EasingMode.EaseOut } : null
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            sb.Children.Add(animation);
        }
        Animate(SaveRestoreMask, "Opacity", 0, 1, 150);
        Animate(SaveRestorePanel, "Opacity", 0, 1, 180);
        Animate(SaveRestorePanelTransform, "ScaleX", 0.94, 1, 220, true);
        Animate(SaveRestorePanelTransform, "ScaleY", 0.94, 1, 220, true);
        Animate(SaveRestorePanelTransform, "TranslateY", 24, 0, 250, true);
        _saveRestoreIn?.Stop();
        _saveRestoreIn = sb;
        sb.Begin();
    }

    private void CloseSaveRestore()
    {
        _saveRestoreIn?.Stop();
        SaveRestoreOverlay.Visibility = Visibility.Collapsed;
        _saveRestoreOpen = false;
        _saveRestoreInput = null;
    }

    // ---- 修改器候选选择遮罩：精确匹配失败时弹出，用户从相似结果里挑一个（手柄可操作）----

    private readonly List<Border> _trainerCards = [];   // 候选卡片（索引与 _trainerCandidates 一一对应）
    private IReadOnlyList<TrainerCandidate> _trainerCandidates = [];
    private int _trainerIndex;
    private bool _trainerChoiceOpen;
    private Storyboard? _trainerChoiceIn;

    /// <summary>
    /// 修改器精确匹配失败时，把相似候选列成遮罩面板让用户自己挑（事件来自后台线程，需 marshal 回 UI 线程）。
    /// 挑中的修改器页地址回传给 TrainerService 继续解析、下载并打开。
    /// </summary>
    private void OnTrainerCandidates(IReadOnlyList<TrainerCandidate> list) =>
        DispatcherQueue.TryEnqueue(() => ShowTrainerChoice(list));

    private void ShowTrainerChoice(IReadOnlyList<TrainerCandidate> list)
    {
        _trainerCandidates = list;
        _trainerIndex = 0;
        _trainerCards.Clear();
        TrainerChoiceList.Children.Clear();
        for (var i = 0; i < list.Count; i++)
        {
            var card = BuildTrainerCard(list[i], i);
            _trainerCards.Add(card);
            TrainerChoiceList.Children.Add(card);
        }
        TrainerChoiceHint.Text = $"没有完全匹配的修改器，请从以下 {list.Count} 条相似结果中选择";
        _trainerChoiceOpen = true;
        TrainerChoiceOverlay.Visibility = Visibility.Visible;
        UpdateTrainerCursor();
        PlayTrainerChoiceIn();
        // 触屏点选要求窗口处于前台：否则第一次点按可能只用于激活窗口
        if (_shown) BringToForeground();
    }

    /// <summary>候选卡片：扳手徽章 + 修改器名 + 右对齐的匹配度百分比（选中态由 UpdateTrainerCursor 统一切换）</summary>
    private Border BuildTrainerCard(TrainerCandidate c, int index)
    {
        var name = new TextBlock
        {
            Text = c.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var percent = new TextBlock
        {
            Text = $"{c.Percent}%",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var badge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2A, 0x4C, 0xC2, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE90F",
                FontSize = 14,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF))
            }
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(percent, 2);
        grid.Children.Add(badge);
        grid.Children.Add(name);
        grid.Children.Add(percent);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(14, 11, 14, 11),
            Child = grid
        };
        card.Tapped += (_, _) => { _trainerIndex = index; UpdateTrainerCursor(); ConfirmTrainerChoice(); };
        return card;
    }

    /// <summary>遮罩入场：遮罩层淡入 + 面板由 0.94 缓动放大到 1（与主页"添加"弹窗一致）</summary>
    private void PlayTrainerChoiceIn()
    {
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140) };
        Storyboard.SetTarget(fade, TrainerChoiceMask);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var scale = new DoubleAnimation
        {
            From = 0.94, To = 1, Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleY = new DoubleAnimation
        {
            From = 0.94, To = 1, Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scale, TrainerChoiceScale);
        Storyboard.SetTargetProperty(scale, "ScaleX");
        Storyboard.SetTarget(scaleY, TrainerChoiceScale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");

        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(scale);
        sb.Children.Add(scaleY);
        _trainerChoiceIn?.Stop();
        _trainerChoiceIn = sb;
        sb.Begin();
    }

    /// <summary>遮罩内手柄按键：上下=切换候选，A=下载所选，B/View/X=取消；其余按键一并吞掉避免误触主页</summary>
    private void HandleTrainerChoice(GamepadButtons b)
    {
        var n = _trainerCards.Count;
        if (b.HasFlag(GamepadButtons.DPadUp))
        {
            if (n > 0) { _trainerIndex = (_trainerIndex - 1 + n) % n; UpdateTrainerCursor(); }
            return;
        }
        if (b.HasFlag(GamepadButtons.DPadDown))
        {
            if (n > 0) { _trainerIndex = (_trainerIndex + 1) % n; UpdateTrainerCursor(); }
            return;
        }
        if (b.HasFlag(GamepadButtons.A)) { ConfirmTrainerChoice(); return; }
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View) || b.HasFlag(GamepadButtons.X))
            CloseTrainerChoice();
    }

    private void OnTrainerChoiceMaskTapped(object sender, TappedRoutedEventArgs e) => CloseTrainerChoice();

    /// <summary>确认当前高亮候选：收起遮罩并交给服务继续解析下载</summary>
    private void ConfirmTrainerChoice()
    {
        if (_trainerIndex < 0 || _trainerIndex >= _trainerCandidates.Count)
        {
            CloseTrainerChoice();
            return;
        }
        var pick = _trainerCandidates[_trainerIndex];
        CloseTrainerChoice();
        TrainerService.DownloadChosen(pick);
    }

    private void CloseTrainerChoice()
    {
        _trainerChoiceOpen = false;
        _trainerChoiceIn?.Stop();
        TrainerChoiceOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>选中项：强调色描边 + 底色高亮，并滚动到可见位置（手柄连续翻页时不会跑出视野）</summary>
    private void UpdateTrainerCursor()
    {
        var accentBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
        var idleBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        var accentBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0x4C, 0xC2, 0xFF));
        var idleBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        for (var i = 0; i < _trainerCards.Count; i++)
        {
            var on = i == _trainerIndex;
            _trainerCards[i].BorderBrush = on ? accentBr : idleBr;
            _trainerCards[i].BorderThickness = new Thickness(on ? 2 : 1.5);
            _trainerCards[i].Background = on ? accentBg : idleBg;
        }
        if (_trainerIndex >= 0 && _trainerIndex < _trainerCards.Count)
            _trainerCards[_trainerIndex].StartBringIntoView();
    }

    // ---- OptiScaler 适配性确认遮罩：显示当前游戏适配性与建议注入方式，询问是否注入（手柄可操作）----

    private LaunchItem? _optiItem;
    private OptiScalerInspection? _optiInspection;
    private bool _optiOpen;
    private Storyboard? _optiIn;

    /// <summary>OptiScaler 适配性检查完成（事件来自后台线程，marshal 回 UI 线程后再弹窗）</summary>
    private void OnOptiInspection(LaunchItem item, OptiScalerInspection insp) =>
        DispatcherQueue.TryEnqueue(() => ShowOptiConfirm(item, insp));

    private void ShowOptiConfirm(LaunchItem item, OptiScalerInspection insp)
    {
        _optiItem = item;
        _optiInspection = insp;
        _optiOpen = true;

        var (accent, emoji) = insp.Status switch
        {
            "✅" => (Windows.UI.Color.FromArgb(0xFF, 0x5C, 0xC4, 0x6A), "✅"),
            "❌" => (Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x7A, 0x6B), "❌"),
            "💥" => (Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB9, 0x00), "💥"),
            _ => (Windows.UI.Color.FromArgb(0xFF, 0x9A, 0xA3, 0xB0), "❔")
        };
        OptiStatusEmoji.Text = emoji;
        OptiStatusEmoji.Foreground = new SolidColorBrush(accent);
        OptiGameName.Text = string.IsNullOrWhiteSpace(insp.GameName) ? insp.ExeName : insp.GameName;
        OptiStatusText.Text = $"适配性：{insp.Status} {insp.StatusText}";
        OptiDllText.Text = insp.SuggestedDll;
        OptiInputsText.Text = insp.Inputs;
        OptiInputsRow.Visibility = string.IsNullOrWhiteSpace(insp.Inputs) ? Visibility.Collapsed : Visibility.Visible;
        OptiNoteText.Text = insp.Note;
        OptiNoteBox.Visibility = string.IsNullOrWhiteSpace(insp.Note) ? Visibility.Collapsed : Visibility.Visible;

        // 已注入过：醒目的覆盖警告 + 提示条改为「覆盖注入」
        OptiWarnBox.Visibility = insp.AlreadyInjected ? Visibility.Visible : Visibility.Collapsed;
        OptiHintBar.Hints = insp.AlreadyInjected ? "A:覆盖注入|B:取消" : "A:注入|B:取消";
        OptiHintText.Text = insp.AlreadyInjected
            ? "继续注入会关闭游戏并覆盖现有 OptiScaler 文件；覆盖前会自动备份，可用「一键还原」恢复。"
            : "选择注入将关闭游戏、写入 OptiScaler 后重新打开游戏；注入前会自动备份被覆盖的同名文件。";

        OptiOverlay.Visibility = Visibility.Visible;
        PlayOptiIn();
        // 触屏点选要求窗口处于前台：否则第一次点按可能只用于激活窗口
        if (_shown) BringToForeground();
    }

    /// <summary>遮罩入场：遮罩层淡入 + 面板由 0.94 缓动放大到 1（与修改器候选弹窗一致）</summary>
    private void PlayOptiIn()
    {
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140) };
        Storyboard.SetTarget(fade, OptiMask);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var scale = new DoubleAnimation
        {
            From = 0.94, To = 1, Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleY = new DoubleAnimation
        {
            From = 0.94, To = 1, Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scale, OptiScale);
        Storyboard.SetTargetProperty(scale, "ScaleX");
        Storyboard.SetTarget(scaleY, OptiScale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");

        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(scale);
        sb.Children.Add(scaleY);
        _optiIn?.Stop();
        _optiIn = sb;
        sb.Begin();
    }

    /// <summary>遮罩内手柄按键：A=注入，B/View/X=取消；其余按键一并吞掉避免误触主页</summary>
    private void HandleOpti(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.A)) { ConfirmOpti(); return; }
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View) || b.HasFlag(GamepadButtons.X))
            CloseOpti();
    }

    private void OnOptiMaskTapped(object sender, TappedRoutedEventArgs e) => CloseOpti();

    /// <summary>确认注入：收起遮罩并交给服务执行（关闭游戏 → 注入 → 重新打开游戏）</summary>
    private void ConfirmOpti()
    {
        var item = _optiItem;
        var insp = _optiInspection;
        CloseOpti();
        if (item is not null && insp is not null) OptiScalerService.InjectConfirmed(item, insp);
    }

    private void CloseOpti()
    {
        _optiOpen = false;
        _optiItem = null;
        _optiInspection = null;
        _optiIn?.Stop();
        OptiOverlay.Visibility = Visibility.Collapsed;
    }

    // ---- 手动注入：没检测到游戏进程时，把游戏 exe 拖进来（自动识别目录 + 适配性检查） ----

    private bool _optiManualOpen;
    private bool _optiManualBusy;
    private LaunchItem? _optiManualItem;
    private OptiScalerInspection? _optiManualInsp;
    private Storyboard? _optiManualIn;

    private void OnOptiManualRequested(LaunchItem item) =>
        DispatcherQueue.TryEnqueue(() => ShowOptiManual(item));

    private void ShowOptiManual(LaunchItem item)
    {
        if (_optiManualOpen) return;
        _optiManualOpen = true;
        _optiManualBusy = false;
        _optiManualItem = item;
        _optiManualInsp = null;

        OptiManualGame.Text = "—";
        OptiManualDir.Text = "—";
        OptiManualStatus.Text = "—";
        OptiManualDll.Text = "—";
        OptiManualInfo.Visibility = Visibility.Collapsed;
        OptiManualDropTitle.Text = "把游戏 exe 拖到这里";
        OptiManualDropSub.Text = "注入目录 = exe 所在目录（游戏无需正在运行）";
        OptiManualHint.Text = "拖入后会检查适配性。A 注入（覆盖前自动备份，可用「一键还原」恢复）。";
        OptiManualHintBar.Hints = "A:注入|B:取消";

        OptiManualOverlay.Visibility = Visibility.Visible;
        PlayOptiManualIn();
        if (_shown) BringToForeground();
    }

    private void PlayOptiManualIn()
    {
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140) };
        Storyboard.SetTarget(fade, OptiManualMask);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var scaleX = new DoubleAnimation
        {
            From = 0.94, To = 1, Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleY = new DoubleAnimation
        {
            From = 0.94, To = 1, Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleX, OptiManualScale);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        Storyboard.SetTarget(scaleY, OptiManualScale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");

        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        _optiManualIn?.Stop();
        _optiManualIn = sb;
        sb.Begin();
    }

    private void OnOptiManualDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);
        e.AcceptedOperation = hasFiles
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        if (e.DragUIOverride is { } ui) ui.Caption = hasFiles ? "释放以检查并注入" : "只接受 .exe 文件";
        SetOptiManualDropHighlight(hasFiles);
    }

    private void OnOptiManualDragLeave(object sender, DragEventArgs e) => SetOptiManualDropHighlight(false);

    /// <summary>拖入高亮：边框换成主题蓝、底色略加深，离开/放下即恢复</summary>
    private void SetOptiManualDropHighlight(bool on)
    {
        if (!_optiManualOpen) return;
        OptiManualDrop.BorderBrush = new SolidColorBrush(on ? Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)
            : Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        OptiManualDrop.Background = new SolidColorBrush(on ? Windows.UI.Color.FromArgb(0x24, 0x4C, 0xC2, 0xFF)
            : Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    }

    private async void OnOptiManualDrop(object sender, DragEventArgs e)
    {
        SetOptiManualDropHighlight(false);
        if (!_optiManualOpen || _optiManualBusy) return;
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<Windows.Storage.StorageFile>()
                .FirstOrDefault(f => f.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                OptiManualDropTitle.Text = "只支持 .exe 文件";
                OptiManualDropSub.Text = "请拖入游戏的可执行文件（exe）";
                return;
            }
            await InspectManualFileAsync(file.Path, file.Name);
        }
        catch (Exception ex)
        {
            OptiManualDropTitle.Text = "读取拖入文件失败";
            OptiManualDropSub.Text = ex.Message;
        }
    }

    /// <summary>拖入 exe 后：读目录、识别游戏名、检查适配性（不依赖游戏是否在运行）</summary>
    private async Task InspectManualFileAsync(string path, string fileName)
    {
        if (_optiManualBusy) return;
        _optiManualBusy = true;
        OptiManualDropTitle.Text = fileName;
        OptiManualDropSub.Text = "正在检查适配性…";
        OptiManualInfo.Visibility = Visibility.Collapsed;
        OptiManualHint.Text = "正在检查适配性…";
        try
        {
            var insp = await OptiScalerService.InspectFileAsync(path);
            if (!_optiManualOpen) return;
            if (insp is null)
            {
                OptiManualDropTitle.Text = "读取失败";
                OptiManualDropSub.Text = "无法读取该 exe，请换一个文件或确认路径可访问";
                OptiManualHint.Text = "拖入后会检查适配性。按 A 注入（自动备份）。";
                return;
            }

            _optiManualInsp = insp;
            OptiManualGame.Text = string.IsNullOrWhiteSpace(insp.GameName) ? insp.ExeName : insp.GameName;
            OptiManualDir.Text = insp.GameDir;
            OptiManualStatus.Text = $"{insp.Status} {insp.StatusText}";
            OptiManualDll.Text = insp.SuggestedDll;
            OptiManualInfo.Visibility = Visibility.Visible;
            OptiManualHint.Text = insp.AlreadyInjected
                ? "该目录已注入过 OptiScaler。继续注入会覆盖现有文件（覆盖前自动备份），按 A 注入。"
                : "已识别目录并检查完成。按 A 注入（覆盖前自动备份，可用「一键还原」恢复）。";
        }
        finally
        {
            _optiManualBusy = false;
        }
    }

    /// <summary>遮罩内手柄按键：A=注入（自动备份），B/View=取消</summary>
    private void HandleOptiManual(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.A)) { ConfirmOptiManual(); return; }
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View)) CloseOptiManual();
    }

    private void ConfirmOptiManual()
    {
        if (_optiManualBusy)
        {
            OptiManualHint.Text = "正在检查适配性，请稍候…";
            return;
        }
        var item = _optiManualItem;
        var insp = _optiManualInsp;
        if (item is null || insp is null)
        {
            OptiManualHint.Text = "请先把游戏 exe 拖入上面的框内";
            return;
        }
        CloseOptiManual();
        OptiScalerService.InjectManual(item, insp);
    }

    private void OnOptiManualMaskTapped(object sender, TappedRoutedEventArgs e) => CloseOptiManual();

    private void CloseOptiManual()
    {
        _optiManualOpen = false;
        _optiManualBusy = false;
        _optiManualItem = null;
        _optiManualInsp = null;
        _optiManualIn?.Stop();
        OptiManualOverlay.Visibility = Visibility.Collapsed;
    }

    // ---- 弹出/隐藏动画：物理移动真实窗口（SetWindowPos 直调 + CompositionTarget.Rendering 帧对齐）。
    // 内容层动画（RenderTransform/Opacity）会因 DWM 缓存帧产生原位"虚影"，只有移动窗口本体才能避免。
    // 卡顿规避：不用 AppWindow.Move（WinUI 包装每帧有 presenter 开销），不用 DispatcherTimer（不与 vsync 对齐）----

    private Windows.Graphics.PointInt32 _summonTarget;
    private Windows.Graphics.PointInt32 _summonStart;
    private Windows.Graphics.PointInt32 _homePos;   // 可见时的停靠位置（滑出后窗口停在屏外，不能用当前位置当目标）
    private bool _summonPending;
    private bool _animating;
    private IntPtr _hwnd;

    // 当前窗口移动动画状态（CompositionTarget.Rendering 每帧推进 + 定时器兜底）
    private EventHandler<object>? _renderHandler;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _animTimer;
    private long _animRenderMs;      // 渲染帧最近一次推进的时刻
    private long _animTimerSeenMs;   // 定时器上次检查时看到的渲染时刻
    private System.Diagnostics.Stopwatch? _animSw;
    private double _animFromX, _animFromY, _animToX, _animToY;
    private bool _animEaseOut;
    private int _animMs;
    private Action? _animDone;

    /// <summary>目标位置对应的屏外起点：开始菜单=系统底栏或 Windows 任务栏所在的一侧，
    /// 左停靠=左侧屏外，右停靠=右侧屏外，全屏/居中=上方屏外</summary>
    private Windows.Graphics.PointInt32 OffscreenPos(
        Windows.Graphics.PointInt32 target, Windows.Graphics.SizeInt32 size)
    {
        if (SettingsService.Current.StartMenuMode)
        {
            var outer = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
            return StartMenuEdge() switch
            {
                SummonEdge.Top => new Windows.Graphics.PointInt32(target.X, outer.Y - size.Height),
                SummonEdge.Left => new Windows.Graphics.PointInt32(outer.X - size.Width, target.Y),
                SummonEdge.Right => new Windows.Graphics.PointInt32(outer.X + outer.Width, target.Y),
                // 任务栏在下方：起点落在屏幕底边之外，向上滑入
                _ => new Windows.Graphics.PointInt32(target.X, outer.Y + outer.Height)
            };
        }
        if (_fullscreen || SettingsService.Current.SidebarSide == Models.DockSide.Center)
            return new Windows.Graphics.PointInt32(target.X, target.Y - size.Height);
        return SettingsService.Current.SidebarSide == Models.DockSide.Right
            ? new Windows.Graphics.PointInt32(target.X + size.Width, target.Y)
            : new Windows.Graphics.PointInt32(target.X - size.Width, target.Y);
    }

    /// <summary>显示窗口前调用：目标位取停靠位 _homePos，把窗口移到其屏外起点，等待 Activated 滑入；
    /// 动画开关关闭时直接摆到目标位、不播放滑入</summary>
    private void PrepareSummon()
    {
        StopMoveAnimation();
        var size = AppWindow.Size;
        _summonTarget = _homePos;
        if (!SettingsService.Current.SummonAnimation)
        {
            MoveWindowTo(_summonTarget);
            _summonPending = false;
            return;
        }
        // 动画期间也保持软件置顶，避免下沿与任务栏交换层级。
        SetAlwaysOnTop(true);
        _summonStart = OffscreenPos(_summonTarget, size);
        MoveWindowTo(_summonStart);
        _summonPending = true;
    }

    /// <summary>切换窗口置顶（开始菜单模式动画期间需要临时降置顶，露出任务栏）</summary>
    private void SetAlwaysOnTop(bool on)
    {
        if (AppWindow.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = on;
    }

    /// <summary>移动窗口：优先直调 SetWindowPos（轻量），hwnd 未就绪时回退 AppWindow.Move</summary>
    private void MoveWindowTo(Windows.Graphics.PointInt32 p)
    {
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, IntPtr.Zero, p.X, p.Y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        else
            AppWindow.Move(p);
    }

    /// <summary>逐帧移动窗口 from→to，由 CompositionTarget.Rendering 驱动（与合成器 vsync 对齐）。
    /// 合成器在没有新帧时不会派发 Rendering（窗口被遮挡/完全在屏外时尤其明显），
    /// 单靠它会让动画偶发卡死，故同时挂一个 16ms 定时器：渲染帧正常推进时定时器让位，
    /// 渲染帧超过一帧没有到来时由定时器接管推进。</summary>
    private void AnimateMove(Windows.Graphics.PointInt32 from, Windows.Graphics.PointInt32 to,
        int ms, bool easeOut, Action? done)
    {
        StopMoveAnimation();
        _animating = true;
        _animFromX = from.X; _animFromY = from.Y;
        _animToX = to.X; _animToY = to.Y;
        _animMs = Math.Max(1, ms); _animEaseOut = easeOut; _animDone = done;
        _animRenderMs = 0;
        _animTimerSeenMs = 0;
        _animSw = System.Diagnostics.Stopwatch.StartNew();
        _renderHandler = OnRenderFrame;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _renderHandler;
        // 兜底定时器：与渲染帧同样的时间基准，重复驱动也不会跳位
        _animTimer ??= DispatcherQueue.CreateTimer();
        _animTimer.Interval = TimeSpan.FromMilliseconds(16);
        _animTimer.IsRepeating = true;
        _animTimer.Tick += OnAnimTimerTick;
        _animTimer.Start();
    }

    private void OnRenderFrame(object? sender, object e)
    {
        if (_animSw == null) return;
        _animRenderMs = _animSw.ElapsedMilliseconds;
        AdvanceMoveFrame();
    }

    /// <summary>兜底：渲染帧正常到来时让位；超过一帧没有渲染帧就自己推进</summary>
    private void OnAnimTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_animSw == null) return;
        if (_animRenderMs != _animTimerSeenMs)
        {
            _animTimerSeenMs = _animRenderMs;   // 合成器在正常出帧，交给 Rendering
            return;
        }
        AdvanceMoveFrame();
    }

    /// <summary>按已过去的时间推进一帧；到点后落位并回调完成</summary>
    private void AdvanceMoveFrame()
    {
        if (_animSw == null) return;
        double t = Math.Clamp(_animSw.ElapsedMilliseconds / (double)_animMs, 0, 1);
        double k = _animEaseOut ? 1 - Math.Pow(1 - t, 3) : t * t * t;
        var pos = new Windows.Graphics.PointInt32(
            (int)Math.Round(_animFromX + (_animToX - _animFromX) * k),
            (int)Math.Round(_animFromY + (_animToY - _animFromY) * k));
        MoveWindowTo(t >= 1 ? new Windows.Graphics.PointInt32((int)_animToX, (int)_animToY) : pos);
        if (t >= 1)
        {
            var done = _animDone;
            StopMoveAnimation();
            done?.Invoke();
        }
    }

    private void StopMoveAnimation()
    {
        if (_renderHandler != null)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _renderHandler;
            _renderHandler = null;
        }
        if (_animTimer != null)
        {
            _animTimer.Stop();
            _animTimer.Tick -= OnAnimTimerTick;
        }
        _animSw = null;
        _animDone = null;
        _animating = false;
    }

    /// <summary>
    /// 手柄路由门控：只有窗口处于"已呼出显示"状态时才把按键派发给页面。
    /// 隐藏到托盘后一律不派发——GamepadService 仍在轮询（呼出组合键、连接状态依赖轮询），
    /// 但不派发按键/方向/摇杆事件，避免刚启动游戏后误触按键把 Y（关闭程序）、X（添加）、
    /// B（隐藏）打到已经看不见的界面上。开关只跟着显示状态走，不受前台焦点影响。
    /// </summary>
    private void SetGamepadRouting(bool on)
    {
        _routing = on;
        Gamepad.RoutingEnabled = on;
    }

    /// <summary>托盘/热键唤起</summary>
    public void ShowFromTray()
    {
        if (_exiting) return;
        if (_shown && LosslessScalingService.ActiveOptiSession is not null
            && IsWindowVisibleForLaunch(_hwnd) && !IsIconic(_hwnd))
        {
            if (!_animating && !_summonPending) BringToForeground();
            return;
        }
        // 开始菜单模式：底栏此时已创建，重新按底栏上沿收一次高度（启动时底栏还没建）
        GamePresence.Refresh();
        if (SettingsService.Current.StartMenuMode) ApplySidebarLayout();
        // 回前台：先退出效率模式，避免滑入动画与手柄响应被系统降频
        Interlocked.Exchange(ref _pageSummonedAt, Environment.TickCount64);
        DockPopupWindow.HideCurrent();
        PowerModeService.SetEfficiencyMode(false);
        _shown = true;
        if (Root.Content is null) Root.Navigate(typeof(MainPage));
        (Root.Content as MainPage)?.CancelPendingLaunch();
        (Root.Content as MainPage)?.SetBackground(false);
        _edgeGesture?.SetStripVisible(false);
        PrepareSummon();
        Activate();
        // 所有呼出入口共用，避免系统开始菜单回调先显示启动器后，边缘事件被显隐门控跳过。
        RevealTaskbar();
        // 所有显示模式均从呼出开始保持在任务栏上方。
        BringToForeground();
        // 窗口已处于激活状态时 Activated 不会再触发，这里兜底启动滑入动画
        if (_summonPending)
        {
            _summonPending = false;
            // 滑入结束再抢一次前台：Activate 是在窗口还在屏外时调的，系统有时因此不给键盘焦点
            AnimateMove(_summonStart, _summonTarget, 260, easeOut: true, OnSummonDone);
        }
        else if (SettingsService.Current.StartMenuMode) BringToForeground(); // 无动画时没有完成回调，直接抢前台
        (Root.Content as MainPage)?.FocusGrid();
        // 已呼出显示：恢复手柄对启动器的操作。只跟显示状态走，不依赖抢前台是否成功
        // （抢前台失败时窗口照样显示，若不在这里打开门控，手柄就会永久失效）
        SetGamepadRouting(true);
    }

    /// <summary>滑入动画结束：开始菜单模式恢复置顶（动画期间临时取消），再抢一次前台焦点</summary>
    private void OnSummonDone()
    {
        if (!_shown) return;
        if (SettingsService.Current.StartMenuMode) SetAlwaysOnTop(true);
        BringToForeground();
    }

    /// <summary>按当前设置同步键盘呼出键（控制页捕获保存后调用）</summary>
    public void SyncKeyboardHook()
    {
        KeyboardHook.Key = SettingsService.Current.KeyboardSummonKey;
        KeyboardHook.Mods = SettingsService.Current.KeyboardSummonMods;
        KeyboardHook.StartMenuMode = SettingsService.Current.StartMenuMode;
    }

    /// <summary>
    /// 开始菜单模式的呼出/收起切换（Win 键、任务栏开始按钮点击、系统开始菜单被拦截共用）。
    /// 按窗口当前显隐状态决定方向，并做 400ms 去抖：同一次操作可能同时经鼠标钩子与前台监听到达。
    /// </summary>
    public void ToggleStartMenu()
    {
        if (_exiting || !KeyboardHook.StartMenuMode) return;
        var now = Environment.TickCount64;
        if (now - _lastStartMenuToggle < 400) return;
        _lastStartMenuToggle = now;
        DebugLog.Write($"开始菜单: 切换 shown={_shown}");
        KeyboardHook.BeginStartUiBlockWatch();
        if (_shown) HideToTray();
        else ShowFromTray();
    }

    /// <summary>
    /// 系统开始菜单被拦截后：确保本启动器处于显示状态（不切换），
    /// 并在系统菜单抢前台后补抢一次焦点，防止手柄按键落到系统开始菜单上。
    /// </summary>
    private void EnsureStartMenuShown()
    {
        if (_exiting || !KeyboardHook.StartMenuMode) return;
        _startMenuAutoHide.Stop();
        if (!_shown) ShowFromTray();
        else BringToForeground();
        _ = ReclaimForegroundAsync();
    }

    private async Task ReclaimForegroundAsync()
    {
        try
        {
            await Task.Delay(150);
            if (!_exiting && KeyboardHook.StartMenuMode && _shown &&
                KeyboardHookService.IsStartMenuProcessWindow(GetForegroundWindow()))
                BringToForeground();
            await Task.Delay(250);
            if (!_exiting && KeyboardHook.StartMenuMode && _shown &&
                KeyboardHookService.IsStartMenuProcessWindow(GetForegroundWindow()))
                BringToForeground();
        }
        catch { }
    }

    /// <summary>
    /// 开始菜单模式开关变更后调用：需要时退出全屏、按新模式重排布局，并同步键盘钩子的接管状态。
    /// </summary>
    public void ApplyStartMenuMode()
    {
        if (SettingsService.Current.StartMenuMode)
        {
            _taskbarSeen = TaskbarState();
            _taskbarWatch.Start();
        }
        else _taskbarWatch.Stop();
        if (SettingsService.Current.StartMenuMode && _fullscreen)
        {
            ToggleFullscreen(); // 内部会再调 ApplySidebarLayout
            SyncKeyboardHook();
            return;
        }
        _startMenuAutoHide.Stop();
        _menuSide = StartButtonSide();
        ApplySidebarLayout();
        SyncKeyboardHook();
    }

    /// <summary>键盘呼出键命中（开关）：窗口未显示或被其他窗口遮盖时置顶呼出；已显示且在前台时收起。
    /// 显示状态用 _shown 判定——AppWindow.Hide() 后 Window.Visible 不一定同步翻转。</summary>
    private void SummonFromHotkey()
    {
        if (!_shown || GetForegroundWindow() != _hwnd) ShowFromTray();
        else HideToTray();
    }

    /// <summary>底栏呼出组合键：先收起启动器（否则底栏被它挡住），再把底栏亮出来。
    /// 底栏没启用时给一次提示，不偷偷把它打开。</summary>
    private async Task SummonDockAsync()
    {
        if (_exiting) return;
        DebugLog.Write($"[底栏] 呼出组合键命中：SystemEnabled={SettingsService.Current.SystemEnabled} " +
                       $"已显示={DockHost.IsVisible} 启动器={(_shown ? "显示中" : "已收起")}");
        if (!SettingsService.Current.SystemEnabled)
        {
            FloatingToastService.Show("底栏未启用：设置 → 系统 → 打开「启用系统」", warning: true);
            return;
        }
        if (_shown) HideToTray();
        try { await DockHost.SummonAsync(); }
        catch (Exception ex) { DebugLog.Write("[底栏] 手柄呼出失败：" + ex.Message); }
    }

    /// <summary>游戏内底部边缘上滑：呼出底栏；Windows 开始菜单模式下同时把启动器叫出来。</summary>
    private async void SummonDockBySwipe()
    {
        if (_exiting || !SettingsService.Current.SystemEnabled || !GamePresence.InGame) return;
        DebugLog.Write($"[底栏] 手势呼出：StartMenuMode={SettingsService.Current.StartMenuMode} " +
                       $"启动器={(_shown ? "显示中" : "已收起")}");
        var source = GetForegroundWindow();
        try
        {
            await DockHost.SummonAsync();
            if (!_exiting && !_shown && SettingsService.Current.StartMenuUsesBottomEdge
                && GamePresence.InGame && source != IntPtr.Zero && source == GetForegroundWindow())
            {
                _gameSwipeSource = source;
                ShowFromTray();
            }
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 手势呼出失败：" + ex.Message); }
    }

    private void HideDockBySwipe()
    {
        if (!GamePresence.InGame) return;
        DockHost.HideForGame();
        if (_gameSwipeSource != IntPtr.Zero && _shown) HideToTray();
    }

    /// <summary>Y 关闭目标程序后调用：等待目标窗口销毁、系统焦点转移完成，再把启动器重新置顶聚焦（保持前台不最小化）</summary>
    public async void KeepForeground()
    {
        await Task.Delay(250);
        BringToForeground();
    }

    /// <summary>
    /// 强制窗口置顶并聚焦：Activate() 不保证 Z 序，热键/手柄呼出时有几率被其他窗口遮盖。
    /// 窗口常驻 TOPMOST（要盖住同样置顶的任务栏），所以这里只重申一次 TOPMOST
    /// 把它提到置顶带最上，不能再回落到 NOTOPMOST（否则任务栏又会压住窗口下沿），再抢前台焦点。
    /// SetForegroundWindow 在"发起进程本身不是前台进程"时会被系统拒绝（手柄轮询呼出、托盘/边缘
    /// 滑动呼出都是后台进程发起），表现为窗口显示出来了但没有键盘焦点。这里临时把本线程挂到当前
    /// 前台线程的输入队列上，借对方的前台权限把焦点抢过来，随后立刻解除挂接。
    /// </summary>
    private void BringToForeground()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (GetForegroundWindow() == _hwnd)
        {
            RefreshRevealedTaskbar();
            return;
        }

        var cur = GetCurrentThreadId();
        var fgOwner = GetForegroundWindow();
        var fgThread = fgOwner == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fgOwner, out _);
        // 与前台的线程挂接：只有挂上之后 SetForegroundWindow 才会被系统放行
        var attached = fgThread != 0 && fgThread != cur && AttachThreadInput(fgThread, cur, true);
        SetForegroundWindow(_hwnd);
        SetActiveWindow(_hwnd);
        BringWindowToTop(_hwnd);
        if (attached) AttachThreadInput(fgThread, cur, false);
        RefreshRevealedTaskbar();
    }

    internal void ExitForUpdate(Action startInstaller)
    {
        if (_exiting || _afterExitCleanup is not null) throw new InvalidOperationException("软件正在退出");
        _afterExitCleanup = startInstaller;
        ExitApp();
    }

    /// <summary>处理托盘退出和系统关窗。</summary>
    public async void ExitApp()
    {
        if (_exiting) return;
        if (_edgePolicyChanging)
        {
            _exitAfterEdgePolicy = true;
            return;
        }

        _exiting = true;
        var app = (App)Microsoft.UI.Xaml.Application.Current;
        app.BeginShutdown();
        DownloadModeService.BeginShutdown();
        try
        {
            Root.IsEnabled = false;
            _shown = false;
            _summonPending = false;
            RunExitStep("停止滑动动画", StopMoveAnimation);
            RunExitStep("停止首页任务", () => (Root.Content as MainPage)?.PrepareForExit());
            _startMenuAutoHide.Stop();
            _taskbarWatch.Stop();
            _foregroundFallback.Stop();
            ForegroundWatcherService.ForegroundWindowChanged -= QueueForegroundRefresh;
            RunExitStep("关闭前台事件", ForegroundWatcherService.StopForegroundEvents);
            _bTimer?.Stop();
            RunExitStep("恢复任务栏", () =>
            {
                ReleaseTaskbar();
                EndDownloadOverlay();
            });
            RunExitStep("关闭顶栏", StatusBarHost.Shutdown);
            RunExitStep("关闭底栏", DockHost.Shutdown);
            RunExitStep("恢复键盘", () => SystemKeyboardWatcher.Apply(false));
            RunExitStep("关闭托盘", app.DisposeTray);
            RunExitStep("关闭键盘钩子", KeyboardHook.Dispose);
            RunExitStep("关闭手柄", Gamepad.Dispose);
            RunExitStep("关闭边缘手势", () => _edgeGesture?.Dispose());
            RunExitStep("关闭屏幕翻译", ScreenTranslateService.Stop);
            RunExitStep("停止文件传输", FileTransferService.Stop);
            RunExitStep("关闭音效", () => { UiSound.Muted = true; UiSound.Close(); });

            var proxy = RunExitStepAsync("停止代理", ProxyService.ShutdownAsync);
            var capture = RunExitStepAsync("停止捕获助手", LosslessScalingService.StopOptiScalerForExitAsync);
            var monitor = RunExitStepAsync("停止监控", MonitorService.ShutdownAsync);
            await RunExitStepAsync("停止供电监听", app.StopBackgroundWorkAsync);
            await RunExitStepAsync("恢复下载模式", () => Task.Run(DownloadModeService.Exit));
            await Task.WhenAll(proxy, capture, monitor,
                RunExitStepAsync("关闭功耗助手", CpuPowerService.ShutdownAsync),
                RunExitStepAsync("恢复游戏 CPU 核心设置", () => Task.Run(GameCpuService.Shutdown)),
                RunExitStepAsync("恢复冻结进程", () => Task.Run(ForegroundWatcherService.ResumeFrozenOnExit)));
        }
        catch (Exception ex) { DebugLog.Write($"[退出] {ex}"); }
        finally
        {
            RunExitStep("恢复顶部任务栏", TopTaskbarMode.ForceRestore);
            if (_afterExitCleanup is { } startInstaller) RunExitStep("启动更新安装器", startInstaller);
            _allowClose = true;
            try { Close(); }
            finally { app.Exit(); }
        }
    }

    private static void RunExitStep(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { DebugLog.Write($"[退出] {name}失败：{ex.Message}"); }
    }

    private static async Task RunExitStepAsync(string name, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { DebugLog.Write($"[退出] {name}失败：{ex.Message}"); }
    }

    public async Task HideForLaunchAsync(CancellationToken token)
    {
        HideToTray();
        while (IsWindowVisibleForLaunch(_hwnd))
        {
            token.ThrowIfCancellationRequested();
            if (_shown) throw new OperationCanceledException("启动器已重新呼出", token);
            await Task.Delay(16, token);
        }
        if (_shown) throw new OperationCanceledException("启动器已重新呼出", token);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisibleForLaunch(IntPtr hwnd);

    /// <summary>隐藏到托盘：动画开启时先播放反向滑出（真实窗口移出屏外）再隐藏；关闭时直接隐藏</summary>
    public void HideToTray() => HideToTray(animate: true);

    internal void HideToTray(bool animate)
    {
        if (_exiting) return;
        // 立刻停手柄路由：一旦决定隐藏，滑出动画期间就不该再响应手柄
        // （否则刚启动游戏后晃动/误触按键会把启动器上的操作（如 Y 关闭程序）打到已经看不见的界面上）
        _shown = false;
        _gameSwipeSource = IntPtr.Zero;
        _summonPending = false;
        (Root.Content as MainPage)?.CloseSearch();
        var wasAnimating = _animating;
        StopMoveAnimation();
        (Root.Content as MainPage)?.SetBackground(true);
        SetGamepadRouting(false);
        if (!animate || !SettingsService.Current.SummonAnimation || wasAnimating)
        {
            AppWindow.Hide();
            _captureOrder.Detach(_hwnd);
            PowerModeService.SetEfficiencyMode(true); // 完全隐藏后进入效率模式，降低常驻功耗
            ReleaseTaskbar();
            _edgeGesture?.SetStripVisible(true); // 窗口隐藏后恢复边缘条，任意前台都能触摸呼出
            return;
        }
        var target = AppWindow.Position;
        var size = AppWindow.Size;
        var off = OffscreenPos(target, size);
        // 滑出期间同样保持层级，完全隐藏后停止维护。
        AnimateMove(target, off, 260, easeOut: false, () =>
        {
            if (_shown) return;
            AppWindow.Hide();
            _captureOrder.Detach(_hwnd);
            PowerModeService.SetEfficiencyMode(true); // 完全隐藏后进入效率模式，降低常驻功耗
            ReleaseTaskbar();
            _edgeGesture?.SetStripVisible(true);
        });
    }

    /// <summary>底栏窗口的物理上沿；底栏不可见时返回 0（调用方回退到工作区边缘）。</summary>
    private static int DockTopPhysical()
    {
        // 必须带窗口类，否则部分 WinUI 顶层窗用 FindWindow(null, title) 会返回 0
        var hwnd = FindWindow("WinUIDesktopWin32WindowClass", "Game Locker Dock");
        if (hwnd == IntPtr.Zero) return 0;
        return GetWindowRect(hwnd, out var rect) ? rect.Top : 0;
    }

    /// <summary>
    /// 窗口布局：左/右停靠=贴屏幕左右边缘、宽度约 30%、高度铺满整屏（含任务栏区域，盖住任务栏）；
    /// 居中=水平居中、上下留边距（四周都不贴屏幕边，也不盖任务栏），宽度按设置值放大换算。
    /// 设置页修改停靠方式后立即调用本方法生效；全屏模式下不干预。
    /// </summary>
    public void ApplySidebarLayout()
    {
        if (AppWindow == null || _fullscreen) return;
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.IsMaximizable = false;
            presenter.Restore();
        }

        // OuterBounds 是整屏（含任务栏占用的下沿）；WorkArea 会少掉任务栏那条，
        // 导致窗口上下铺不满且下沿露出任务栏。
        var bounds = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
        var area = Microsoft.UI.Windowing.DisplayArea.Primary;
        var pct = Math.Clamp(SettingsService.Current.SidebarWidthPercent, 0.38, 0.5);

        int x, y, width, height;
        if (SettingsService.Current.StartMenuMode)
        {
            // 贴住和呼出手势相同的边缘，对面留边距。宽度沿用居中模式的倍率（38% ≈ 70% 屏宽）。
            var menuPct = Math.Min(pct / 0.38 * 0.70, 0.96);
            var work = area.WorkArea;
            const int gap = 12; // 与任务栏之间的缝
            const int sideGap = 16; // 靠边对齐时与屏幕左右边缘的留白
            var inset = Math.Max(24, (int)(work.Height * 0.06)); // 与任务栏相对一侧的留边
            width = Math.Min(Math.Max(360, (int)(work.Width * menuPct)), work.Width - sideGap * 2);
            // 面板位置跟随开始按钮在任务栏长轴上的位置：居中→居中，靠某端→同端（留少量屏幕边距）
            var side = StartButtonSide();
            var alignX = AlignAxis(work.X, work.Width, width, side, sideGap);
            switch (StartMenuEdge())
            {
                case SummonEdge.Top: // 任务栏在上方：面板贴顶，下方留边
                    height = work.Height - inset - gap;
                    x = alignX;
                    y = work.Y + gap;
                    break;
                case SummonEdge.Left: // 任务栏在左侧：面板贴左，沿垂直轴跟随开始按钮（按钮靠上→面板居上）
                    height = work.Height - inset - gap;
                    x = work.X + gap;
                    y = side switch
                    {
                        DockSide.Left => work.Y + gap,   // 与按钮同端：只留缝
                        DockSide.Right => work.Y + inset, // 反端留大边
                        _ => AlignAxis(work.Y, work.Height, height, side, sideGap)
                    };
                    break;
                case SummonEdge.Right: // 任务栏在右侧：面板贴右，沿垂直轴跟随开始按钮
                    height = work.Height - inset - gap;
                    x = work.X + work.Width - width - gap;
                    y = side switch
                    {
                        DockSide.Left => work.Y + gap,
                        DockSide.Right => work.Y + inset,
                        _ => AlignAxis(work.Y, work.Height, height, side, sideGap)
                    };
                    break;
                default: // 任务栏在下方：面板贴底（本机常见形态），上方留边
                    height = work.Height - inset - gap;
                    x = alignX;
                    y = work.Y + inset;
                    break;
            }

            // 开始菜单面板的下沿要让开底栏：留 10px 小缝，不盖住底栏
            var dockTop = DockTopPhysical();
            if (dockTop > 0)
            {
                var limit = dockTop - 20;
                if (y + height > limit) height = Math.Max(280, limit - y);
            }
            DebugLog.Write($"[开始菜单] 布局 dockTop={dockTop} y={y} h={height} bottom={y + height}");

            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
        else if (SettingsService.Current.SidebarSide == Models.DockSide.Center)
        {
            // 居中模式宽度换算：设置里的最小值 38% 对应实际 70% 屏宽（居中比贴边需要占更大一块），
            // 按同一倍率往上放大；上限 96% 保证左右始终留边
            var centerPct = Math.Min(pct / 0.38 * 0.70, 0.96);
            width = Math.Max(360, (int)(bounds.Width * centerPct));

            // 水平居中；窗口高度仍为屏高的 88%（上下各留 6% 的量），但整体比正居中往上靠一点：
            // 上边距取屏高的 3%（至少 24px），多出来的空间留给下边距
            var topInset = Math.Max(24, (int)(bounds.Height * 0.03));
            height = bounds.Height - Math.Max(48, (int)(bounds.Height * 0.06)) * 2;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            x = bounds.X + (bounds.Width - width) / 2;
            y = bounds.Y + topInset;
        }
        else
        {
            width = Math.Max(360, (int)(bounds.Width * pct));
            height = bounds.Height;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            x = SettingsService.Current.SidebarSide == Models.DockSide.Right
                ? bounds.X + bounds.Width - width
                : bounds.X;
            y = bounds.Y;
        }
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        _homePos = new Windows.Graphics.PointInt32(x, y);

        // 任务栏自身是 TOPMOST，窗口不置顶的话任务栏会压在窗口下沿之上（盖不住）。
        // 放在 Resize/Move 之后设置，避免中途被窗口操作重置。
        if (presenter != null) presenter.IsAlwaysOnTop = true;

        // presenter 状态操作可能恢复窗口样式，重新移除标题栏
        RemoveTitleBarStyle();

        // 居中模式是四边不贴屏的浮动小块 → 圆角；贴边/全屏要方形（否则屏幕边缘会露出圆角缺口）
        ApplyWindowCorners();

        // 停靠方式可能变化，重新定位边缘手势窄条
        SyncEdgeGesture();

        // 形态（贴边/居中窄或宽）变化后，抽屉宽度上限需重算：全屏/居中按窗口宽度比例居中，贴边铺满
        if (Root.Content is MainPage mp)
        {
            mp.ApplyDrawerWidth();
            mp.UpdateHomeHintLayout();
        }
    }

    /// <summary>
    /// 窗口圆角：用系统 DWM 圆角（仅 Win11 支持，Win10 上该属性无效、保持方形）。
    /// 开始菜单模式的悬浮面板与居中模式都圆角；贴边与全屏必须方形。
    /// </summary>
    private void ApplyWindowCorners()
    {
        if (_hwnd == IntPtr.Zero) return;
        var round = !_fullscreen && (SettingsService.Current.StartMenuMode
            || SettingsService.Current.SidebarSide == DockSide.Center);
        var pref = round ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>
    /// Win32 样式层彻底移除标题栏：WinUI 的 SetBorderAndTitleBar 在 unpackaged 下
    /// 会残留一条系统默认白色的标题条，只能移除 WS_CAPTION 等样式并重算非客户区。
    /// 同时置 WS_EX_TOOLWINDOW：启动器不出现在任务栏与 Alt+Tab（常驻入口只保留托盘图标）。
    /// </summary>
    private void RemoveTitleBarStyle()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOOLWINDOW));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 背景：用户背景色（BackgroundRect）+ 可选玻璃（Acrylic 材质）+ 整窗透明度（分层窗口 LWA_ALPHA）。
    /// 玻璃开启时背景色改为半透明叠加，透出磨砂材质；关闭时用不透明纯色。
    /// WinUI 的 Window 没有 Opacity 属性，整窗半透明只能经 HWND 扩展样式实现（含文字）。
    /// 玻璃用 <see cref="AlwaysActiveAcrylicBackdrop"/> 而非系统默认的 DesktopAcrylicBackdrop：
    /// 后者跟随窗口激活状态，窗口失活时磨砂会被系统降级为纯色（表现为"要点击窗口玻璃才出现"）。
    /// </summary>
    private SystemBackdrop? _acrylic;

    public void ApplyBackground()
    {
        var s = SettingsService.Current;
        var content = (Microsoft.UI.Xaml.Controls.Panel)Content;
        content.Background = new SolidColorBrush(Colors.Transparent);

        var color = ParseColor(s.BackgroundColor);

        if (s.GlassEffect)
        {
            _acrylic ??= new AlwaysActiveAcrylicBackdrop();
            SystemBackdrop = _acrylic;
            // 低透明度叠色：只轻微染色，突出 Acrylic 磨砂透出效果
            BackgroundRect.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, color.R, color.G, color.B));
        }
        else
        {
            SystemBackdrop = null;
            BackgroundRect.Fill = new SolidColorBrush(color);
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return;

        var opacity = Math.Clamp(s.WindowOpacity, 0.3, 1.0);
        if (opacity < 1.0)
        {
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_LAYERED));
            SetLayeredWindowAttributes(hwnd, 0, (byte)(opacity * 255), LWA_ALPHA);
        }
        else
        {
            // 完全不透明：移除分层样式，避免残留
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex & ~WS_EX_LAYERED));
        }
    }

    /// <summary>解析 #RRGGBB 十六进制颜色，非法值回退 #202020</summary>
    private static Windows.UI.Color ParseColor(string hex)
    {
        try
        {
            if (hex.Length == 7 && hex[0] == '#')
            {
                byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                return Windows.UI.Color.FromArgb(0xFF, r, g, b);
            }
        }
        catch { /* 回退默认色 */ }
        return Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20);
    }

    /// <summary>返回主页（从任意子页逐级回退到 MainPage）</summary>
    public void ShowMainPage()
    {
        while (Root.Content is not MainPage && Root.CanGoBack)
            Root.GoBack();
        if (Root.Content is not MainPage)
            Root.Navigate(typeof(MainPage));
    }

    /// <summary>设置子页 → 返回设置主页</summary>
    public void ShowSettingsHub()
    {
        if (Root.Content is ISettingsGamepad and not SettingsPage && Root.CanGoBack)
        {
            Root.GoBack();
            if (SettingsService.Current.UiSoundEnabled) UiSound.Play(UiSoundKind.Back);
        }
    }

    /// <summary>设置主页 → 分类子页</summary>
    public void NavigateSettings(Type page) => Root.Navigate(page);

    /// <summary>主页选择遮罩确认后：进入添加快捷项页</summary>
    public void RootNavigateAddItem() => Root.Navigate(typeof(AddItemPage));

    /// <summary>主页双击磁贴：进入编辑快捷项页（等同 R3）</summary>
    public void NavigateEditItem(Models.LaunchItem item) => Root.Navigate(typeof(AddItemPage), item);

    /// <summary>底栏改了"是否在主页显示"之类只影响磁贴排布的设置时，让当前主页立即重建</summary>
    public void ReloadHome() => (Root.Content as MainPage)?.Reload();

    /// <summary>主页选择遮罩确认后：进入小组件页</summary>
    public void RootNavigateWidget() => Root.Navigate(typeof(WidgetPage));

    internal async void ReturnToDock(object page)
    {
        if (_exiting || !IsCurrentPage(page)) return;
        if (Root.CanGoBack) Root.GoBack();
        else ShowMainPage();
        HideToTray();
        try { await DockHost.SummonAsync(); }
        catch (Exception ex) { DebugLog.Write("[底栏] 返回底栏失败：" + ex.Message); }
    }

    /// <summary>底栏性能组件：呼出启动器，并在主页里按 Y 打开完整性能面板。</summary>
    public void OpenPerformanceFromDock()
    {
        ShowFromTray();
        if (Root.Content is MainPage page)
        {
            page.OpenPerformanceFromDock();
            return;
        }
        // 当前停在设置/编辑页：回到主页后再开面板
        void OnNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            Root.Navigated -= OnNavigated;
            if (Root.Content is MainPage home) home.OpenPerformanceFromDock();
        }
        Root.Navigated += OnNavigated;
        Root.Navigate(typeof(MainPage));
    }

    /// <summary>底栏小组件图标：转交给主页，复用磁贴的触发语义（A/点击对应的动作）。</summary>
    public void ActivateDockWidget(Models.LaunchItem item)
    {
        MainPage.BeginDockAction();
        if ((Root.Content as MainPage ?? MainPage.Current) is { } page)
        {
            page.ActivateWidgetFromDock(item);
            return;
        }
        Services.DebugLog.Write("[底栏] 小组件触发失败：当前不在主页");
    }

    /// <summary>主页当前页号（0=主主页；副页从 1 开始）：主页切页时更新，新建磁贴据此归属当前页</summary>
    public int CurrentHomePage { get; set; }

    /// <summary>抽屉内按 X 添加时挂起的一次性请求：(抽屉组件 Target, 抽屉下标)。
    /// 在小组件页/编辑页新建磁贴时消费——新磁贴直接放进该抽屉，而不是加在主页顶层；消费后由消费方置空。</summary>
    public (string Target, int Tab)? PendingDrawerInsert { get; set; }

    /// <summary>设置页返回：回到进入设置前的页面（主页），无历史则回主页</summary>
    public void SettingsBack()
    {
        if (Root.CanGoBack) Root.GoBack();
        else ShowMainPage();
        if (SettingsService.Current.UiSoundEnabled) UiSound.Play(UiSoundKind.Back);
    }

    /// <summary>指定页面是否仍处于导航栈顶（供页面在后台回调里做自救守卫，避免已切走后误执行）</summary>
    public bool IsCurrentPage(object page) => ReferenceEquals(Root.Content, page);

    /// <summary>启动器窗口当前是否在前台（手柄映射注入按键前判断：在前台时键盘/鼠标注入会打到启动器自己，需跳过；
    /// 手柄输出目标则相反，仅此时才有意义）</summary>
    public bool IsLauncherForeground => GetForegroundWindow() == _hwnd;

    internal bool OwnsPageGamepadInput
    {
        get
        {
            if (!_routing || _hwnd == IntPtr.Zero || !IsWindowVisibleForLaunch(_hwnd) || IsIconic(_hwnd)) return false;
            var foreground = GetForegroundWindow();
            if (foreground == _hwnd || foreground == IntPtr.Zero) return true;
            if (DockPopupWindow.IsOpen) return false;
            GetWindowThreadProcessId(foreground, out var pid);
            if (pid == (uint)OwnProcessId) return true;
            // Allow the activation/animation to settle, and keep existing in-game overlay navigation.
            return Environment.TickCount64 - Interlocked.Read(ref _pageSummonedAt) < 600 || GamePresence.InGame;
        }
    }

    /// <summary>外部（手柄映射服务）把目标手柄键派发到当前页面（手柄输出仅在本启动器内生效）</summary>
    public void DispatchGamepad(GamepadButtons buttons) =>
        DispatcherQueue.TryEnqueue(() => OnGamepadButton(buttons));

    /// <summary>顶栏显示开关变更后即时应用到主页（若已创建）</summary>
    public void ApplyTopbarVisibility()
    {
        if (Root.Content is MainPage mp) mp.ApplyTopbarVisibility();
    }

    /// <summary>抽屉设置（开关/颜色）变更后即时应用到主页（若已创建）</summary>
    public void ApplyDrawerSettings()
    {
        if (Root.Content is MainPage mp) mp.ApplyDrawerSettings();
    }

    private void OnGamepadConnection(bool connected) =>
        DispatcherQueue.TryEnqueue(() => (Root.Content as MainPage)?.SetGamepadStatus(connected));

    private void OnGamepadAllReleased() =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Root.Content is ISettingsGamepad page) page.NotifyAllReleased();
        });

    private void OnGamepadButton(GamepadButtons buttons)
    {
        // Check ownership before queuing as well as before dispatching.
        if (OwnsPageGamepadInput)
            DispatcherQueue.TryEnqueue(() => DispatchButtons(buttons));
    }

    /// <summary>
    /// 鼠标 / 触摸 / 笔点击的统一反馈：与 A 键同一个确认音（常规设置关闭「操作音效」时静默）。
    /// 只有真正点中控件才响：内部处理过的点击（e.Handled）、标准控件、以及自绘的主页磁贴；
    /// 点窗口空白处不发声。
    /// </summary>
    private long _pointerSoundVersion;

    private void OnAnyTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_shown || !SettingsService.Current.UiSoundEnabled ||
            _pointerSoundVersion != UiSound.PlayVersion) return;
        if (!(e.Handled || IsInteractiveSource(e.OriginalSource) ||
              (Root.Content is MainPage page && page.IsTileSource(e.OriginalSource))))
            return;
        // 开关、下拉、列表项、复选/单选、滑块属于"选择命中"，用 success 音；按钮、磁贴等用 A 键音
        // 下拉框由页面的展开/提交事件发声，不能用 SelectionChanged 区分预览和提交。
        if (FindAncestor<ComboBox>(e.OriginalSource) is not null) return;
        if (Root.Content is MainPage home && home.IsToggleTileSource(e.OriginalSource)) return;
        // 性能面板里的应用/确认与主页磁贴开关同款（success 音），不用 A 键确认音
        var perfPanel = Root.Content is MainPage mp && mp.IsPerformancePanelOpen;
        UiSound.Play(perfPanel || IsSelectionSource(e.OriginalSource) ? UiSoundKind.Select : UiSoundKind.Confirm);
    }

    /// <summary>向上找最近的指定类型控件；弹出层里的候选项不在本窗口可视树，不会命中</summary>
    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        for (var el = source as DependencyObject; el is not null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is T hit) return hit;
        }
        return null;
    }

    /// <summary>点击/焦点是否落在"选择类"控件上：开关、下拉、列表项、复选/单选、滑块</summary>
    private static bool IsSelectionSource(object? source)
    {
        for (var el = source as DependencyObject; el is not null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is ToggleSwitch or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton or ComboBox or Slider
                or Microsoft.UI.Xaml.Controls.Primitives.SelectorItem)
                return true;
        }
        return false;
    }

    /// <summary>点击源是否落在标准交互控件上（自绘磁贴、自定义 Border 由各页面另行判定）</summary>
    private static bool IsInteractiveSource(object? source)
    {
        for (var el = source as DependencyObject; el is not null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                or Microsoft.UI.Xaml.Controls.Primitives.SelectorItem
                or ToggleSwitch or ComboBox or Slider)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 全局操作音效：所有页面共用一套反馈音（常规设置关闭「操作音效」时静默）。
    /// 方向键包含长按连发，所以连推摇杆/按住十字键时每次移动都有一声，和主机系统一致。
    /// </summary>
    private UiSoundKind? DefaultCue(GamepadButtons buttons)
    {
        // 性能面板里的应用/切换与主页磁贴开关同款（success 音），不用 A 键确认音
        var perfPanel = Root.Content is MainPage { IsPerformancePanelOpen: true };
        if (perfPanel && (buttons.HasFlag(GamepadButtons.A) || buttons.HasFlag(GamepadButtons.X)))
            return UiSoundKind.Select;
        // 性能控制磁贴的 A 是应用/开关，与主页开关同款 success 音
        if (buttons.HasFlag(GamepadButtons.A) && Root.Content is MainPage { IsPerformanceTileSelected: true })
            return UiSoundKind.Select;
        if (buttons.HasFlag(GamepadButtons.A)) return UiSoundKind.Confirm;
        if (buttons.HasFlag(GamepadButtons.B)) return UiSoundKind.Back;
        if (buttons.HasFlag(GamepadButtons.View))
            return Root.Content is MainPage ? UiSoundKind.Confirm : UiSoundKind.Back;
        if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadDown) ||
            buttons.HasFlag(GamepadButtons.DPadLeft) || buttons.HasFlag(GamepadButtons.DPadRight))
            return UiSoundKind.Move;
        return null;
    }

    /// <summary>手柄按键派发（UI 线程）。allowDeferB=false 专用于"双击 B 判定到期"后补发单击 B 的原有行为</summary>
    private void DispatchButtons(GamepadButtons buttons, bool allowDeferB = true)
    {
        // 入队后输入归属可能已变化，执行前再确认一次。
        if (!OwnsPageGamepadInput) return;

        var overlay = _saveRestoreInput is not null || _trainerChoiceOpen || _optiOpen || _optiManualOpen;
        var cue = IsCapturingNow() && !overlay ? null : DefaultCue(buttons);
        // 开关由实际切换结果反馈；其余主页 A、移动/返回仍立即发声。
        if (!overlay && buttons.HasFlag(GamepadButtons.A) && Root.Content is MainPage home && home.IsToggleSelected)
            cue = null;
        using var feedback = UiSound.BeginFeedback(cue,
            immediate: cue is UiSoundKind.Move or UiSoundKind.Back || Root.Content is MainPage,
            alreadyPlayed: !allowDeferB);
        if (!overlay && allowDeferB && buttons == GamepadButtons.B && Root.Content is not MainPage &&
            !IsCapturingNow() && HandleBPress()) return;

        if (_saveRestoreInput is not null)
        {
            _saveRestoreInput(buttons);
            return;
        }

        // 系统焦点框由各页面在切换光标时自行清理（FocusRing）：页面真正把焦点交给某个控件时
        // 焦点框就是它的光标，不能在这里统一关掉，否则到边界不移动时光标会消失。
        // 修改器候选遮罩打开时，方向/A/B 全交给遮罩，避免误触底下的主页操作
        if (_trainerChoiceOpen)
        {
            HandleTrainerChoice(buttons);
            return;
        }

        // OptiScaler 适配性确认遮罩打开时，A/B 交给遮罩（A=注入，B=取消），避免误触主页
        if (_optiOpen)
        {
            HandleOpti(buttons);
            return;
        }

        // 手动注入遮罩（拖入 exe）：A=备份注入，X=直接注入，B=取消
        if (_optiManualOpen)
        {
            HandleOptiManual(buttons);
            return;
        }

        // 双击 B = 直接最小化：B 按下先挂起，双击窗口内没有第二次按下才补发原 B 行为（返回/收起），
        // 这样双击不会先触发一次返回。
        // 不挂在主页——主页 B 单击本就是"收起窗口到托盘"，与双击同效，延迟反而让收起变卡；
        // 挂起也不用于按键捕获场景——那里 B 是待捕获的原始输入，不能延迟
        // 添加页：方向/A/B 全交给页面（浏览状态 B 返回表单；映射按键捕获时把原始按键交给捕获）
        if (Root.Content is AddItemPage add)
        {
            if (add.IsCapturing) add.CaptureButtons(buttons);
            else add.HandleGamepad(buttons);
            return;
        }

        // 小组件页：同添加页模式（浏览状态 B 返回表单）
        if (Root.Content is WidgetPage widget)
        {
            widget.HandleGamepad(buttons);
            return;
        }

        // 设置主页与各分类子页：捕获模式下按键原样交给页面；
        // 否则方向/A 页面处理，View/B 逐级返回
        if (Root.Content is ISettingsGamepad page)
        {
            if (page.IsCapturing)
            {
                page.CaptureButtons(buttons);
                return;
            }
            page.HandleGamepad(buttons);
            // 页面已消费本次按键（如下拉框展开时 B 只收起下拉）→ 不再执行返回
            if (page.ConsumedBack) return;
            if (buttons.HasFlag(GamepadButtons.View) || buttons.HasFlag(GamepadButtons.B))
            {
                if (page is SettingsPage) SettingsBack();
                else ShowSettingsHub();
            }
            return;
        }

        if (Root.Content is MainPage main && main.HandleGamepad(buttons))
            return;

        if (buttons.HasFlag(GamepadButtons.View)) Root.Navigate(typeof(SettingsPage));
        else if (buttons.HasFlag(GamepadButtons.X))
        {
            // 添加：先弹出方式选择（自定义 / 小组件 / 副页块）
            if (Root.Content is MainPage mp2) mp2.OpenAddChoice();
        }
        else if (buttons.HasFlag(GamepadButtons.RightThumb))
        {
            // R3（右摇杆按下）：编辑当前选中的快捷项；设置/电源常驻磁贴不可编辑
            if (Root.Content is MainPage mp && mp.SelectedItem is { } item && !TileStyle.IsFixedTile(item))
                Root.Navigate(typeof(AddItemPage), item);
        }
        else if (buttons.HasFlag(GamepadButtons.B)) HideToTray();
    }

    // ---- 双击 B = 直接最小化。B 单击原本是"返回上一级/收起"，若不做延迟，
    // 双击的第一下就已经把页面返回了，用户会看到"双击误触发返回"。故第一下先挂起，
    // 双击窗口（同系统"双击速度"，见 MainPage 磁贴双击）内没有第二次按下，才补发单击行为。----

    private bool _bPending;
    private DispatcherTimer? _bTimer;

    /// <summary>B 按下：返回 true=本次按键已在此处消费（双击已最小化 / 首次已挂起）。
    /// false 不返回——本方法总是返回 true，仅为调用点写起来顺手</summary>
    private bool HandleBPress()
    {
        if (_bPending)
        {
            // 双击窗口内的第二次按下：直接最小化（收起启动器）
            _bPending = false;
            _bTimer?.Stop();
            if (SettingsService.Current.UiSoundEnabled) UiSound.Play(UiSoundKind.Back);
            HideToTray();
            return true;
        }
        _bPending = true;
        (_bTimer ??= NewBTimer()).Start();
        return true;
    }

    private DispatcherTimer NewBTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DoublePressMs) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!_bPending) return;
            _bPending = false;
            DispatchButtons(GamepadButtons.B, allowDeferB: false); // 补发单击 B 的原有行为
        };
        return t;
    }

    /// <summary>双击 B 判定窗口（毫秒）。手柄按键是物理按键，双击远快于鼠标，故不用系统"双击速度"
    /// （≥400ms 会让单击返回明显发卡），取 180ms：单击返回几乎无感，又够容纳一次有意的双击</summary>
    private const int DoublePressMs = 180;

    /// <summary>当前页是否在"捕获按键"（此时 B 是待捕获的原始输入，不能延迟/劫持）</summary>
    private bool IsCapturingNow() =>
        (Root.Content as ISettingsGamepad)?.IsCapturing == true ||
        (Root.Content is AddItemPage a && a.IsCapturing);

    public void ToggleFullscreen()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            // 窗口始终无边框（无标题栏），全屏切换只做最大化/还原
            if (_fullscreen)
            {
                _fullscreen = false;
                p.Restore();
                ApplySidebarLayout(); // 退出全屏回到侧边栏位置
            }
            else
            {
                _fullscreen = true;
                // 伪全屏：不用 Maximize，而是 Resize 占满整屏 + Move 到左上角。
                // 最大化窗口无法被 AppWindow.Move 移动，伪全屏才能播放全屏弹出/隐藏滑入动画。
                // 用 OuterBounds（含任务栏）保证上下铺满并盖住任务栏。
                var bounds = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
                AppWindow.Resize(new Windows.Graphics.SizeInt32(bounds.Width, bounds.Height));
                AppWindow.Move(new Windows.Graphics.PointInt32(bounds.X, bounds.Y));
                _homePos = new Windows.Graphics.PointInt32(bounds.X, bounds.Y);
                if (AppWindow.Presenter is OverlappedPresenter p2) p2.IsAlwaysOnTop = true;
                RemoveTitleBarStyle(); // 尺寸变化可能恢复标题栏样式
            }
        }
        // 全屏/侧边栏切换后边缘手势窄条需重新定位（全屏=顶边）
        SyncEdgeGesture();
        // 全屏要方形，退出全屏后按当前停靠方式恢复圆角
        ApplyWindowCorners();
        if (Root.Content is MainPage page) page.UpdateHomeHintLayout();
    }

    // ---- 下载模式：全屏置顶遮罩 / 临时露出下载页 ----

    /// <summary>进入下载模式前是否已处于全屏（退出时按原状恢复）</summary>
    private bool? _downloadPrevFullscreen;

    /// <summary>下载模式期间隐藏的任务栏窗口（退出时按原可见状态恢复）</summary>
    private IntPtr _dlTaskbar;

    /// <summary>进入下载模式：强制全屏 + 置顶，并隐藏任务栏，让纯黑遮罩真正盖满整屏</summary>
    public void BeginDownloadOverlay()
    {
        _downloadPrevFullscreen = _fullscreen;
        if (!_fullscreen)
        {
            ToggleFullscreen();
        }
        else if (AppWindow.Presenter is OverlappedPresenter p2)
        {
            // 已经是全屏：按当前显示器重新铺满（窗口可能被挪动过），不再退出重进以免闪一下
            var bounds = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(bounds.Width, bounds.Height));
            AppWindow.Move(new Windows.Graphics.PointInt32(bounds.X, bounds.Y));
            p2.IsAlwaysOnTop = true;
        }
        SetAlwaysOnTop(true);

        // 任务栏自身是 TOPMOST，伪全屏偶尔仍会被它压住；直接隐藏，退出时恢复
        ReleaseTaskbar(); // 先停掉"边缘呼出任务栏"的维护，避免下载模式期间又把它提起来
        _dlTaskbar = FindWindow("Shell_TrayWnd", null);
        if (_dlTaskbar != IntPtr.Zero)
            ShowWindowAsync(_dlTaskbar, SW_HIDE);
    }

    /// <summary>退出下载模式：恢复任务栏与进入前的全屏状态</summary>
    public void EndDownloadOverlay()
    {
        var wasFullscreen = _downloadPrevFullscreen;
        _downloadPrevFullscreen = null;
        if (_dlTaskbar != IntPtr.Zero)
        {
            // 任务栏隐藏开启时保持隐藏；Explorer 重建过任务栏也不再动新窗口
            if (!TaskbarControl.IsHidden && FindWindow("Shell_TrayWnd", null) == _dlTaskbar)
                ShowWindowAsync(_dlTaskbar, SW_SHOW);
            _dlTaskbar = IntPtr.Zero;
        }
        if (wasFullscreen == false && _fullscreen) ToggleFullscreen();
    }

    /// <summary>查看下载进度：最小化本窗口，露出进入前的下载页面</summary>
    public void HideWindowForPeek()
    {
        if (_hwnd != IntPtr.Zero) ShowWindowAsync(_hwnd, SW_MINIMIZE);
    }

    /// <summary>查看结束：还原并前置本窗口（全屏与置顶保持）</summary>
    public void ShowWindowAfterPeek()
    {
        if (_exiting || _hwnd == IntPtr.Zero) return;
        ShowWindowAsync(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
    }

    /// <summary>把自定义图标设到窗口标题栏与任务栏</summary>
    private void SetWindowIcon()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return;
        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, IconLoader.Small);
        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, IconLoader.Large);
    }

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---- 触摸手势：向下滑 = 返回上一级（主页则最小化）；从屏幕左右边缘起手的横滑 = 最小化；
    // 主页中间的横滑 = 切换副页（与 LT/RT 同向）。全屏停靠时向上滑 = 最小化。
    // 双通道：① 窗口内指针事件（主页磁贴区可靠）；② 原始输入触摸通道（子页被列表/滚动视图
    // 捕获触摸时仍有效，见 EdgeGestureService.DismissTriggered/BackTriggered）。两通道按 600ms 冷却互斥去重。----

    private Windows.Foundation.Point? _swipeStart;
    private DateTime _swipeStartAt;
    private uint _swipeId;
    private bool _swipeDone;
    private bool _swipeMoveLogged;
    private long _dismissAt;
    private long _backAt;

    /// <summary>本次触摸起点是否落在抽屉手势感应区内（决定上/下划是抽屉呼出/收起，而非最小化/返回）</summary>
    private bool _swipeFromBottom;
    /// <summary>本次触摸起点在已向上滚动的内容里：下滑只用于浏览，不判返回</summary>
    private bool _swipeFromScroll;

    /// <summary>指针起点是否在"已滚动过的可滚动区域"：ScrollViewer 还能往上滚且当前不在顶部。
    /// 这类下滑是用户在浏览列表，不能当成"下划=返回"</summary>
    private static bool IsScrolledContentOrigin(PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return false;
        for (var node = src; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer sv) return sv.ScrollableHeight > 8 && sv.VerticalOffset > 8;
        }
        return false;
    }
    /// <summary>最近一次"抽屉感应区内触摸"的时间戳：该触摸期间及抬起后短暂窗口内，
    /// 原始输入触摸通道的返回/最小化一并让位（本地触摸由窗口指针通道处理），避免两通道抢同一手势</summary>
    private long _drawerTouchAt;
    /// <summary>底部上下划判定阈值（DIP），比页面级手势的 120 小：起点已限定在感应区内</summary>
    private const double DrawerSwipeThreshold = 30;

    /// <summary>左右边缘手势带宽度（DIP）：横滑的终点在此带内视为"滑到屏幕边缘 → 收起窗口"</summary>
    private const double ScreenEdgeSwipeZone = 28;

    /// <summary>点是否落在屏幕（窗口）左右边缘带内</summary>
    private bool IsNearSideEdge(Windows.Foundation.Point p)
    {
        if (Content is not FrameworkElement root || root.ActualWidth <= 0) return false;
        return p.X <= ScreenEdgeSwipeZone || p.X >= root.ActualWidth - ScreenEdgeSwipeZone;
    }

    /// <summary>在窗口根面板监听指针（触摸）事件（handledEventsToo=true，ScrollViewer 消费后仍能收到）</summary>
    private void HookSwipeBack()
    {
        var root = (Microsoft.UI.Xaml.Controls.Panel)Content;
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSwipePointerDown), true);
        root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnSwipePointerMove), true);
        root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSwipePointerUp), true);
    }

    private void OnSwipePointerDown(object sender, PointerRoutedEventArgs e)
    {
        if (Root.Content is MainPage { IsMascotPointerCaptured: true })
        {
            _swipeStart = null;
            if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
                _backAt = _dismissAt = Environment.TickCount64;
            return;
        }

        // 起点在"已经向上滚动过"的列表/页面里：这次下滑是浏览内容，不是返回。
        // 指针通道直接跳过返回判定，同时压制原始输入通道（触摸会同时进这两条通道）
        var scrolledOrigin = IsScrolledContentOrigin(e);
        if (scrolledOrigin)
        {
            _edgeGesture.BackSuppressedUntil = Environment.TickCount64 + 900;
            EdgeGestureService.Trace($"win swipe scroll-suppressed page={Root.Content?.GetType().Name}");
        }
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        _swipeStart = e.GetCurrentPoint(null).Position;
        _swipeStartAt = DateTime.UtcNow;
        _swipeId = e.Pointer.PointerId;
        _swipeDone = false;
        _swipeMoveLogged = false;
        _swipeFromScroll = scrolledOrigin;
        // 起点落在抽屉手势感应区内（抽屉面板区域 / 抽屉收起时的窗口底部窄带，仅自动隐藏开启时）：
        // 本次触摸归抽屉处理，并立刻预埋去重时间戳，避免原始输入通道抢先触发"下划=返回/反向滑=最小化"
        _swipeFromBottom = Content is FrameworkElement rootEl && rootEl.ActualHeight > 0
                           && Root.Content is MainPage zonePage && zonePage.DrawerAutoHideActive
                           && _swipeStart.Value.Y >= zonePage.DrawerGestureZoneTop;
        if (_swipeFromBottom)
        {
            _drawerTouchAt = _backAt = _dismissAt = Environment.TickCount64;
        }
        EdgeGestureService.Trace($"win swipe down page={Root.Content?.GetType().Name}");
    }

    private void OnSwipePointerMove(object sender, PointerRoutedEventArgs e)
    {
        if (_swipeDone || _swipeStart is not { } s || e.Pointer.PointerId != _swipeId) return;
        var p = e.GetCurrentPoint(null).Position;
        // 反向滑方向 = 呼出手势的反向：左停靠=向左滑，右停靠=向右滑，全屏=向上滑 → 最小化
        // 主轴位移>120px、正交偏移<80px、时长<500ms；慢速滚动不误触
        var edge = CurrentSummonEdge();
        var dx = p.X - s.X;
        var dy = p.Y - s.Y;
        var reverse = edge switch
        {
            SummonEdge.Left => -dx,
            SummonEdge.Right => dx,
            _ => -dy,
        };
        var cross = edge == SummonEdge.Top ? Math.Abs(dx) : Math.Abs(dy);
        // 诊断：每次按下只记一条，用于确认子页是否还能收到 Moved 事件
        if (!_swipeMoveLogged && Math.Abs(reverse) > 40)
        {
            _swipeMoveLogged = true;
            EdgeGestureService.Trace(
                $"win swipe moved page={Root.Content?.GetType().Name} reverse={reverse} cross={cross} dy={dy}");
        }
        var fast = (DateTime.UtcNow - _swipeStartAt).TotalMilliseconds < 500;

        // 抽屉感应区内竖直滑动：上划=呼出抽屉、下划=收起抽屉（仅"抽屉自动隐藏"开启时接管）。
        // 起点已限定在感应区（抽屉面板区域 / 底部窄带），故阈值比页面级手势小（也不要求快滑）；
        // 命中即 return：本次触摸不再走下面的"反向滑=最小化 / 下划=返回上一级"
        if (_swipeFromBottom && Math.Abs(dx) < 80
            && Root.Content is MainPage drawerPage && drawerPage.DrawerAutoHideActive)
        {
            if (dy <= -DrawerSwipeThreshold)
            {
                _swipeDone = true;
                _drawerTouchAt = _backAt = _dismissAt = Environment.TickCount64;
                drawerPage.SetDrawerSummoned(true);
                return;
            }
            if (dy >= DrawerSwipeThreshold)
            {
                _swipeDone = true;
                _drawerTouchAt = _backAt = _dismissAt = Environment.TickCount64;
                drawerPage.SetDrawerSummoned(false);
                return;
            }
        }

        // 横滑不在这里判定：是"滑到屏幕边缘=最小化"还是"中间横滑=切副页"要等抬手时看终点位置，
        // 见 OnSwipePointerUp（滑动途中若提前切页，继续滑到边缘就会同时切页+最小化）

        // 全屏停靠：向上快滑 → 最小化（竖直手势保持不变）
        if (fast && edge == SummonEdge.Top && reverse > 120 && cross < 80
            && Environment.TickCount64 - Math.Max(_backAt, _dismissAt) > 600)
        {
            _swipeDone = true;
            _dismissAt = Environment.TickCount64; // 与原始输入通道去重
            Minimize();
        }
        // 向下滑（竖直向下、横向偏移小）→ 返回上一级。全屏停靠时"向下"即呼出方向，不会与上面的反向滑冲突
        // 起点在已滚动的内容里时不当返回（那是用户在往下翻看列表）
        else if (_gameSwipeSource == IntPtr.Zero && !_swipeFromScroll && fast && dy > 120 && Math.Abs(dx) < 80
            && Environment.TickCount64 - Math.Max(_backAt, _dismissAt) > 600)
        {
            _swipeDone = true;
            _backAt = Environment.TickCount64; // 与原始输入通道去重
            BackOneLevel();
        }
    }

    private void OnSwipePointerUp(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _swipeId) return;
        var start = _swipeStart;
        var elapsed = (DateTime.UtcNow - _swipeStartAt).TotalMilliseconds;
        _swipeStart = null;
        // 感应区内触摸抬起后仍短暂压制原始输入通道：其 WM_INPUT 抬手检测可能晚于本次回调
        if (_swipeFromBottom)
        {
            _drawerTouchAt = _backAt = _dismissAt = Environment.TickCount64;
            _swipeFromBottom = false;
            return;
        }
        // 横滑按"抬手终点"判定：滑到屏幕左右边缘 = 最小化；停在中间 = 主页切换副页
        if (_swipeDone || start is not { } s || elapsed >= 500) return;
        var p = e.GetCurrentPoint(null).Position;
        var dx = p.X - s.X;
        var dy = p.Y - s.Y;
        if (Math.Abs(dx) <= 120 || Math.Abs(dy) >= 80) return;
        if (IsNearSideEdge(p))
        {
            if (Environment.TickCount64 - Math.Max(_backAt, _dismissAt) > 600) // 原始输入通道可能已抢先触发
            {
                _dismissAt = Environment.TickCount64;
                Minimize();
            }
        }
        else if (Root.Content is MainPage home)
        {
            home.SwitchPageBySwipe(dx > 0 ? -1 : 1);
        }
    }

    /// <summary>反向滑：直接最小化（收起启动器：滑出屏外再隐藏到托盘）</summary>
    private void Minimize() => HideToTray();

    /// <summary>向下滑：返回上一级；已在主页（无上一级）则改用最小化</summary>
    private void BackOneLevel()
    {
        if (Root.Content is MainPage) HideToTray();
        else if (Root.CanGoBack) Root.GoBack();
    }

    /// <summary>原始输入触摸通道识别到"滑到屏幕左右边缘"的快滑：仅在本窗口前台时直接最小化。
    /// 主页也可用（该通道只在终点贴边时触发，不会误伤中间的切页横滑）。</summary>
    private void OnDismissGesture()
    {
        if (Root.Content is MainPage { IsMascotPointerCaptured: true }) return;
        if (!Visible || GetForegroundWindow() != _hwnd) return;
        if (Environment.TickCount64 - _drawerTouchAt < 900) return; // 抽屉感应区触摸：让位给窗口指针通道
        if (Environment.TickCount64 - Math.Max(_backAt, _dismissAt) < 600) return; // 与指针通道去重
        _dismissAt = Environment.TickCount64;
        Minimize();
    }

    /// <summary>原始输入触摸通道识别到"向下滑"：返回上一级（主页则最小化）。
    /// 该方向与磁贴页的横向拖拽换位不冲突，故磁贴页也放行。</summary>
    private void OnBackGesture()
    {
        if (Root.Content is MainPage { IsMascotPointerCaptured: true }) return;
        if (!Visible || GetForegroundWindow() != _hwnd) return;
        if (Environment.TickCount64 - _drawerTouchAt < 900) return; // 抽屉感应区触摸：让位给窗口指针通道
        if (Environment.TickCount64 - Math.Max(_backAt, _dismissAt) < 600) return; // 与指针通道去重
        _backAt = Environment.TickCount64;
        BackOneLevel();
    }

    // ---- 移除标题栏所需 Win32 样式操作 ----

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;   // 不进任务栏 / Alt+Tab
    private const uint LWA_ALPHA = 0x00000002;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;   // WS_BORDER | WS_DLGFRAME
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_SYSMENU = 0x00080000L;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly int OwnProcessId = Environment.ProcessId;

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    // 窗口圆角（Win11 DWMWA_WINDOW_CORNER_PREFERENCE = 33）
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}

/// <summary>
/// 恒定"输入激活"的亚克力背景。
/// 系统的 <c>DesktopAcrylicBackdrop</c> 会随窗口激活状态自动切换配置：窗口失活（被游戏或其他应用抢走前台）时，
/// 背景亚克力会被系统降级为纯色，于是只在点击窗口、重新激活后才看得到磨砂效果。
/// 这里改用手动控制器，把 <see cref="SystemBackdropConfiguration.IsInputActive"/> 恒设为 true，
/// 让磨砂材质与窗口是否前台解耦——呼出后即便不是前台，也保持玻璃质感。
/// </summary>
internal sealed class AlwaysActiveAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _config = new SystemBackdropConfiguration
        {
            IsInputActive = true, // 关键：不跟随窗口激活状态（否则失活即降级为纯色）
            Theme = (xamlRoot.Content as FrameworkElement)?.ActualTheme == ElementTheme.Light
                ? SystemBackdropTheme.Light
                : SystemBackdropTheme.Dark
        };
        _controller = new DesktopAcrylicController();
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(_config);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.Dispose();
        _controller = null;
        _config = null;
    }
}
