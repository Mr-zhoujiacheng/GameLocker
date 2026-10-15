using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using GameLocker.Models;
using GameLocker.Services;

namespace GameLocker;

/// <summary>
/// 主窗口：Frame 容器（MainPage / SettingsPage 导航）+ 全局手柄路由 + 侧边栏布局。
/// 主页按键：方向=导航，A=启动，Y=删除，X=添加，R3=编辑，L3=排列，LB/RB=翻页，View=设置，B=隐藏。
/// 设置页按键：方向=选项，A=切换，View/B=返回。
/// </summary>
public sealed partial class MainWindow : Window
{
    public GamepadService Gamepad { get; } = new();
    private bool _exiting;
    private bool _fullscreen;
    private EdgeGestureService? _edgeGesture;

    /// <summary>手柄是否派发到页面（与 Gamepad.RoutingEnabled 同步；仅窗口已呼出显示时为 true）</summary>
    private bool _routing;

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

    private SummonEdge CurrentSummonEdge()
    {
        if (_fullscreen) return SummonEdge.Top;
        return SettingsService.Current.SidebarSide == DockSide.Right
            ? SummonEdge.Right
            : SummonEdge.Left;
    }

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

        // 平板边缘手势呼出
        _edgeGesture = new EdgeGestureService();
        _edgeGesture.SummonTriggered += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (!Visible || GetForegroundWindow() != _hwnd)
                ShowFromTray();
        });
        // 收起手势（呼出的反方向）：走原始输入触摸通道，与页面内容无关，任何页面都生效
        _edgeGesture.DismissTriggered += () => DispatcherQueue.TryEnqueue(OnDismissGesture);
        SyncEdgeGesture();

        Root.Navigate(typeof(MainPage));

        if (SettingsService.Current.StartFullscreen && !_fullscreen) ToggleFullscreen();

        // 启动：先把窗口移到屏外，App.Activate 显示时不在原位（无虚影），Activated 后滑入
        PrepareSummon();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                // 本窗口成为前台：隐藏边缘条（否则会吞掉本窗口边缘的触摸）
                _edgeGesture?.SetStripVisible(false);
                // 可见时重申手柄路由；失去前台（Deactivated）不动门控——
                // 否则游戏抢回焦点时会把仍然显示在屏幕上的启动器手柄一并关掉
                if (_shown) SetGamepadRouting(true);
            }
            else
            {
                // 任何其他应用成为前台（含被最小化/遮盖）：恢复边缘条，保证任意前台都能触摸呼出
                _edgeGesture?.SetStripVisible(true);
            }
            if (!_summonPending || e.WindowActivationState == WindowActivationState.Deactivated) return;
            _summonPending = false;
            AnimateMove(_summonStart, _summonTarget, 260, easeOut: true, null);
        };
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

    // 当前窗口移动动画状态（CompositionTarget.Rendering 每帧推进）
    private EventHandler<object>? _renderHandler;
    private System.Diagnostics.Stopwatch? _animSw;
    private double _animFromX, _animFromY, _animToX, _animToY;
    private bool _animEaseOut;
    private int _animMs;
    private Action? _animDone;

    /// <summary>目标位置对应的屏外起点：左停靠=左侧屏外，右停靠=右侧屏外，全屏=上方屏外</summary>
    private Windows.Graphics.PointInt32 OffscreenPos(
        Windows.Graphics.PointInt32 target, Windows.Graphics.SizeInt32 size)
    {
        if (_fullscreen)
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
        _summonStart = OffscreenPos(_summonTarget, size);
        MoveWindowTo(_summonStart);
        _summonPending = true;
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

    /// <summary>逐帧移动窗口 from→to，由 CompositionTarget.Rendering 驱动（与合成器 vsync 对齐）</summary>
    private void AnimateMove(Windows.Graphics.PointInt32 from, Windows.Graphics.PointInt32 to,
        int ms, bool easeOut, Action? done)
    {
        StopMoveAnimation();
        _animating = true;
        _animFromX = from.X; _animFromY = from.Y;
        _animToX = to.X; _animToY = to.Y;
        _animMs = ms; _animEaseOut = easeOut; _animDone = done;
        _animSw = System.Diagnostics.Stopwatch.StartNew();
        _renderHandler = OnRenderFrame;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _renderHandler;
    }

    private void OnRenderFrame(object? sender, object e)
    {
        if (_animSw == null) return;
        double t = Math.Clamp(_animSw.ElapsedMilliseconds / (double)_animMs, 0, 1);
        double k = _animEaseOut ? 1 - Math.Pow(1 - t, 3) : t * t * t;
        MoveWindowTo(new Windows.Graphics.PointInt32(
            (int)Math.Round(_animFromX + (_animToX - _animFromX) * k),
            (int)Math.Round(_animFromY + (_animToY - _animFromY) * k)));
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
        BringToForeground();
        // 窗口已处于激活状态时 Activated 不会再触发，这里兜底启动滑入动画
        if (_summonPending)
        {
            _summonPending = false;
            AnimateMove(_summonStart, _summonTarget, 260, easeOut: true, null);
        }
        (Root.Content as MainPage)?.FocusGrid();
        // 已呼出显示：恢复手柄对启动器的操作。只跟显示状态走，不依赖抢前台是否成功
        // （抢前台失败时窗口照样显示，若不在这里打开门控，手柄就会永久失效）
        SetGamepadRouting(true);
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
    /// </summary>
    private void BringToForeground()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetForegroundWindow(_hwnd);
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
        AnimateMove(target, off, 260, easeOut: false, () =>
        {
            AppWindow.Hide();
            _edgeGesture?.SetStripVisible(true);
        });
    }

    /// <summary>
    /// 侧边栏布局：窗口贴屏幕左/右边缘，宽度约 30%、高度铺满整屏（含任务栏区域，盖住任务栏）。
    /// 设置页修改停靠侧后立即调用本方法生效；全屏模式下不干预。
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
        var pct = Math.Clamp(SettingsService.Current.SidebarWidthPercent, 0.38, 0.5);
        var width = Math.Max(360, (int)(bounds.Width * pct));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, bounds.Height));
        var x = SettingsService.Current.SidebarSide == Models.DockSide.Right
            ? bounds.X + bounds.Width - width
            : bounds.X;
        AppWindow.Move(new Windows.Graphics.PointInt32(x, bounds.Y));
        _homePos = new Windows.Graphics.PointInt32(x, bounds.Y);

        // 任务栏自身是 TOPMOST，窗口不置顶的话任务栏会压在窗口下沿之上（盖不住）。
        // 放在 Resize/Move 之后设置，避免中途被窗口操作重置。
        if (presenter != null) presenter.IsAlwaysOnTop = true;

        // presenter 状态操作可能恢复窗口样式，重新移除标题栏
        RemoveTitleBarStyle();

        // 停靠侧可能变化，重新定位边缘手势窄条
        SyncEdgeGesture();
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
    /// </summary>
    private DesktopAcrylicBackdrop? _acrylic;

    public void ApplyBackground()
    {
        var s = SettingsService.Current;
        var content = (Microsoft.UI.Xaml.Controls.Panel)Content;
        content.Background = new SolidColorBrush(Colors.Transparent);

        var color = ParseColor(s.BackgroundColor);

        if (s.GlassEffect)
        {
            _acrylic ??= new DesktopAcrylicBackdrop();
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

    /// <summary>打开磁贴集小组件：进入其添加时复制的主页快照页</summary>
    public void OpenTileSet(LaunchItem set) => Root.Navigate(typeof(TileSetPage), set);

    /// <summary>设置页返回：回到进入设置前的页面（磁贴集副本页/主页），无历史则回主页</summary>
    public void SettingsBack()
    {
        if (Root.CanGoBack) Root.GoBack();
        else ShowMainPage();
    }

    /// <summary>返回上一级页面（磁贴集快照编辑保存/取消后回到副本页）</summary>
    public void GoBackOne()
    {
        if (Root.CanGoBack) Root.GoBack();
        else ShowMainPage();
    }

    /// <summary>指定页面是否仍处于导航栈顶（供页面在后台回调里做自救守卫，避免已切走后误执行）</summary>
    public bool IsCurrentPage(object page) => ReferenceEquals(Root.Content, page);

    /// <summary>顶栏显示开关变更后即时应用到主页（若已创建）</summary>
    public void ApplyTopbarVisibility()
    {
        if (Root.Content is MainPage mp) mp.ApplyTopbarVisibility();
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
        // 轮询线程触发，marshal 回 UI 线程
        DispatcherQueue.TryEnqueue(() =>
        {
            // 双保险：入队后窗口可能已经隐藏/失去前台，此时一律不处理手柄
            // （否则"在游戏里按 Y"会命中启动器上一次选中的磁贴，把游戏关掉）
            if (!_routing) return;

            // 添加页：方向/A/B 全交给页面（浏览状态 B 返回表单）
            if (Root.Content is AddItemPage add)
            {
                add.HandleGamepad(buttons);
                return;
            }

            // 小组件页：同添加页模式（浏览状态 B 返回表单）
            if (Root.Content is WidgetPage widget)
            {
                widget.HandleGamepad(buttons);
                return;
            }

            // 磁贴集副本页：方向/A/LB/RB/R3 交给页面，B/View 返回主页
            if (Root.Content is TileSetPage tileSet)
            {
                // R3：编辑当前选中的快照磁贴（与主页同理；排列模式中不触发）
                if (buttons.HasFlag(GamepadButtons.RightThumb) && !buttons.HasFlag(GamepadButtons.A))
                {
                    if (!tileSet.IsMoving && tileSet.SelectedItem is { } si)
                        Root.Navigate(typeof(AddItemPage), (tileSet.SetTarget, si));
                    return;
                }
                // 页面未消费的 B/View 才返回主页（排列模式中 B=退出排列，已被页面消费）
                if (!tileSet.HandleGamepad(buttons) &&
                    (buttons.HasFlag(GamepadButtons.View) || buttons.HasFlag(GamepadButtons.B)))
                    ShowMainPage();
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
                // 添加：先弹出方式选择（自定义 / 小组件）
                if (Root.Content is MainPage mp2) mp2.OpenAddChoice();
            }
            else if (buttons.HasFlag(GamepadButtons.RightThumb))
            {
                // R3（右摇杆按下）：编辑当前选中的快捷项
                if (Root.Content is MainPage mp && mp.SelectedItem is { } item)
                    Root.Navigate(typeof(AddItemPage), item);
            }
            else if (buttons.HasFlag(GamepadButtons.B)) HideToTray();
        });
    }

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

    // ---- 触摸收起手势：返回上一级 / 主页收起。方向与呼出手势相反：
    // 呼出=从边缘向中心内滑（左停靠=向右、右停靠=向左、全屏=向下），
    // 收起=同轴反向（左停靠=向左、右停靠=向右、全屏=向上）。
    // 双通道：① 窗口内指针事件（主页磁贴区可靠）；② 原始输入触摸通道（子页被列表/滚动视图
    // 捕获触摸时仍有效，见 EdgeGestureService.DismissTriggered）。两通道按 600ms 冷却互斥去重。----

    private Windows.Foundation.Point? _swipeStart;
    private DateTime _swipeStartAt;
    private uint _swipeId;
    private bool _swipeDone;
    private bool _swipeMoveLogged;
    private long _dismissAt;

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
        EdgeGestureService.Trace($"win swipe down page={Root.Content?.GetType().Name}");
    }

    private void OnSwipePointerMove(object sender, PointerRoutedEventArgs e)
    {
        if (_swipeDone || _swipeStart is not { } s || e.Pointer.PointerId != _swipeId) return;
        var p = e.GetCurrentPoint(null).Position;
        // 收起方向 = 呼出手势的反向：左停靠=向左滑，右停靠=向右滑，全屏=向上滑
        // 主轴位移>120px、正交偏移<80px、时长<500ms；慢速滚动不误触
        var edge = CurrentSummonEdge();
        var dx = p.X - s.X;
        var dy = p.Y - s.Y;
        var main = edge switch
        {
            SummonEdge.Left => -dx,
            SummonEdge.Right => dx,
            _ => -dy,
        };
        var cross = edge == SummonEdge.Top ? Math.Abs(dx) : Math.Abs(dy);
        // 诊断：每次按下只记一条，用于确认子页是否还能收到 Moved 事件
        if (!_swipeMoveLogged && Math.Abs(main) > 40)
        {
            _swipeMoveLogged = true;
            EdgeGestureService.Trace($"win swipe moved page={Root.Content?.GetType().Name} main={main} cross={cross}");
        }
        if (main > 120 && cross < 80 &&
            (DateTime.UtcNow - _swipeStartAt).TotalMilliseconds < 500)
        {
            _swipeDone = true;
            _dismissAt = Environment.TickCount64; // 与原始输入通道去重
            DismissCurrent();
        }
    }

    private void OnSwipePointerUp(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _swipeId) _swipeStart = null;
    }

    /// <summary>收起：子页=返回上一级，主页=收起启动器（滑出屏外再隐藏到托盘）</summary>
    private void DismissCurrent()
    {
        if (Root.Content is MainPage)
            HideToTray();
        else if (Root.CanGoBack)
            Root.GoBack();
    }

    /// <summary>原始输入触摸通道识别到"呼出反方向"快滑：仅在本窗口前台、且不在磁贴拖拽页时收起/返回。
    /// 磁贴页（主页/磁贴集）有横向拖拽换位，交给窗口内指针通道判定，避免误收起。</summary>
    private void OnDismissGesture()
    {
        if (!Visible || GetForegroundWindow() != _hwnd) return;
        if (Root.Content is MainPage or TileSetPage) return;
        if (Environment.TickCount64 - _dismissAt < 600) return; // 与指针通道去重
        _dismissAt = Environment.TickCount64;
        DismissCurrent();
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
}
