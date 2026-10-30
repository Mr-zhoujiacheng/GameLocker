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
    private bool _fullscreen;
    private EdgeGestureService? _edgeGesture;

    /// <summary>前台进程采样：修改器小组件据此判断"当前玩的是哪个游戏"。
    /// 启动器是置顶侧边栏，点击磁贴时游戏往往不在前台，故需在后台持续记录最近的前台游戏进程。</summary>
    private readonly Microsoft.UI.Xaml.DispatcherTimer _trainerTracker =
        new() { Interval = TimeSpan.FromMilliseconds(1200) };

    /// <summary>手柄是否派发到页面（与 Gamepad.RoutingEnabled 同步；仅窗口已呼出显示时为 true）</summary>
    private bool _routing;

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

    /// <summary>
    /// 按当前设置同步边缘手势监听：开关开启则启动并把窄条定位到对应边缘
    /// （全屏=顶边，左/右停靠=左/右边缘），关闭则停止并恢复系统边缘手势。
    /// 停靠侧/全屏状态变化后也需调用以重新定位窄条。
    /// </summary>
    public void SyncEdgeGesture()
    {
        if (_edgeGesture == null) return;
        if (!SettingsService.Current.EdgeGestureSummon)
        {
            _edgeGesture.Stop();
            return;
        }
        _edgeGesture.Start();
        _edgeGesture.SetEdge(CurrentSummonEdge());
    }

    /// <summary>是否"满幅悬浮"形态（全屏 / 居中停靠）：这两种模式窗口很宽，
    /// 抽屉面板按窗口宽度的固定比例居中显示，避免横向拉满显得过长（见 MainPage.ApplyDrawerWidth）</summary>
    public bool IsWideOverlay =>
        _fullscreen || SettingsService.Current.SidebarSide == DockSide.Center;

    private SummonEdge CurrentSummonEdge()
    {
        // 开始菜单模式：呼出手势跟着任务栏走（任务栏在下方就从屏幕底边向上滑）
        if (SettingsService.Current.StartMenuMode) return TaskbarEdge();
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

    /// <summary>任务栏所在的屏幕边缘</summary>
    private static SummonEdge TaskbarEdge() => TaskbarState().edge;

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

        Closed += (_, _) =>
        {
            MonitorService.Stop();     // 销毁置顶悬浮条并停止 ETW 会话
            KeyboardHook.Dispose();
            if (_exiting)
                Microsoft.UI.Xaml.Application.Current?.Exit();
        };

        Gamepad.ButtonPressed += OnGamepadButton;
        Gamepad.ConnectionChanged += OnGamepadConnection;
        Gamepad.AllReleased += OnGamepadAllReleased;
        Gamepad.SummonTriggered += () => DispatcherQueue.TryEnqueue(() =>
        {
            // 呼出组合键：窗口隐藏或未在前台（被其他窗口遮盖）时，都置顶聚焦
            if (!Visible || GetForegroundWindow() != _hwnd)
                ShowFromTray();
        });
        Gamepad.SummonCombo = SettingsService.Current.SummonCombo;
        Gamepad.FullscreenCombo = SettingsService.Current.FullscreenCombo;
        Gamepad.FullscreenTriggered += () => DispatcherQueue.TryEnqueue(() =>
        {
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
        Gamepad.Start();
        GamepadRemapService.Init(); // 手柄映射：订阅原始手柄按下（不经路由门控，隐藏到托盘时也生效）

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
        // 手指落在呼出边缘带即开始封锁系统开始菜单（上滑动作完成前先把它按掉，避免原版菜单闪出来）
        _edgeGesture.EdgeTouched += () => DispatcherQueue.TryEnqueue(() => KeyboardHook.BeginStartUiBlockWatch());
        _edgeGesture.SummonTriggered += () => DispatcherQueue.TryEnqueue(() =>
        {
            // 触屏上滑时 explorer 会同时拉起系统开始菜单，先封锁它的宿主进程，避免原版菜单闪一下
            KeyboardHook.BeginStartUiBlockWatch();
            if (!Visible || GetForegroundWindow() != _hwnd)
                ShowFromTray();
        });
        // 收起手势（呼出的反方向）：走原始输入触摸通道，与页面内容无关，任何页面都生效（现为"直接最小化"）
        _edgeGesture.DismissTriggered += () => DispatcherQueue.TryEnqueue(OnDismissGesture);
        // 向下滑：返回上一级（主页则最小化）
        _edgeGesture.BackTriggered += () => DispatcherQueue.TryEnqueue(OnBackGesture);
        SyncEdgeGesture();

        // 前台游戏进程采样（常驻运行：窗口隐藏期间也要能记录到正在玩的游戏）
        _trainerTracker.Tick += (_, _) => TrainerService.CaptureForeground();
        TrainerService.CaptureForeground();
        _trainerTracker.Start();

        // 修改器无精确匹配时，弹窗让用户从相似结果里挑一个
        TrainerService.CandidatesReady += OnTrainerCandidates;

        // OptiScaler 点击磁贴后，弹窗显示适配性并询问是否注入
        OptiScalerService.InspectionReady += OnOptiInspection;

        // 触屏：点弹窗底部放大的提示图标 = 执行对应手柄指令（与物理手柄共用同一处理入口）
        TrainerChoiceHintBar.HintTapped += key => HandleTrainerChoice(GamepadHintBar.ToGamepadButtons(key));
        OptiHintBar.HintTapped += key => HandleOpti(GamepadHintBar.ToGamepadButtons(key));

        Root.Navigate(typeof(MainPage));

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
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                MonitorService.SetAppForeground(true);
                ForegroundWatcherService.SetForeground(true);
                _trainerTracker.Interval = TimeSpan.FromMilliseconds(1200);
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
                _trainerTracker.Interval = TimeSpan.FromMilliseconds(5000);
                _taskbarWatch.Interval = TimeSpan.FromMilliseconds(5000);
                // 开始菜单模式：启动其他程序后保持显示（只能手动隐藏），同时取消置顶，
                // 避免面板盖在新启动的程序上面；仍做一次失焦复查，用于抢回被系统开始菜单夺走的前台
                if (SettingsService.Current.StartMenuMode && _shown)
                {
                    SetAlwaysOnTop(false);
                    _startMenuAutoHide.Start();
                }
                // 任何其他应用成为前台（含被最小化/遮盖）：恢复边缘条，保证任意前台都能触摸呼出
                _edgeGesture?.SetStripVisible(true);
            }
            if (!_summonPending || e.WindowActivationState == WindowActivationState.Deactivated) return;
            _summonPending = false;
            // 滑入结束再抢一次前台：Activate 是在窗口还在屏外时调的，系统有时因此不给键盘焦点
            AnimateMove(_summonStart, _summonTarget, 260, easeOut: true, OnSummonDone);
        };
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

    /// <summary>目标位置对应的屏外起点：开始菜单=任务栏外侧（从任务栏后方滑出），
    /// 左停靠=左侧屏外，右停靠=右侧屏外，全屏/居中=上方屏外</summary>
    private Windows.Graphics.PointInt32 OffscreenPos(
        Windows.Graphics.PointInt32 target, Windows.Graphics.SizeInt32 size)
    {
        if (SettingsService.Current.StartMenuMode)
        {
            var outer = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
            return TaskbarEdge() switch
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
        // 开始菜单模式：滑入期间临时取消置顶，让任务栏压住窗口下沿，形成"从任务栏后方升起"的观感；
        // 动画结束由 ShowFromTray 的完成回调恢复置顶
        if (SettingsService.Current.StartMenuMode) SetAlwaysOnTop(false);
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
        _shown = true;
        _edgeGesture?.SetStripVisible(false);
        PrepareSummon();
        Activate();
        // 开始菜单模式的滑入要保持在任务栏后方：先把置顶/前台抢回推迟到动画结束（OnSummonDone），
        // 否则 BringToForeground 会立刻把窗口提到任务栏之上，失去"从任务栏后方升起"的观感
        if (!SettingsService.Current.StartMenuMode) BringToForeground();
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
    private void ToggleStartMenu()
    {
        var now = Environment.TickCount64;
        if (now - _lastStartMenuToggle < 400) return;
        _lastStartMenuToggle = now;
        // Win 键/点击开始按钮后系统也可能补开开始菜单，短暂封锁它的宿主进程
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
        if (!SettingsService.Current.StartMenuMode) return;
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
            if (SettingsService.Current.StartMenuMode && _shown &&
                KeyboardHookService.IsStartMenuProcessWindow(GetForegroundWindow()))
                BringToForeground();
            await Task.Delay(250);
            if (SettingsService.Current.StartMenuMode && _shown &&
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
        if (GetForegroundWindow() == _hwnd) return;

        var cur = GetCurrentThreadId();
        var fgOwner = GetForegroundWindow();
        var fgThread = fgOwner == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fgOwner, out _);
        // 与前台的线程挂接：只有挂上之后 SetForegroundWindow 才会被系统放行
        var attached = fgThread != 0 && fgThread != cur && AttachThreadInput(fgThread, cur, true);
        SetForegroundWindow(_hwnd);
        SetActiveWindow(_hwnd);
        BringWindowToTop(_hwnd);
        if (attached) AttachThreadInput(fgThread, cur, false);
    }

    /// <summary>托盘菜单退出</summary>
    public void ExitApp()
    {
        _exiting = true;
        Gamepad.Dispose();
        _edgeGesture?.Dispose();
        Close();
    }

    /// <summary>隐藏到托盘：动画开启时先播放反向滑出（真实窗口移出屏外）再隐藏；关闭时直接隐藏</summary>
    public void HideToTray()
    {
        // 立刻停手柄路由：一旦决定隐藏，滑出动画期间就不该再响应手柄
        // （否则刚启动游戏后晃动/误触按键会把启动器上的操作（如 Y 关闭程序）打到已经看不见的界面上）
        _shown = false;
        SetGamepadRouting(false);
        if (!SettingsService.Current.SummonAnimation || _animating)
        {
            AppWindow.Hide();
            _edgeGesture?.SetStripVisible(true); // 窗口隐藏后恢复边缘条，任意前台都能触摸呼出
            return;
        }
        var target = AppWindow.Position;
        var size = AppWindow.Size;
        var off = OffscreenPos(target, size);
        // 开始菜单模式：滑出期间临时取消置顶，让窗口从任务栏后方消失（任务栏压住下沿）
        if (SettingsService.Current.StartMenuMode) SetAlwaysOnTop(false);
        AnimateMove(target, off, 260, easeOut: false, () =>
        {
            AppWindow.Hide();
            _edgeGesture?.SetStripVisible(true);
        });
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
            // 开始菜单模式：面板贴住任务栏所在的那条屏幕边（留 12px 缝），对面留边距，
            // 看起来就是从任务栏后方升起的整块面板。宽度沿用居中模式的倍率换算（38% ≈ 70% 屏宽）
            var menuPct = Math.Min(pct / 0.38 * 0.70, 0.96);
            var work = area.WorkArea;
            const int gap = 12; // 与任务栏之间的缝
            const int sideGap = 16; // 靠边对齐时与屏幕左右边缘的留白
            var inset = Math.Max(24, (int)(work.Height * 0.06)); // 与任务栏相对一侧的留边
            width = Math.Min(Math.Max(360, (int)(work.Width * menuPct)), work.Width - sideGap * 2);
            // 面板位置跟随开始按钮在任务栏长轴上的位置：居中→居中，靠某端→同端（留少量屏幕边距）
            var side = StartButtonSide();
            var alignX = AlignAxis(work.X, work.Width, width, side, sideGap);
            switch (TaskbarEdge())
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
        if (Root.Content is MainPage mp) mp.ApplyDrawerWidth();
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
            Root.GoBack();
    }

    /// <summary>设置主页 → 分类子页</summary>
    public void NavigateSettings(Type page) => Root.Navigate(page);

    /// <summary>主页选择遮罩确认后：进入添加快捷项页</summary>
    public void RootNavigateAddItem() => Root.Navigate(typeof(AddItemPage));

    /// <summary>主页双击磁贴：进入编辑快捷项页（等同 R3）</summary>
    public void NavigateEditItem(Models.LaunchItem item) => Root.Navigate(typeof(AddItemPage), item);

    /// <summary>主页选择遮罩确认后：进入小组件页</summary>
    public void RootNavigateWidget() => Root.Navigate(typeof(WidgetPage));

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
    }

    /// <summary>指定页面是否仍处于导航栈顶（供页面在后台回调里做自救守卫，避免已切走后误执行）</summary>
    public bool IsCurrentPage(object page) => ReferenceEquals(Root.Content, page);

    /// <summary>启动器窗口当前是否在前台（手柄映射注入按键前判断：在前台时键盘/鼠标注入会打到启动器自己，需跳过；
    /// 手柄输出目标则相反，仅此时才有意义）</summary>
    public bool IsLauncherForeground => GetForegroundWindow() == _hwnd;

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

    private void OnGamepadButton(GamepadButtons buttons) =>
        // 轮询线程触发，marshal 回 UI 线程
        DispatcherQueue.TryEnqueue(() => DispatchButtons(buttons));

    /// <summary>手柄按键派发（UI 线程）。allowDeferB=false 专用于"双击 B 判定到期"后补发单击 B 的原有行为</summary>
    private void DispatchButtons(GamepadButtons buttons, bool allowDeferB = true)
    {
        // 双保险：入队后窗口可能已经隐藏/失去前台，此时一律不处理手柄
        // （否则"在游戏里按 Y"会命中启动器上一次选中的磁贴，把游戏关掉）
        if (!_routing) return;

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

        // 双击 B = 直接最小化：B 按下先挂起，双击窗口内没有第二次按下才补发原 B 行为（返回/收起），
        // 这样双击不会先触发一次返回。
        // 不挂在主页——主页 B 单击本就是"收起窗口到托盘"，与双击同效，延迟反而让收起变卡；
        // 挂起也不用于按键捕获场景——那里 B 是待捕获的原始输入，不能延迟
        if (allowDeferB && buttons == GamepadButtons.B && Root.Content is not MainPage &&
            !IsCapturingNow() && HandleBPress()) return;

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
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        _swipeStart = e.GetCurrentPoint(null).Position;
        _swipeStartAt = DateTime.UtcNow;
        _swipeId = e.Pointer.PointerId;
        _swipeDone = false;
        _swipeMoveLogged = false;
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
        if (fast && edge == SummonEdge.Top && reverse > 120 && cross < 80)
        {
            _swipeDone = true;
            _dismissAt = Environment.TickCount64; // 与原始输入通道去重
            Minimize();
        }
        // 向下滑（竖直向下、横向偏移小）→ 返回上一级。全屏停靠时"向下"即呼出方向，不会与上面的反向滑冲突
        else if (fast && dy > 120 && Math.Abs(dx) < 80)
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
            if (Environment.TickCount64 - _dismissAt > 600) // 原始输入通道可能已抢先触发
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
        if (!Visible || GetForegroundWindow() != _hwnd) return;
        if (Environment.TickCount64 - _drawerTouchAt < 900) return; // 抽屉感应区触摸：让位给窗口指针通道
        if (Environment.TickCount64 - _dismissAt < 600) return; // 与指针通道去重
        _dismissAt = Environment.TickCount64;
        Minimize();
    }

    /// <summary>原始输入触摸通道识别到"向下滑"：返回上一级（主页则最小化）。
    /// 该方向与磁贴页的横向拖拽换位不冲突，故磁贴页也放行。</summary>
    private void OnBackGesture()
    {
        if (!Visible || GetForegroundWindow() != _hwnd) return;
        if (Environment.TickCount64 - _drawerTouchAt < 900) return; // 抽屉感应区触摸：让位给窗口指针通道
        if (Environment.TickCount64 - _backAt < 600) return; // 与指针通道去重
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

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    // 窗口圆角（Win11 DWMWA_WINDOW_CORNER_PREFERENCE = 33）
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

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
