using System.Collections.ObjectModel;
using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 主页：Win10 磁贴风格画布（小/长方形/大三种尺寸，自动流式排布），支持多页（副页）横向切换。
/// 手柄：方向=选择，A=启动（组磁贴=展开/收起），Y=关闭程序（编辑模式内 Y=删除，组=拆分组，成员=移出组），
/// LT/RT=切换副页，LB/RB=页内滚动；
/// L3=移动模式：A 抓取磁贴后方向=移动、再 A 放下，X=多选标记、标记≥2 按 A 组合为组，B/L3=退出。
/// 返回 true 表示按键已被本页消费，MainWindow 不再默认处理。
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<LaunchItem> _items = new(ConfigService.Load());
    /// <summary>显示序列 = _items 展开组后的扁平视图（item, 所属组或 null）</summary>
    private readonly List<(LaunchItem item, LaunchItem? group)> _display = [];
    private readonly List<Border> _tiles = [];
    private readonly List<LaunchItem> _marked = [];  // 移动模式中已标记待组合的磁贴
    private LaunchItem? _grabbed;                     // 移动模式中被 A 抓取、正在移动的磁贴
    private LaunchItem? _expanded;                    // 当前展开的组（同时只展开一个）
    private bool _groupAnimating;                     // 组展开/收起动画进行中（期间忽略重复触发）
    private int _selected;
    private bool _moving;

    /// <summary>主页当前页号（0=主主页；LT/RT 循环切换）。_display 只含本页项，_items 仍保留全量</summary>
    private int _page;
    /// <summary>切页动画进行中（期间忽略重复触发）</summary>
    private bool _pageAnimating;

    /// <summary>页码圆点自动隐藏定时器：切页时显示，3 秒后隐藏</summary>
    private readonly DispatcherTimer _pageDotsTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    // ---- 磁贴抽屉组件：固定区域 + 多个抽屉（标签页）----

    /// <summary>焦点是否在抽屉区域内部（内部独占手柄分支：DPad 选磁贴、LB/RB 换抽屉、A 启动、B 退出）</summary>
    private bool _inDrawer;
    /// <summary>当前展示的抽屉（标签）下标；仅运行时状态，不持久化</summary>
    private int _drawerIndex;
    /// <summary>当前抽屉组件（外壳磁贴对应的 LaunchItem）</summary>
    private LaunchItem? _drawerOwner;
    /// <summary>外壳 Border（拖拽悬停高亮 / 内层坐标换算用）</summary>
    private Border? _drawerRegion;
    /// <summary>当前抽屉内磁贴的画布（外壳第 1 行）</summary>
    private Canvas? _drawerInnerCanvas;
    /// <summary>标签条滚动容器（切抽屉后把高亮药丸滚入可见）</summary>
    private ScrollViewer? _drawerTabStrip;
    /// <summary>当前抽屉内磁贴元素（与 _drawerOwner.Drawers[_drawerIndex].Items 顺序对齐）</summary>
    private readonly List<Border> _drawerInnerTiles = [];
    /// <summary>区域内手柄焦点序号（-1=焦点在标签条/无）</summary>
    private int _drawerFocusPos;
    /// <summary>拖拽中指针悬停在抽屉区域上（悬停高亮）</summary>
    private bool _dragOverDrawer;

    /// <summary>抽屉"自动隐藏"：当前是否处于收起状态（面板已向下滑出窗口下缘）</summary>
    private bool _drawerHidden;
    /// <summary>上一次的"自动隐藏"开关值：只在开关切换的瞬间改写 _drawerHidden，
    /// 避免每次重绘（RenderDrawerPanel 由 LayoutTiles 频繁触发）都把已呼出的抽屉又收起</summary>
    private bool? _autoHidePrev;

    // ---- 长按设置磁贴进入编辑模式 + 指针拖拽磁贴 ----
    private LaunchItem? _dragItem;      // 正在拖拽的顶层磁贴
    private Border? _dragTile;
    private Windows.Foundation.Point _dragStart;  // 按下点（相对 TileHost）
    private uint _dragPointerId;
    private bool _dragMoved;            // 超过阈值=进入拖拽（否则视为轻点）
    private bool _suppressTap;          // 长按/拖拽完成后吞掉随后的 Tapped
    private Microsoft.UI.Xaml.DispatcherTimer? _lpTimer;  // 长按计时器
    private Border? _lpTile;
    private LaunchItem? _lpCloseItem;  // 长按目标=可关闭磁贴时记录（null=设置磁贴进编辑 / 游戏存档磁贴走同步）
    private LaunchItem? _lpSyncItem;   // 长按目标=游戏存档磁贴时记录（长按=同步存档）
    private LaunchItem? _lpOptiItem;   // 长按目标=OptiScaler 磁贴时记录（长按=注入）
    private Windows.Foundation.Point _lpStart;
    private uint _lpPointerId;

    // 设置磁贴徽章：拖拽悬停=垃圾桶；标记≥2=加号（点击组合）
    private Border? _settingsTile;      // 当前设置磁贴元素（每次 LayoutTiles 重建时更新）
    private enum SettingsBadge { None, Trash, Plus }
    private SettingsBadge _badge;
    private FontIcon? _badgeIcon;
    private string? _badgeOrigGlyph;
    private bool _dragOverTrash;        // 拖拽中指针悬停在设置磁贴上

    /// <summary>编辑模式专属"添加磁贴"（仅 _moving 时创建并追加到 _tiles 末位；不进 _display）</summary>
    private Border? _addTile;

    /// <summary>抽屉内层的"添加磁贴"（仅编辑模式时创建，追加到 _drawerInnerTiles 末位；Tag 留空 = 不是真实磁贴）</summary>
    private Border? _drawerAddTile;

    /// <summary>当前显示的音乐小组件磁贴引用（重排时重建；key=磁贴元素引用，value=显示序列索引）</summary>
    private readonly Dictionary<TileStyle.MusicTileRefs, int> _musicRefs = [];

    /// <summary>当前显示的图片组磁贴引用（key=磁贴元素引用，value=(显示序列索引, 定向文件夹, 设置项)）</summary>
    private readonly Dictionary<TileStyle.PhotoTileRefs, (int index, string folder, LaunchItem item)> _photoRefs = [];

    /// <summary>当前显示的天气小组件磁贴引用（重排时重建；value=显示序列索引）</summary>
    private readonly Dictionary<TileStyle.WeatherTileRefs, int> _weatherRefs = [];

    /// <summary>当前显示的手柄映射磁贴引用（重排时重建；value=(显示序列索引, 磁贴项)，开关状态变化时只改状态文字）</summary>
    private readonly Dictionary<TileStyle.GamepadMapTileRefs, (int index, LaunchItem item)> _remapRefs = [];

    /// <summary>当前显示的前台应用监测磁贴引用（重排时重建；前台应用或冻结状态变化时只改内容）</summary>
    private readonly Dictionary<TileStyle.ForegroundTileRefs, int> _fgRefs = [];

    /// <summary>当前显示的系统小组件磁贴引用（重排时重建；右摇杆切换面板项/读数变化时只改内容）</summary>
    private readonly Dictionary<TileStyle.SystemTileRefs, LaunchItem> _sysRefs = [];

    /// <summary>长按目标=手柄映射磁贴时记录（长按=关闭映射）</summary>
    private LaunchItem? _lpRemapItem;

    /// <summary>磁贴间距（像素，可在外观设置调节）；磁贴基本单位固定</summary>
    private static double Gap => TileStyle.Gap;
    private const double Unit = TileStyle.Unit;

    public MainPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            ApplyDrawerWidth(); // 全屏/居中模式下抽屉按窗口宽度比例收窄并居中（须在首次重排之前生效）
            LayoutTiles();
            UpdateSelection();
        };

        // 顶栏时钟：每秒刷新（页面离开时停止，避免后台空转）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();

        // 系统小组件读数刷新：音量/亮度/射频被外部改动（系统音量键、设置页、硬件开关）时同步磁贴
        _sysTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _sysTimer.Tick += async (_, _) =>
        {
            if (_sysRefs.Count == 0) return;
            await SystemPanelService.RefreshRadiosAsync(); // 射频状态靠 WinRT 异步读，读完再刷新磁贴
            await SystemPanelService.RefreshBrightnessAsync(); // 亮度走 WMI，放到线程池读，不占 UI 线程
            RefreshSystemTiles();
        };

        // 抽屉"自动隐藏"：开关开启时，启动就处于收起状态（由右摇杆↑ / 底部上划呼出）
        _autoHidePrev = SettingsService.Current.DrawerAutoHide;
        _drawerHidden = SettingsService.Current.DrawerAutoHide;

        // 玻璃拟态浮层：ThemeShadow 需要元素有 Z 方向位移才会投影（Z 只影响投影，不影响 X/Y 布局与命中测试）
        DrawerHost.Translation = new System.Numerics.Vector3(0, 0, 32);

        // 页面尺寸变化（窗口/全屏切换）时重算列数重排；先取消订阅再重排，避免 Height/MinHeight 修改引发自激重排循环
        SizeChanged += (_, _) =>
        {
            ApplyDrawerWidth();
            TileHost.SizeChanged -= TileHost_SizeChanged;
            LayoutTiles();
            TileHost.SizeChanged += TileHost_SizeChanged;
        };

        // 添加方式遮罩：鼠标点击选项/面板外空白（遮罩层）
        ChoiceCustom.Tapped += (_, _) => { CloseChoice(); App.Main?.RootNavigateAddItem(); };
        ChoiceWidget.Tapped += (_, _) => { CloseChoice(); App.Main?.RootNavigateWidget(); };
        ChoiceSubPage.Tapped += (_, _) => { CloseChoice(); AddSubPage(); };
        ChoiceMask.Tapped += (_, _) => CloseChoice();

        // 抽屉改名遮罩：点击面板外空白（遮罩层）取消
        RenameMask.Tapped += (_, _) => CancelRenameDrawerTab();

        // 页码圆点：切页时短暂显示，3 秒无操作自动隐藏
        _pageDotsTimer.Tick += (_, _) =>
        {
            _pageDotsTimer.Stop();
            PageDots.Visibility = Visibility.Collapsed;
        };

        // 点击抽屉面板空白（未命中内层磁贴，命中会 e.Handled）=进入区域
        DrawerHost.Tapped += (_, _) => EnterDrawer();
        // 双击抽屉区域（空白处/当前抽屉本身）=重命名当前抽屉，等同抽屉层 R3（标签条上的双击由标签自身处理）
        DrawerHost.DoubleTapped += (_, _) => BeginRenameDrawerTab();
        // 抽屉面板尺寸变化（内容重排/窗口缩放）：同步底部 toast 的位置（面板收起时 toast 需随其占位高度下移）
        DrawerHost.SizeChanged += (_, _) => UpdateToastOffset();
    }

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _sysTimer;

    private void UpdateClock()
    {
        var now = DateTime.Now;
        // 编辑模式：整个时钟+日期模块被"编辑模式"大字替换，跳过刷新
        if (!_moving)
        {
            ClockText.Text = now.ToString("HH:mm");
            DateText.Text = now.ToString("M月d日 dddd");
        }
        UpdateBattery();
        // 网络状态无需秒级刷新，每 5 秒查询一次
        if (_tickCount % 5 == 0) UpdateNetwork();
        _tickCount++;
        // 音乐磁贴进度秒级刷新
        if (_musicRefs.Count > 0) RefreshMusicTiles();
    }

    private int _tickCount;

    /// <summary>顶栏 Wi-Fi / 蓝牙图标：已连接绿色，未连接白色；关闭开关时隐藏</summary>
    private void UpdateNetwork()
    {
        var s = SettingsService.Current;
        UpdateStatusIcon(WifiIcon, s.ShowWifi, NetworkService.IsWifiConnected());
        UpdateStatusIcon(BluetoothIcon, s.ShowBluetooth, NetworkService.IsBluetoothConnected());
    }

    private static void UpdateStatusIcon(Microsoft.UI.Xaml.Controls.FontIcon icon, bool show, bool connected)
    {
        if (!show)
        {
            icon.Visibility = Visibility.Collapsed;
            return;
        }
        icon.Visibility = Visibility.Visible;
        icon.Foreground = new SolidColorBrush(connected
            ? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B)  // 绿=已连接
            : Microsoft.UI.Colors.White);                        // 白=未连接
    }

    /// <summary>顶栏电池图标：每 10% 一格（Segoe MDL2 Battery0~10），颜色按电量分档；无电池设备/关闭开关时隐藏</summary>
    private void UpdateBattery()
    {
        var pct = BatteryService.GetPercent();
        if (!SettingsService.Current.ShowBattery || pct is null)
        {
            BatteryIcon.Visibility = Visibility.Collapsed;
            return;
        }
        BatteryIcon.Visibility = Visibility.Visible;

        var level = Math.Clamp((int)Math.Ceiling(pct.Value / 10.0), 0, 10);
        // Battery0~9 = E850~E859，Battery10 = E83F
        BatteryIcon.Glyph = level == 10 ? "\uE83F" : ((char)(0xE850 + level)).ToString();

        var color = pct >= 80 ? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B)    // 绿
                  : pct >= 20 ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD3, 0x4D)    // 黄
                  : Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x50, 0x4A);               // 红
        BatteryIcon.Foreground = new SolidColorBrush(color);
    }

    private void TileHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 组展开/收起动画进行中：LayoutTiles 改 Height 会触发本事件，
        // 若此时重排会 Children.Clear 重建、把正在播放动画的磁贴摘除（展开动画"没效果"的根因）
        if (_groupAnimating) return;
        LayoutTiles();
    }

    public LaunchItem? SelectedItem
    {
        get
        {
            // 焦点在抽屉区域：抽屉层 R3 打开抽屉组件编辑页（命名/增删抽屉）；内层 R3 编辑当前聚焦的内层磁贴
            if (_inDrawer) return FocusedDrawerItem() ?? _drawerOwner;
            if (_selected < 0 || _selected >= _display.Count) return null;
            var (item, group) = _display[_selected];
            // 组成员不支持编辑页编辑（展开的组按 R3 编辑组本体：名称/大小/颜色/图标/图片）；
            // 音乐/图片/天气等小组件支持 R3 编辑（表单按 _isWidget 裁剪）
            if (group is not null) return null;
            return item;
        }
    }

    public void SetGamepadStatus(bool connected)
    {
        GamepadIcon.Foreground = new SolidColorBrush(connected
            ? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B)   // 绿
            : Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x50, 0x4A)); // 红
        GamepadIcon.Opacity = 1.0;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 页号：沿用当前页，并同步到窗口供新建磁贴归属
        if (App.Main is { } main) main.CurrentHomePage = _page;
        // 抽屉开关打开但尚无宿主（首次运行/宿主被清空）：补建一个默认宿主并落盘，
        // 保证「打开则抽屉开」恒成立，同时作为往抽屉里添加磁贴的唯一入口
        if (SettingsService.Current.DrawerEnabled && DrawerService.EnsureHost(_items, _page))
            ConfigService.Save(_items.ToList());
        Reload(); // 内部 UpdateSelection 会按当前选中项设置底部提示条
        LayoutPageDots();
        // 页面实例可能重建：主动同步手柄连接状态，避免图标回退默认白色
        SetGamepadStatus(App.Main?.Gamepad.HasGamepad ?? false);
        ApplyTopbarVisibility();
        UpdateClock();
        _clockTimer.Start();
        // 音乐小组件：接管系统媒体控制（SMTC），状态变化即时刷新磁贴
        // （事件可能来自非 UI 线程，marshal 后刷新）
        SystemMediaService.StateChanged += OnMusicStateChanged;
        _ = SystemMediaService.InitAsync();
        RefreshMusicTiles();
        // 图片组小组件：定时轮播当前图片，换图时只刷新磁贴内容不重建
        SlideshowService.CurrentChanged += OnSlideshowChanged;
        SlideshowService.Start();
        RefreshPhotoTiles();
        // 天气小组件：数据在后台线程取得，事件 marshal 后刷新磁贴；窗口显示时按需补一次刷新（30 分钟过期）
        WeatherService.StateChanged += OnWeatherChanged;
        WeatherService.EnsureFresh();
        RefreshWeatherTiles();
        // 修改器小组件：下载/搜索在后台线程执行，进度经提示条反馈
        TrainerService.StatusChanged += OnTrainerStatus;
        // 游戏存档小组件：定位/打包在后台线程执行，进度经提示条反馈
        SaveBackupService.StatusChanged += OnBackupStatus;
        // OptiScaler 小组件：检测/下载/注入在后台线程执行，进度经提示条反馈（适配性弹窗由 MainWindow 处理）
        OptiScalerService.StatusChanged += OnOptiScalerStatus;
        // 手柄映射小组件：开关状态变化后刷新磁贴的"映射开/关"显示
        GamepadRemapService.StateChanged += OnRemapStateChanged;
        RefreshRemapTiles();
        // 前台应用监测小组件：后台轮询当前前台窗口，换应用/冻结状态变化后刷新磁贴
        ForegroundWatcherService.StateChanged += OnForegroundChanged;
        ForegroundWatcherService.Start();
        RefreshForegroundTiles();
        // 系统小组件：定时刷新读数（音量/亮度/射频被外部改动时同步磁贴）
        _sysTimer.Start();
        _ = InitSystemReadingsAsync();
        RefreshSystemTiles();
    }

    /// <summary>系统小组件首次读数：射频 + 亮度都在后台线程读，读完刷新磁贴（避免 UI 线程做 WMI 调用）</summary>
    private async Task InitSystemReadingsAsync()
    {
        await SystemPanelService.RefreshRadiosAsync();
        await SystemPanelService.RefreshBrightnessAsync();
        RefreshSystemTiles();
    }

    private void OnMusicStateChanged() =>
        DispatcherQueue.TryEnqueue(RefreshMusicTiles);

    private void OnSlideshowChanged() =>
        DispatcherQueue.TryEnqueue(RefreshPhotoTiles);

    private void OnWeatherChanged() =>
        DispatcherQueue.TryEnqueue(RefreshWeatherTiles);

    /// <summary>修改器取回进度：事件来自后台线程，marshal 回 UI 线程后用顶部提示条反馈（失败用警告样式）</summary>
    private void OnTrainerStatus(TrainerMessage m) =>
        DispatcherQueue.TryEnqueue(() => ShowToast(m.Text, m.Error ? ToastKind.Warning : ToastKind.Success, m.Progress));

    /// <summary>游戏存档进度：事件来自后台线程，marshal 回 UI 线程后用顶部提示条反馈（失败用警告样式）</summary>
    private void OnBackupStatus(BackupMessage m) =>
        DispatcherQueue.TryEnqueue(() => ShowToast(m.Text, m.Error ? ToastKind.Warning : ToastKind.Success));

    /// <summary>OptiScaler 进度：事件来自后台线程，marshal 回 UI 线程后用顶部提示条反馈（失败用警告样式）</summary>
    private void OnOptiScalerStatus(OptiScalerMessage m) =>
        DispatcherQueue.TryEnqueue(() => ShowToast(m.Text, m.Error ? ToastKind.Warning : ToastKind.Success, m.Progress));

    /// <summary>按设置显示/隐藏顶栏时钟、网络/电池与手柄图标</summary>
    public void ApplyTopbarVisibility()
    {
        var s = SettingsService.Current;
        ClockPanel.Visibility = s.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        GamepadPanel.Visibility = s.ShowGamepadIcon ? Visibility.Visible : Visibility.Collapsed;
        UpdateBattery();
        UpdateNetwork();
    }

    /// <summary>全屏/居中模式下抽屉面板宽度占窗口宽度的比例（这两种模式窗口很宽，铺满会显得横向过长）</summary>
    private const double DrawerWidthRatio = 0.8;

    /// <summary>按窗口形态设定抽屉面板宽度：全屏/居中模式 = 窗口宽度的 80% 且水平居中（不横向拉满）；
    /// 左右贴边停靠（窄条）仍铺满。窗口尺寸变化、停靠方式/全屏切换后调用。</summary>
    public void ApplyDrawerWidth()
    {
        var root = (FrameworkElement)Content;
        var w = root.ActualWidth;
        if (w <= 0) return; // 首帧未测量：等下次布局（SizeChanged/Loaded）再来

        if (App.Main?.IsWideOverlay == true)
        {
            DrawerHost.HorizontalAlignment = HorizontalAlignment.Center;
            DrawerHost.Width = Math.Round(w * DrawerWidthRatio);
        }
        else
        {
            DrawerHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            DrawerHost.Width = double.NaN; // 自动 = 铺满父容器（减去左右外边距）
        }
    }

    /// <summary>抽屉设置（开关/颜色）变更后即时应用：打开时确保抽屉宿主存在（首次开启自动创建并落盘），随后按设置重绘或隐藏抽屉区域</summary>
    public void ApplyDrawerSettings()
    {
        var s = SettingsService.Current;

        // "自动隐藏"开关切换：打开→立即收起；关闭→恢复常驻显示。
        // 只在开关值变化的瞬间改写收起状态，避免重绘时把用户刚呼出的抽屉又收起
        if (_autoHidePrev != s.DrawerAutoHide)
        {
            var first = _autoHidePrev is null;
            _autoHidePrev = s.DrawerAutoHide;
            if (s.DrawerAutoHide) _drawerHidden = true;
            else if (_drawerHidden)
            {
                _drawerHidden = false;
                AnimateDrawerSlide(false, animate: !first);
            }
        }

        if (s.DrawerEnabled && DrawerService.EnsureHost(_items, App.Main?.CurrentHomePage ?? 0))
            ConfigService.Save(_items.ToList());
        RenderDrawerPanel();
        UpdateSelection();
    }

    /// <summary>"自动隐藏"是否生效（抽屉总开关 + 自动隐藏都开启）：触屏底部上划/下划据此决定是否拦截</summary>
    public bool DrawerAutoHideActive =>
        SettingsService.Current.DrawerEnabled && SettingsService.Current.DrawerAutoHide;

    /// <summary>呼出/收起抽屉（自动隐藏）：show=true 向上滑回原位，false 向下滑出窗口。
    /// 由右摇杆↑/↓ 与软件底部上划/下划调用。收起时若焦点正在抽屉内则退回磁贴区。</summary>
    public void SetDrawerSummoned(bool show)
    {
        if (!DrawerAutoHideActive) return;
        if (show == !_drawerHidden) return; // 已经是目标状态
        _drawerHidden = !show;
        if (_drawerHidden && _inDrawer) ExitDrawer(); // 收起时焦点不能留在屏幕外的面板上
        AnimateDrawerSlide(_drawerHidden, animate: true);
    }

    /// <summary>抽屉面板下移/复位：hidden=true 滑出窗口下缘，false 滑回原位。
    /// animate=false 直接摆位（重绘后校正用，避免闪回可见）</summary>
    private void AnimateDrawerSlide(bool hidden, bool animate)
    {
        UpdateToastOffset();
        var target = hidden ? DrawerHideOffset() : 0;
        if (!animate)
        {
            DrawerSlide.Y = target;
            return;
        }
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            EasingFunction = new CubicEase { EasingMode = hidden ? EasingMode.EaseIn : EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, DrawerSlide);
        Storyboard.SetTargetProperty(anim, "Y");
        sb.Children.Add(anim);
        sb.Begin();
    }

    /// <summary>把面板整体移出窗口下缘所需的向下平移量：面板顶行落到页面底边。
    /// 尚未测量（ActualHeight=0，刚 Visible 的首帧）时返回整页高度，保证移出且不闪回</summary>
    private double DrawerHideOffset()
    {
        var root = (FrameworkElement)Content;
        var h = DrawerHost.ActualHeight;
        if (h <= 0) return root.ActualHeight;
        try
        {
            var r = DrawerHost.TransformToVisual(root)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, DrawerHost.ActualWidth, h));
            var y = double.IsNaN(DrawerSlide.Y) ? 0 : DrawerSlide.Y;
            return Math.Max(h, root.ActualHeight - (r.Y - y));
        }
        catch { return root.ActualHeight; }
    }

    /// <summary>底部 toast 相对抽屉的动态底边距：toast 常驻磁贴区底部（Row0 底），抽屉（Row1）在它下方占位，
    /// 故抽屉可见时天然位于抽屉上方。开启"自动隐藏"后面板被平移出屏、但仍占 Row1 的布局高度，
    /// 此时需把 toast 下移这段高度，让它落到磁贴区最底部，而不是悬在半空。
    /// 面板高度 + 上下外边距（DrawerHost Margin="12,6,12,8" → 6+8=14）即需补下的距离。</summary>
    private void UpdateToastOffset()
    {
        var reserve = _drawerHidden && DrawerHost.Visibility == Visibility.Visible
            ? DrawerHost.ActualHeight + 14
            : 0;
        // 30 为无抽屉时的默认底边距（XAML 中的 Margin 0,0,0,30）
        MoveHintBar.Margin = new Thickness(0, 0, 0, 30 - reserve);
    }

    /// <summary>抽屉手势感应区高度（DIP）：抽屉收起（面板不在屏上）时，窗口底部这条窄带内上下划也归抽屉</summary>
    private const double DrawerGestureBand = 72;

    /// <summary>抽屉手势感应区上边界（相对窗口顶部的 DIP）：抽屉可见=面板顶边（上留 8px 余量，整块面板都算感应区）；
    /// 收起=窗口底部窄带。供 MainWindow 判定一次触摸是否属于抽屉手势——属于则只做呼出/隐藏，
    /// 不再走"下划=返回上一级 / 反向滑=最小化"，两套手势因此不再互相抢。</summary>
    public double DrawerGestureZoneTop
    {
        get
        {
            var root = (FrameworkElement)Content;
            var stripTop = Math.Max(0, root.ActualHeight - DrawerGestureBand);
            if (_drawerHidden || DrawerHost.Visibility != Visibility.Visible) return stripTop;
            try
            {
                var r = DrawerHost.TransformToVisual(root)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, DrawerHost.ActualWidth, DrawerHost.ActualHeight));
                var y = double.IsNaN(DrawerSlide.Y) ? 0 : DrawerSlide.Y;
                // 去掉平移动画的影响，取面板"原位"顶边
                return Math.Min(stripTop, Math.Max(0, r.Y - y - 8));
            }
            catch { return stripTop; }
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 双保险：任何导航离开都取消未决长按，避免计时器在后台触发
        _lpTimer?.Stop();
        _lpTile = null;
        _lpCloseItem = null;
        _lpSyncItem = null;
        _lpRemapItem = null;
        _lpOptiItem = null;
        _clockTimer.Stop();
        SystemMediaService.StateChanged -= OnMusicStateChanged;
        SlideshowService.CurrentChanged -= OnSlideshowChanged;
        SlideshowService.Stop();
        WeatherService.StateChanged -= OnWeatherChanged;
        TrainerService.StatusChanged -= OnTrainerStatus;
        SaveBackupService.StatusChanged -= OnBackupStatus;
        OptiScalerService.StatusChanged -= OnOptiScalerStatus;
        GamepadRemapService.StateChanged -= OnRemapStateChanged;
        ForegroundWatcherService.StateChanged -= OnForegroundChanged;
        ForegroundWatcherService.Stop();
        _sysTimer.Stop();
    }

    public void Reload()
    {
        // 去别的页面再返回（OnNavigatedTo/添加流程）时保留抽屉焦点：记下当前层级与内层序号，
        // 面板重建（LayoutTiles → RenderDrawerPanel）后原样恢复，否则焦点会掉回磁贴区
        var keepDrawer = _inDrawer;
        var keepPos = _drawerFocusPos;

        _items.Clear();
        foreach (var it in ConfigService.Load()) _items.Add(it);
        _expanded = null;
        _marked.Clear();
        _inDrawer = false;
        _drawerOwner = null;
        if (_selected >= _items.Count) _selected = Math.Max(0, _items.Count - 1);
        BuildDisplay();
        LayoutTiles();
        if (keepDrawer) RestoreDrawerFocus(keepPos);
        UpdateSelection();
        if (_moving) UpdateHintBar(); // 编辑模式内返回：提示条按恢复后的焦点重刷
    }

    /// <summary>返回主页后按离开前的层级恢复抽屉焦点：抽屉已不存在则不恢复；
    /// 原来在内层但该序号已超出（磁贴被删/换抽屉）时退回抽屉层。</summary>
    private void RestoreDrawerFocus(int pos)
    {
        var owner = DrawerService.FindDrawer(_items.ToList());
        if (owner?.Drawers is not { Count: > 0 }) return;
        _drawerOwner = owner;
        _inDrawer = true;
        _drawerFocusPos = pos >= 0 && pos < _drawerInnerTiles.Count ? pos : -1;
    }

    /// <summary>把 _items（组保持嵌套）按展开状态、当前页号摊平为显示序列</summary>
    private void BuildDisplay()
    {
        _display.Clear();
        foreach (var it in _items)
        {
            if (DrawerService.IsDrawer(it)) continue; // 抽屉固定在主页下方独立区域，不进磁贴网格
            if (PageIndexOf(it) != _page) continue;   // 只显示归属当前页的项
            _display.Add((it, null));
            if (it.Kind == LaunchKind.Group && ReferenceEquals(it, _expanded))
                foreach (var m in it.Members)
                    _display.Add((m, it));
        }
        if (_selected >= _display.Count) _selected = Math.Max(0, _display.Count - 1);
    }

    /// <summary>主页总页数（含主主页，至少 1 页）</summary>
    private static int PageCount() => Math.Max(1, SettingsService.Current.HomePageCount);

    /// <summary>项所属页号；设置磁贴固定在第 0 页（保证任意页都能进设置）</summary>
    private static int PageIndexOf(LaunchItem it) =>
        it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.Settings ? 0 : it.PageIndex;

    // ---- 桌面拖入：快捷方式/程序/网站/文件夹 → 自动识别生成磁贴 ----

    private void OnDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        // 仅接受文件拖放（从桌面/资源管理器拖入）
        e.AcceptedOperation = e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        // 落在下方抽屉面板上 → 文件进当前抽屉（不是主页网格）：高亮面板 + 改拖拽提示文案
        var overDrawer = PointInDrawer(e.GetPosition(TileHost));
        HighlightDrawerFileDrop(overDrawer);
        if (e.DragUIOverride != null)
        {
            var name = overDrawer ? CurrentDrawerPage()?.Name : null;
            e.DragUIOverride.Caption = overDrawer && !string.IsNullOrWhiteSpace(name)
                ? $"放入「{name}」" : "添加到 Game Locker";
        }
    }

    private void OnDragLeave(object sender, Microsoft.UI.Xaml.DragEventArgs e) => HighlightDrawerFileDrop(false);

    /// <summary>外部文件拖到（离开）抽屉面板时的描边反馈</summary>
    private void HighlightDrawerFileDrop(bool on)
    {
        if (_drawerRegion is not { } shell) return;
        if (on)
        {
            shell.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
            shell.BorderThickness = new Thickness(3);
        }
        else if (!_inDrawer)
        {
            ResetDrawerFrame(); // 区域内焦点描边不动，否则恢复默认
        }
    }

    private async void OnDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;

        // 落点在下方抽屉面板上：条目放进当前抽屉；否则照旧进主页当前页
        var overDrawer = PointInDrawer(e.GetPosition(TileHost));
        HighlightDrawerFileDrop(false);

        var added = 0;
        var skipped = 0;
        var list = ConfigService.Load();
        var drawerPage = overDrawer && DrawerService.FindDrawer(list)?.Drawers is { Count: > 0 } ds
            ? ds[Math.Clamp(_drawerIndex, 0, ds.Count - 1)]
            : null;
        foreach (var storage in items)
        {
            var path = storage.Path;
            if (string.IsNullOrEmpty(path)) continue;
            var it = DropImportService.FromDroppedPath(path);
            if (it == null) { skipped++; continue; }
            // 去重：Target 已存在（顶层、组内成员或抽屉内）则跳过
            if (DrawerService.Anywhere(list, x => x.Target == it.Target) ||
                list.Any(x => x.Kind == LaunchKind.Group && x.Members.Any(m => m.Target == it.Target)))
            { skipped++; continue; }
            if (it.Kind != LaunchKind.Url)
                it.IconFile = await IconExtractService.ExtractAsync(it.Target);
            if (drawerPage is not null) drawerPage.Items.Add(it);           // 放进抽屉，不占主页位置
            else
            {
                it.PageIndex = App.Main?.CurrentHomePage ?? 0;              // 拖入项归到主页当前页
                list.Add(it);
            }
            added++;
        }
        if (added > 0) ConfigService.Save(list);

        Reload();
        if (drawerPage is not null && added > 0)
            ShowToast($"已放入「{drawerPage.Name}」{added} 个" + (skipped > 0 ? $" · 跳过 {skipped} 个" : ""));
        else ShowDropToast(added, skipped);
    }

    /// <summary>提示条语义：决定图标徽章与强调色</summary>
    private enum ToastKind { Success, Warning }

    /// <summary>临时提示条：玻璃质感药丸，2 秒后自动隐藏（关闭反馈、删除反馈、拖入结果等复用）。
    /// 成功=青色对勾，警告=琥珀感叹号；位置在磁贴画布底部（手柄提示条上方），首次出现时淡入 + 自下方归位。
    /// progress 非空表示这是一条联网下载进度：文案下方显示细进度条，且只要还有进度上报就不自动收起。</summary>
    private Microsoft.UI.Xaml.DispatcherTimer? _dropToastTimer;
    private Storyboard? _toastIn;

    private void ShowToast(string text, ToastKind kind = ToastKind.Success, double? progress = null)
    {
        var warn = kind == ToastKind.Warning;
        var accent = warn ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB9, 0x00)
                          : Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF);
        MoveHintIcon.Glyph = warn ? "\uE7BA" : "\uE73E"; // 感叹号 / 对勾
        MoveHintIcon.Foreground = new SolidColorBrush(accent);
        MoveHintBadge.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x2A, accent.R, accent.G, accent.B));
        MoveHintText.Text = text;

        // 下载进度 → 细进度条（0..1 折算成 0..100）；下载结束/失败的报告不带进度，条随即收起
        if (progress is { } p)
        {
            MoveHintProgress.Foreground = new SolidColorBrush(accent);
            MoveHintProgress.Value = Math.Clamp(p, 0, 1) * 100;
            MoveHintProgress.Visibility = Visibility.Visible;
        }
        else
        {
            MoveHintProgress.Visibility = Visibility.Collapsed;
        }

        if (MoveHintBar.Visibility != Visibility.Visible)
        {
            UpdateToastOffset(); // 按抽屉当前状态（显示/自动隐藏收起）定位后再显示，避免首帧跳动
            MoveHintBar.Visibility = Visibility.Visible;
            PlayToastIn();
        }
        if (_dropToastTimer == null)
        {
            _dropToastTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _dropToastTimer.Tick += (_, _) =>
            {
                _dropToastTimer!.Stop();
                // 底部 toast 到时直接隐藏；底部操作提示条（HintBar）由 UpdateHintBar 独立管理，互不影响
                MoveHintBar.Visibility = Visibility.Collapsed;
            };
        }
        // 下载中每条进度都会重置计时器（正常下载不会中途消失），卡住超过 6 秒才收起
        _dropToastTimer.Interval = TimeSpan.FromMilliseconds(progress is null ? 2000 : 6000);
        _dropToastTimer.Stop();
        _dropToastTimer.Start();
    }

    /// <summary>提示条入场：淡入 + 自下方 10px 上移归位</summary>
    private void PlayToastIn()
    {
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(fade, MoveHintBar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new DoubleAnimation
        {
            From = 10, To = 0, Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, MoveHintSlide);
        Storyboard.SetTargetProperty(slide, "Y");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(slide);
        _toastIn?.Stop();
        _toastIn = sb;
        sb.Begin();
    }

    private void ShowDropToast(int added, int skipped)
    {
        if (added == 0)
        {
            ShowToast(skipped > 0 ? "拖入项已存在或不支持，未添加" : "未识别到可添加项", ToastKind.Warning);
            return;
        }
        ShowToast(skipped > 0 ? $"已添加 {added} 个磁贴（跳过 {skipped} 个重复/不支持）" : $"已添加 {added} 个磁贴");
    }

    /// <summary>
    /// 处理手柄按键。返回 true = 已消费（MainWindow 跳过默认路由）。
    /// </summary>
    public bool HandleGamepad(GamepadButtons buttons)
    {
        // 添加方式选择遮罩 / 抽屉改名遮罩打开：按键优先交给遮罩
        if (_choiceOpen) return HandleChoice(buttons);
        if (_renaming) return HandleRename(buttons);

        // LT/RT：左右循环切换主页（副页），带滑动动画；本身不参与其它语义
        if (buttons.HasFlag(GamepadButtons.LeftTrigger)) { SwitchPage(-1); return true; }
        if (buttons.HasFlag(GamepadButtons.RightTrigger)) { SwitchPage(1); return true; }

        // 右摇杆↑/↓：抽屉"自动隐藏"的呼出/收起（仅在 抽屉 + 自动隐藏 都开启时接管）
        if (SettingsService.Current.DrawerAutoHide && SettingsService.Current.DrawerEnabled)
        {
            if (buttons.HasFlag(GamepadButtons.RightStickUp)) { SetDrawerSummoned(true); return true; }
            if (buttons.HasFlag(GamepadButtons.RightStickDown)) { SetDrawerSummoned(false); return true; }
        }

        // 右摇杆←/→：系统小组件在焦点内时循环切换面板项（设置/我的电脑/资源管理器/音量/亮度/WiFi/蓝牙）；
        // 与 ↑/↓（抽屉呼出/收起）互不冲突，编辑模式不响应
        if (!_moving && (buttons.HasFlag(GamepadButtons.RightStickLeft) || buttons.HasFlag(GamepadButtons.RightStickRight)))
        {
            if (SystemItemInFocus() is { } sysItem)
            {
                CycleSystemPanel(sysItem, buttons.HasFlag(GamepadButtons.RightStickRight) ? 1 : -1);
                return true;
            }
        }

        // 磁贴抽屉分两层：
        // ① 抽屉层（选中框停在抽屉本身，_drawerFocusPos < 0）：所有按键只作用于抽屉——LB/RB 或 ←→ 切抽屉、X 添加、Y/R3 编辑删除；只有 A 进入内层
        // ② 内层磁贴层（按 A 之后）：所有按键作用于内层磁贴——DPad 选、A 启动、X 添加、Y 关闭/删除、↑ 或 B 退回抽屉层
        if (_inDrawer)
        {
            if (_drawerFocusPos < 0)
            {
                if (buttons.HasFlag(GamepadButtons.DPadUp)) { ExitDrawer(); return true; }   // ↑：退回磁贴区
                if (buttons.HasFlag(GamepadButtons.DPadDown)) return true;                   // ↓ 不进入内层（只有 A 能进入）
                if (buttons.HasFlag(GamepadButtons.DPadLeft) || buttons.HasFlag(GamepadButtons.LeftShoulder)) { CycleDrawer(-1); return true; }
                if (buttons.HasFlag(GamepadButtons.DPadRight) || buttons.HasFlag(GamepadButtons.RightShoulder)) { CycleDrawer(1); return true; }
                // A：编辑模式=把已标记磁贴移入当前抽屉（无标记则进入内层，编辑模式的加号磁贴在里面）；常规模式=进入抽屉内对焦内层磁贴
                if (buttons.HasFlag(GamepadButtons.A))
                {
                    if (_moving && _marked.Count >= 1) MoveMarkedIntoDrawer(_drawerIndex);
                    else EnterDrawerInner();
                    return true;
                }
                if (buttons.HasFlag(GamepadButtons.X)) { if (!_moving) BeginAddIntoDrawer(); return true; }
                if (buttons.HasFlag(GamepadButtons.Y))
                {
                    if (_moving) DeleteDrawerItem();   // 编辑模式：删除当前抽屉（最后一个删完则移除整个组件）
                    else AddDrawerTab();               // 常规模式：新增一个抽屉（等同标签条上的 +）
                    return true;
                }
                if (buttons.HasFlag(GamepadButtons.RightThumb)) { BeginRenameDrawerTab(); return true; } // R3：重命名当前抽屉
                if (buttons.HasFlag(GamepadButtons.B)) { ExitDrawer(); return true; }   // 消费 B，避免落 MainWindow 的最小化
                if (buttons.HasFlag(GamepadButtons.LeftThumb)) { ExitDrawer(); EnterEditMode(); return true; }
                return false; // View（设置）等交 MainWindow
            }

            // 内层磁贴层：最上行再按 ↑（无候选）=退回抽屉层（焦点回到抽屉本身）
            if (buttons.HasFlag(GamepadButtons.DPadUp)) { if (!MoveInDrawer(0, -1)) BackToDrawerLevel(); return true; }
            if (buttons.HasFlag(GamepadButtons.DPadDown)) { MoveInDrawer(0, 1); return true; }
            if (buttons.HasFlag(GamepadButtons.DPadLeft)) { MoveInDrawer(-1, 0); return true; }
            if (buttons.HasFlag(GamepadButtons.DPadRight)) { MoveInDrawer(1, 0); return true; }
            // LB/RB：内层焦点是系统小组件时做调整（音量/亮度），否则按原语义切抽屉
            if (buttons.HasFlag(GamepadButtons.LeftShoulder)) { if (!TryAdjustSystem(-1)) CycleDrawer(-1); return true; }
            if (buttons.HasFlag(GamepadButtons.RightShoulder)) { if (!TryAdjustSystem(1)) CycleDrawer(1); return true; }
            // A：编辑模式=焦点在"添加磁贴"上则走添加流程、否则把已标记的磁贴移入当前抽屉；常规模式=启动内层磁贴
            if (buttons.HasFlag(GamepadButtons.A))
            {
                if (_moving)
                {
                    if (FocusedDrawerIsAddTile()) BeginAddIntoDrawer();
                    else if (_marked.Count >= 1) MoveMarkedIntoDrawer(_drawerIndex);
                }
                else ActivateDrawerFocus();
                return true;
            }
            if (buttons.HasFlag(GamepadButtons.X)) { if (!_moving) BeginAddIntoDrawer(); return true; }
            if (buttons.HasFlag(GamepadButtons.Y))
            {
                if (_moving) DeleteDrawerItem();          // 编辑模式：删内层磁贴 / 抽屉为空时删组件
                else HandleItemY(FocusedDrawerItem());    // 常规模式：注入/同步/关闭程序
                return true;
            }
            if (buttons.HasFlag(GamepadButtons.B)) { BackToDrawerLevel(); return true; }
            if (buttons.HasFlag(GamepadButtons.LeftThumb)) { ExitDrawer(); EnterEditMode(); return true; }
            return false; // 其它键（View/Menu 等）交 MainWindow 默认路由
        }

        if (_moving)
        {
            if (buttons.HasFlag(GamepadButtons.DPadUp)) { if (_grabbed is not null) SwapInDirection(0, -1); else MoveSelection(0, -1); return true; }
            // 最底行再按 ↓（无候选）=进入抽屉区域（编辑模式内用于把标记的磁贴移入抽屉）
            if (buttons.HasFlag(GamepadButtons.DPadDown)) { if (_grabbed is not null) SwapInDirection(0, 1); else if (!MoveSelection(0, 1)) EnterDrawer(); return true; }
            if (buttons.HasFlag(GamepadButtons.DPadLeft)) { if (_grabbed is not null) SwapInDirection(-1, 0); else MoveSelection(-1, 0); return true; }
            if (buttons.HasFlag(GamepadButtons.DPadRight)) { if (_grabbed is not null) SwapInDirection(1, 0); else MoveSelection(1, 0); return true; }
            if (buttons.HasFlag(GamepadButtons.X)) { ToggleMark(); return true; }
            if (buttons.HasFlag(GamepadButtons.Y)) { DeleteSelected(); return true; }
            if (buttons.HasFlag(GamepadButtons.A))
            {
                // 选中"添加磁贴"（追加在显示序列之外的末位）：弹出添加方式选择
                if (_addTile is not null && _selected == _display.Count) { OpenAddFromEdit(); return true; }
                // 已标记 ≥2：组合为组；否则 A = 抓取/放下当前磁贴
                if (_marked.Count >= 2) { CreateGroupFromMarked(); return true; }
                ToggleGrab();
                return true;
            }
            if (buttons.HasFlag(GamepadButtons.LeftThumb) || buttons.HasFlag(GamepadButtons.B))
            {
                EndMove();
                return true;
            }
            return false;
        }

        if (buttons.HasFlag(GamepadButtons.DPadUp)) { MoveSelection(0, -1); return true; }
        // 磁贴区最底行再按 ↓（无候选）=进入抽屉区域（无抽屉组件时 EnterDrawer 静默返回）
        if (buttons.HasFlag(GamepadButtons.DPadDown)) { if (!MoveSelection(0, 1)) EnterDrawer(); return true; }
        if (buttons.HasFlag(GamepadButtons.DPadLeft)) { MoveSelection(-1, 0); return true; }
        if (buttons.HasFlag(GamepadButtons.DPadRight)) { MoveSelection(1, 0); return true; }
        if (buttons.HasFlag(GamepadButtons.A)) { ActivateSelected(); return true; }
        // 常规模式 Y：OptiScaler 磁贴=注入；游戏存档磁贴=同步存档（用最近一份备份覆盖回本地）；
        // 回收站磁贴=一键清空回收站；前台应用磁贴=强制结束当前前台应用；系统磁贴无定义（消费按键）；否则关闭选中磁贴对应的运行中程序
        if (buttons.HasFlag(GamepadButtons.Y))
        {
            if (IsOptiSelected()) OptiScalerService.Activate(_display[_selected].item);
            else if (IsBackupSelected()) SaveBackupService.Sync(_display[_selected].item);
            else if (IsRecycleBinSelected()) EmptyRecycleBin();
            else if (IsForegroundSelected()) CloseForegroundApp();
            else if (IsSystemSelected()) ShowToast("系统小组件：右摇杆←→切换 · A 开关 · LB/RB 调整");
            else CloseSelected();
            return true;
        }
        // 普通模式不响应其他键（防误删）：仅 L3 编辑/移动模式内 Y=删除
        if (buttons.HasFlag(GamepadButtons.LeftShoulder))
        {
            // 选中系统小组件：LB=调整（音量/亮度减小）；选中音乐小组件：LB=上一曲；选中图片组：LB=上一张；否则翻页
            if (TryAdjustSystem(-1)) return true;
            if (IsMusicSelected()) { SystemMediaService.Prev(); return true; }
            if (IsPhotoSelected()) { SlideshowService.Skip(SelectedFolder(), -1); return true; }
            PageScroll(false); return true;
        }
        if (buttons.HasFlag(GamepadButtons.RightShoulder))
        {
            if (TryAdjustSystem(1)) return true;
            if (IsMusicSelected()) { SystemMediaService.Next(); return true; }
            if (IsPhotoSelected()) { SlideshowService.Skip(SelectedFolder(), 1); return true; }
            PageScroll(true); return true;
        }
        if (buttons.HasFlag(GamepadButtons.LeftThumb))
        {
            EnterEditMode();
            return true;
        }
        return false;
    }

    /// <summary>进入编辑（移动）模式：时钟+日期换成"编辑模式"大字，提示条换成编辑操作提示。
    /// L3 与长按设置磁贴共用；重排一次以给磁贴挂上拖拽手势。
    /// 另加过渡反馈：大字弹出 + 所有磁贴错落抖动一下，让人明确感知已进入编辑模式。</summary>
    private void EnterEditMode()
    {
        if (_moving) return;
        _moving = true;
        // 若当前焦点在抽屉区域内部，先退出（编辑模式不建立区域内焦点）
        _inDrawer = false;
        _drawerFocusPos = -1;
        ClockText.Visibility = Visibility.Collapsed;
        DateText.Visibility = Visibility.Collapsed;
        EditModeText.Visibility = Visibility.Visible;
        UpdateHintBar();
        LayoutTiles();
        PopInEditModeText();
        ShakeTiles();
    }

    /// <summary>"编辑模式"大字弹出：0.6 → 1 缩放 + 淡入，收尾时清掉 RenderTransform 免得留残影</summary>
    private void PopInEditModeText()
    {
        EditModeText.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        EditModeText.RenderTransform = new ScaleTransform { ScaleX = 0.6, ScaleY = 0.6 };
        EditModeText.Opacity = 0;

        var dur = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sb = new Storyboard();

        var o = new DoubleAnimation { From = 0, To = 1, Duration = dur, EasingFunction = ease };
        Storyboard.SetTarget(o, EditModeText);
        Storyboard.SetTargetProperty(o, "Opacity");
        sb.Children.Add(o);

        foreach (var path in new[]
                 {
                     "(UIElement.RenderTransform).(ScaleTransform.ScaleX)",
                     "(UIElement.RenderTransform).(ScaleTransform.ScaleY)"
                 })
        {
            var a = new DoubleAnimation { From = 0.6, To = 1, Duration = dur, EasingFunction = ease };
            Storyboard.SetTarget(a, EditModeText);
            Storyboard.SetTargetProperty(a, path);
            sb.Children.Add(a);
        }

        sb.Completed += (_, _) =>
        {
            EditModeText.RenderTransform = null;
            EditModeText.Opacity = 1;
        };
        sb.Begin();
    }

    /// <summary>进入编辑模式的磁贴抖动：每个磁贴快速左右各摆几下后回正，按序号错开起播时间形成"波纹"。
    /// 走 Composition 层的 RotationAngleInDegrees（不改 RenderTransform），因此与拖拽的 TranslateTransform、
    /// 换位/展开动画互不干扰；动画收在 0°，结束后不留偏移。</summary>
    private void ShakeTiles()
    {
        for (var k = 0; k < _tiles.Count; k++)
        {
            var t = _tiles[k];
            var visual = ElementCompositionPreview.GetElementVisual(t);
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)t.ActualWidth / 2f, (float)t.ActualHeight / 2f, 0f);

            var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(360);
            anim.DelayTime = TimeSpan.FromMilliseconds(k * 22);
            anim.InsertKeyFrame(0f, 0f);
            anim.InsertKeyFrame(0.2f, -4.5f);
            anim.InsertKeyFrame(0.4f, 4.5f);
            anim.InsertKeyFrame(0.6f, -3f);
            anim.InsertKeyFrame(0.8f, 2f);
            anim.InsertKeyFrame(1f, 0f);
            visual.StartAnimation("RotationAngleInDegrees", anim);
        }
    }

    private void EndMove()
    {
        _moving = false;
        _marked.Clear();
        _grabbed = null;
        _dragItem = null;
        _dragTile = null;
        _dragMoved = false;
        _dragOverTrash = false;
        _dragOverDrawer = false;
        RefreshSettingsBadge();
        // 选中项若停在"添加磁贴"（显示序列之外的末位），退出后回落到最后一个真实磁贴
        if (_selected >= _display.Count) _selected = Math.Max(0, _display.Count - 1);
        // 恢复时钟+日期模块与底部常规提示条
        EditModeText.Visibility = Visibility.Collapsed;
        ClockText.Visibility = Visibility.Visible;
        DateText.Visibility = Visibility.Visible;
        UpdateClock();
        LayoutTiles();
        UpdateSelection(); // 末尾按当前选中项恢复底部提示条（音乐磁贴=音乐提示）
        ShakeTiles();      // 退出编辑模式同样抖一下，让"回到普通模式"也有明确反馈
    }

    /// <summary>移动模式 A 键：抓取当前磁贴（此后方向键=移动它）/ 再按放下</summary>
    private void ToggleGrab()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        if (_grabbed is not null)
        {
            _grabbed = null;
        }
        else
        {
            if (group is not null || item.Kind == LaunchKind.Group) return; // 组成员/组本体不可抓取
            _grabbed = item;
        }
        UpdateHintBar();
        LayoutTiles();
        UpdateSelection();
    }

    /// <summary>A 键：组磁贴展开/收起，成员/顶层磁贴启动</summary>
    private void ActivateSelected() => ActivateAt(_selected);

    // ---- 布局 ----

    private static (int Cols, int Rows) CellsOf(TileSize size) => Packing.CellsOf(size);

    /// <summary>自动流式排布：每个磁贴放入第一个能容纳它的空位（按 _display 显示序列）</summary>
    private void LayoutTiles()
    {
        TileHost.Children.Clear();
        _tiles.Clear();
        _musicRefs.Clear();
        _photoRefs.Clear();
        _weatherRefs.Clear();
        _remapRefs.Clear();
        _fgRefs.Clear();
        _sysRefs.Clear();
        // 磁贴重建：旧的徽章引用失效
        _settingsTile = null;
        _badge = SettingsBadge.None;
        _badgeIcon = null;
        _badgeOrigGlyph = null;
        _addTile = null;
        // 抽屉区域引用由 RenderDrawerPanel 重建

        // 列数以可视区宽度为准（ViewportWidth 已扣除 Padding，保证最右列不被裁切）
        var availW = TileScroll.ViewportWidth > 0 ? TileScroll.ViewportWidth : TileHost.ActualWidth;
        if (availW <= 0) return;

        var cols = Math.Max(2, (int)((availW + Gap) / (Unit + Gap)));
        var occupied = new HashSet<(int, int)>();
        int maxRow = -1;
        int maxCol = -1;
        var placed = new List<(Border tile, int col, int row)>();

        for (var i = 0; i < _display.Count; i++)
        {
            var (item, group) = _display[i];
            // 组成员保持自身原有尺寸/样式；音乐小组件固定 2×2，系统小组件固定 2×1，其余可编辑尺寸
            var (w, h) = item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Music
                ? (2, 2)
                : item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.System
                    ? (2, 1)
                    : CellsOf(item.Size);

            (int c, int r) spot = (0, 0);
            bool found = false;
            for (var r = 0; !found && r < 1000; r++)
                for (var c = 0; c + w <= cols && !found; c++)
                {
                    if (Fits(occupied, c, r, w, h))
                    {
                        spot = (c, r);
                        found = true;
                    }
                }
            if (!found) break; // 极端情况放不下，剩余项下帧再排

            for (var dy = 0; dy < h; dy++)
                for (var dx = 0; dx < w; dx++)
                    occupied.Add((spot.c + dx, spot.r + dy));
            maxRow = Math.Max(maxRow, spot.r + h - 1);
            maxCol = Math.Max(maxCol, spot.c + w - 1);

            var tile = CreateTile(item, group, i, w, h);
            placed.Add((tile, spot.c, spot.r));
            TileHost.Children.Add(tile);
            _tiles.Add(tile);
        }

        // 编辑模式专属"添加磁贴"（1×1 小）：排在最后一个空位，点击=退出编辑并弹出添加方式选择（等同普通模式 X 键）。
        // 只并入 _tiles（可手柄选中）而不进 _display——因此不参与标记/拖拽/删除，所有索引比对处均以 _display.Count 收口。
        if (_moving)
        {
            (int c, int r) add = (-1, -1);
            for (var r = 0; add.c < 0 && r < 1000; r++)
                for (var c = 0; c < cols; c++)
                    if (Fits(occupied, c, r, 1, 1)) { add = (c, r); break; }
            if (add.c >= 0)
            {
                occupied.Add((add.c, add.r));
                maxRow = Math.Max(maxRow, add.r);
                maxCol = Math.Max(maxCol, add.c);

                _addTile = CreateAddTile();
                _addTile.Tapped += (_, _) => OpenAddFromEdit();
                placed.Add((_addTile, add.c, add.r));
                TileHost.Children.Add(_addTile);
                _tiles.Add(_addTile);
            }
        }

        // 水平居中：整块磁贴按实际占用宽度在可视区内居中（窗口大小改变时随 SizeChanged 重算）；垂直保持靠左上
        var usedW = maxCol < 0 ? 0 : (maxCol + 1) * (Unit + Gap) - Gap;
        var offsetX = Math.Max(0, (availW - usedW) / 2);
        foreach (var (tile, c, r) in placed)
        {
            Canvas.SetLeft(tile, offsetX + c * (Unit + Gap));
            Canvas.SetTop(tile, r * (Unit + Gap));
        }

        // 撑满视口靠 XAML 的 MinWidth/MinHeight 绑定实现（垂直靠左上，暂不居中）；这里只设内容实际高度供超出时滚动
        TileHost.Height = Math.Max(200, (maxRow + 1) * (Unit + Gap));

        // 主页下方固定抽屉区域：按面板实际宽度重排（内容随宽度换行、高度随内容增长）
        RenderDrawerPanel();

        // 磁贴是每次重排新建的 Border，CreateTile 只画默认/标记/抓取描边；
        // 选中框由 UpdateSelection 叠加。呼出显示、组展开/收起、返回主页等都会触发
        // SizeChanged→LayoutTiles 重建磁贴，若不在此重绘选中框，手柄选中框就会丢失。
        UpdateSelection();
        RefreshSettingsBadge();
    }

    /// <summary>按当前状态刷新设置磁贴徽章：拖拽悬停=垃圾桶；选中≥2=加号（点击组合）；否则恢复原图标</summary>
    private void RefreshSettingsBadge()
    {
        var want = SettingsBadge.None;
        if (_moving && _settingsTile is not null)
            want = _dragOverTrash ? SettingsBadge.Trash
                   : _marked.Count >= 2 ? SettingsBadge.Plus
                   : SettingsBadge.None;
        if (_badge == want) return;
        if (_badgeIcon is not null && _badgeOrigGlyph is not null) _badgeIcon.Glyph = _badgeOrigGlyph;
        _badgeIcon = null;
        _badgeOrigGlyph = null;
        _badge = want;
        if (_settingsTile is null) return;
        if (want == SettingsBadge.None)
        {
            _settingsTile.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            _settingsTile.BorderThickness = new Thickness(1);
            UpdateSelection(); // 若设置磁贴正被选中，重绘选中描边
            return;
        }
        _settingsTile.BorderBrush = new SolidColorBrush(want == SettingsBadge.Trash
            ? Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x50, 0x4A)
            : Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B));
        _settingsTile.BorderThickness = new Thickness(3);
        _badgeIcon = FindIcon(_settingsTile);
        if (_badgeIcon is not null)
        {
            _badgeOrigGlyph = _badgeIcon.Glyph;
            _badgeIcon.Glyph = want == SettingsBadge.Trash ? "\uE74D" : "\uE710"; // 删除 / 加号（E710 与设置齿轮 E713 同系列线条风格）
        }
    }

    private static bool Fits(HashSet<(int, int)> occ, int c, int r, int w, int h)
        => Packing.Fits(occ, c, r, w, h);

    /// <summary>创建单个磁贴（强调色底 + 图标 + 名称，Win10 磁贴风格；group 非空表示组展开成员）</summary>
    private Border CreateTile(LaunchItem item, LaunchItem? group, int index, int cellsW, int cellsH)
    {
        double width = cellsW * Unit + (cellsW - 1) * Gap;
        double height = cellsH * Unit + (cellsH - 1) * Gap;

        // 无颜色磁贴判定与配色（与小组件页共用同一套样式）
        var noColor = TileStyle.IsNoColor(item);
        var accent = TileStyle.AccentFor(item);

        // 音乐小组件磁贴：外壳样式与普通磁贴一致，内容显示播放状态与控制按钮
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Music)
        {
            var (musicTile, refs) = TileStyle.BuildMusicTile(
                item, width, height, accent, noColor,
                SystemMediaService.Prev, SystemMediaService.TogglePlay, SystemMediaService.Next);
            musicTile.Tag = index;
            _musicRefs[refs] = index;
            musicTile.Tapped += (_, _) => OnTileTapped(index, null);
            musicTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(musicTile, item, group, index, false);
            return musicTile;
        }

        // 图片组小组件磁贴：铺满轮播图片；未定向/无图时显示占位提示
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Photos)
        {
            var folder = item.WidgetFolder ?? "";
            SlideshowService.Register(folder, SlideshowService.IntervalOf(item.SlideshowSpeed));
            var img = SlideshowService.CurrentImage(folder);
            var placeholder = string.IsNullOrEmpty(folder) ? "图片组\n按 R3 选择图片文件夹"
                : !Directory.Exists(folder) ? "图片文件夹不存在\n按 R3 重新选择"
                : !SlideshowService.IsScanned(folder) ? "正在扫描图片…"
                : "文件夹中没有图片";
            var (photoTile, refs) = TileStyle.BuildPhotoTile(item, width, height, img, placeholder);
            photoTile.Tag = index;
            _photoRefs[refs] = (index, folder, item);
            photoTile.Tapped += (_, _) => OnTileTapped(index, () => SlideshowService.Skip(folder, 1));
            photoTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(photoTile, item, group, index, false);
            return photoTile;
        }

        // 天气小组件磁贴：显示当前位置当天天气，A 键/点击 = 刷新（只改文字不重建磁贴）
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Weather)
        {
            var (weatherTile, weatherRefs) = TileStyle.BuildWeatherTile(item, width, height, accent, noColor);
            weatherTile.Tag = index;
            _weatherRefs[weatherRefs] = index;
            weatherTile.Tapped += (_, _) => OnTileTapped(index, WeatherService.Refresh);
            weatherTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(weatherTile, item, group, index, false);
            TileStyle.FillWeatherTile(weatherRefs); // 建好即显示当前数据（无需等一次刷新）
            return weatherTile;
        }

        // 映射小组件磁贴：显示已映射的按键，A 键/点击 = 触发（键盘键对外发送 / 手柄键内部派发）
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Mapping)
        {
            var mappingTile = TileStyle.BuildMappingTile(item, width, height, accent, noColor);
            mappingTile.Tag = index;
            mappingTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            mappingTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(mappingTile, item, group, index, false);
            return mappingTile;
        }

        // 修改器小组件磁贴：A 键/点击 = 按当前游戏进程自动下载并打开最新修改器
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Trainer)
        {
            var trainerTile = TileStyle.BuildTrainerTile(item, width, height, accent, noColor);
            trainerTile.Tag = index;
            trainerTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            trainerTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(trainerTile, item, group, index, false);
            return trainerTile;
        }

        // 游戏存档小组件磁贴：A 键/点击 = 备份当前正在玩的游戏的存档
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Backup)
        {
            var backupTile = TileStyle.BuildBackupTile(item, width, height, accent, noColor);
            backupTile.Tag = index;
            backupTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            backupTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(backupTile, item, group, index, false);
            return backupTile;
        }

        // 手柄映射小组件磁贴：显示开关状态，A 键/点击 = 开/关映射，长按 = 关闭映射
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.GamepadMap)
        {
            var (remapTile, remapRefs) = TileStyle.BuildGamepadMapTile(item, width, height, accent, noColor);
            remapTile.Tag = index;
            _remapRefs[remapRefs] = (index, item);
            remapTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            remapTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(remapTile, item, group, index, false);
            TileStyle.FillGamepadMapState(remapRefs, item); // 建好即显示当前开关状态
            return remapTile;
        }

        // OptiScaler 小组件磁贴：A 键/点击 = 检测当前游戏适配性并询问是否注入
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.OptiScaler)
        {
            var optiTile = TileStyle.BuildOptiScalerTile(item, width, height, accent, noColor);
            optiTile.Tag = index;
            optiTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            optiTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(optiTile, item, group, index, false);
            return optiTile;
        }

        // 前台应用监测小组件磁贴：实时显示当前前台应用，A 键/点击 = 冻结/解冻，Y 键/长按 = 结束进程
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Foreground)
        {
            var (fgTile, fgRefs) = TileStyle.BuildForegroundTile(item, width, height, accent, noColor);
            fgTile.Tag = index;
            _fgRefs[fgRefs] = index;
            fgTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            fgTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(fgTile, item, group, index, false);
            TileStyle.FillForegroundTile(fgRefs); // 建好即显示当前前台应用
            return fgTile;
        }

        // 系统小组件磁贴：一个磁贴收纳系统入口与开关，右摇杆←→切换，A=打开/开关，LB/RB=调整
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.System)
        {
            var (sysTile, sysRefs) = TileStyle.BuildSystemTile(item, width, height, accent, noColor);
            sysTile.Tag = index;
            _sysRefs[sysRefs] = item;
            sysTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            sysTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(sysTile, item, group, index, false);
            TileStyle.FillSystemTile(sysRefs, item); // 建好即显示当前面板项
            return sysTile;
        }

        // 标准磁贴（背景图片铺满 / 强调色底+图标+名称）：与小组件页预览共用同一构建
        var tile = TileStyle.BuildStandardTile(item, group, width, height, accent, noColor);
        tile.Tag = index;

        // 移动模式已标记：青色描边提示；已抓取：黄色描边提示
        if (_moving && _marked.Contains(item))
        {
            tile.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
            tile.BorderThickness = new Thickness(3);
        }
        else if (_moving && ReferenceEquals(_grabbed, item))
        {
            tile.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD3, 0x4D));
            tile.BorderThickness = new Thickness(3);
        }

        tile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
        tile.DoubleTapped += (_, _) => HandleDoubleTap(index);
        var isSettings = item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Settings;
        if (isSettings) _settingsTile = tile;
        AttachEditGestures(tile, item, group, index, isSettings);
        return tile;
    }

    /// <summary>编辑模式专属"添加磁贴"（1×1 小）：淡色底 + 强调色加号，只作为"去添加"的入口控件——
    /// 无名称、不挂长按/拖拽/双击手势。点击（或手柄选中后按 A）等同普通模式 X 键。</summary>
    private Border CreateAddTile() => new()
    {
        Width = Unit,
        Height = Unit,
        CornerRadius = new CornerRadius(TileStyle.Radius),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x4C, 0xC2, 0xFF)),
        BorderThickness = new Thickness(1.5),
        Child = new FontIcon
        {
            Glyph = "\uE710", // 加号
            FontSize = 28,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    /// <summary>"添加磁贴"点击/按 A：直接弹出添加方式选择遮罩（与普通模式 X 键一致），
    /// 不退出编辑模式——加完返回主页仍停在编辑模式，方便继续调整</summary>
    private void OpenAddFromEdit()
    {
        if (!_moving) return;
        OpenAddChoice();
    }

    // ==================== 磁贴抽屉（WidgetKind.Drawer）====================
    // 主页下方固定区域：顶部一行抽屉标签条（药丸 + "+"），下方是当前抽屉的磁贴。
    // 宽度铺满软件界面并随窗口自适应，高度随内容增长；磁贴区最底行按 ↓ 进入、区域内最上行按 ↑ 退出。
    // 分两层焦点：抽屉层（选中框停在抽屉本身，_drawerFocusPos < 0）= 按键只作用于抽屉（←→/LB/RB 切抽屉、X 添加、Y/R3 编辑），
    // 按 A 才进入内层磁贴层 = 按键作用于内层磁贴（DPad 选、A 启动、Y 关闭、B/↑ 退回抽屉层）。
    // 内层磁贴是面板的子元素，不进 _display/_tiles，因此不参与组展开/标记/换位。

    /// <summary>抽屉标签条高度（像素）——一行标签 + 贴底选中下线，下沿即标签条基线</summary>
    private const double DrawerTabHeight = 34;
    /// <summary>抽屉内容区左右内边距（内容区宽度 = 面板宽度 - 2×该值）</summary>
    private const double DrawerPadH = 12;
    /// <summary>抽屉内容区下内边距</summary>
    private const double DrawerPadV = 10;

    /// <summary>抽屉标签条中当前选中的标签（切抽屉后滚入可见）</summary>
    private FrameworkElement? _drawerSelectedPill;

    /// <summary>设置里的抽屉控件色（"none"/空 = 未指定，此时回退到抽屉自身的随机强调色 = 原观感）</summary>
    private static Windows.UI.Color? DrawerColorSetting =>
        TileStyle.ParseTileColor(SettingsService.Current.DrawerColor);

    /// <summary>渲染/刷新主页下方固定抽屉区域（宽度自适应、高度随内容增长）。
    /// 无抽屉组件、或抽屉被删空时折叠隐藏并清引用，不占高度。</summary>
    private void RenderDrawerPanel()
    {
        // 总开关（设置 → 抽屉）：关闭时与"无抽屉组件"同样折叠隐藏（抽屉内容保留，重开即恢复）
        var owner = SettingsService.Current.DrawerEnabled ? DrawerService.FindDrawer(_items.ToList()) : null;
        if (owner?.Drawers is not { Count: > 0 })
        {
            DrawerHost.Visibility = Visibility.Collapsed;
            DrawerHost.Child = null;
            _drawerRegion = null;
            _drawerInnerCanvas = null;
            _drawerTabStrip = null;
            _drawerInnerTiles.Clear();
            _drawerAddTile = null;
            _drawerSelectedPill = null;
            _drawerOwner = null;
            if (_inDrawer) { _inDrawer = false; _drawerFocusPos = -1; }
            return;
        }

        _drawerOwner = owner;
        if (_drawerIndex >= owner.Drawers.Count) _drawerIndex = owner.Drawers.Count - 1;
        if (_drawerIndex < 0) _drawerIndex = 0;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DrawerTabHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var strip = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // 左内边距 2（+ 标签自身 10 的内边距 = 文字与内容区左对齐于 DrawerPadH）；标签贴基线靠下
            Padding = new Thickness(2, 0, DrawerPadH, 0)
        };
        strip.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(strip, 0);

        // 标签条基线：贴标签条底部、两端留内边距的细分隔线，把抽屉与上方磁贴区分开。
        // 先加入（在标签条之下），让当前标签的强调色下划线能压在这条线上
        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(DrawerPadH, 0, DrawerPadH, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            IsHitTestVisible = false
        };
        Grid.SetRow(divider, 0);
        grid.Children.Add(divider);
        grid.Children.Add(strip);

        var inner = new Canvas
        {
            // 上边距 14：标签条基线与磁贴之间留出呼吸空间，不贴住
            Margin = new Thickness(DrawerPadH, 14, DrawerPadH, DrawerPadV),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(inner, 1);
        grid.Children.Add(inner);

        DrawerHost.Child = grid;
        DrawerHost.Visibility = Visibility.Visible;
        // 面板整体跟随"磁贴透明度"设置淡出（留 0.6 下限：面板是容器底，过淡会看不清标签）
        DrawerHost.Opacity = Math.Max(0.6, TileStyle.Opacity);
        ResetDrawerFrame(); // 面板描边：默认弱描边，或设置里指定的抽屉颜色

        _drawerRegion = DrawerHost;
        _drawerTabStrip = strip;
        _drawerInnerCanvas = inner;

        LayoutDrawerTabStrip(owner);
        LayoutDrawerContent(owner);

        // 自动隐藏：收起状态下重绘后直接把面板摆回窗口下方（无动画），避免重绘瞬间闪现。
        // 首帧 ActualHeight 可能为 0，故延后到本次布局结束再校正一次位置
        if (_drawerHidden && SettingsService.Current.DrawerAutoHide)
        {
            AnimateDrawerSlide(true, animate: false);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_drawerHidden) AnimateDrawerSlide(true, animate: false);
            });
        }
    }

    /// <summary>重建标签条：每个抽屉一个标签（名称 + 当前项强调色下划线），末尾一个"+"。
    /// 观感：常态标签无底色（干净），悬停微亮，当前项用所选颜色淡底 + 贴基线实色下划线（3px 视觉基线）</summary>
    private void LayoutDrawerTabStrip(LaunchItem item)
    {
        if (_drawerTabStrip?.Content is not StackPanel row) return;
        row.Children.Clear();
        _drawerSelectedPill = null;

        var drawers = item.Drawers ?? [];
        // 抽屉控件色：设置里指定了就用它，否则回退到抽屉自身的随机强调色
        var accent = DrawerColorSetting ?? TileStyle.AccentFor(item);
        // 玻璃卡片上的标签：常态无底（只靠文字亮度分层），悬停一层薄高光，当前项=所选颜色较重淡底
        var idleFill = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
        var hoverFill = Windows.UI.Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF);
        var selectedFill = Windows.UI.Color.FromArgb(0x52, accent.R, accent.G, accent.B);
        var transparent = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
        for (var i = 0; i < drawers.Count; i++)
        {
            var idx = i;
            var selected = idx == _drawerIndex;

            // 淡底胶囊：常态透明 / 悬停薄高光 / 当前项=所选颜色较重淡底（玻璃上更醒目）；
            // 用 StackPanel 承载以获得 BrushTransition 背景过渡（Border 无该属性）
            var pill = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(selected ? selectedFill : idleFill),
                BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(140) },
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            pill.Children.Add(new TextBlock
            {
                Text = drawers[i].Name,
                FontSize = selected ? 13.5 : 12.5,
                FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = new SolidColorBrush(selected
                    ? Microsoft.UI.Colors.White
                    : Windows.UI.Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center
            });

            // 当前项下划线：贴标签条基线（压在基线的细分隔线上），3px 加粗提高辨识；
            // 未选中用全透明同厚度保证基线不跳
            var rule = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(selected ? accent : transparent),
                IsHitTestVisible = false
            };

            // 标签容器：给全透明背景才能整块参与命中测试（Grid 无背景时空区不响应点击/悬停）
            var tab = new Grid
            {
                Height = DrawerTabHeight,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)),
                Tag = idx
            };
            tab.Children.Add(pill);
            tab.Children.Add(rule);
            tab.PointerEntered += (_, _) => pill.Background = new SolidColorBrush(hoverFill);
            tab.PointerExited += (_, _) => pill.Background = new SolidColorBrush(selected ? selectedFill : idleFill);
            tab.Tapped += (_, e) => { e.Handled = true; SetDrawerIndex(idx); };
            // 双击标签 = 重命名该抽屉（与抽屉层 R3 同一入口）
            tab.DoubleTapped += (_, e) => { e.Handled = true; SetDrawerIndex(idx); BeginRenameDrawerTab(); };
            row.Children.Add(tab);
            if (selected) _drawerSelectedPill = tab;
        }

        // "+"：新增抽屉（鼠标/触摸直达；手柄用编辑页增删或 X 添加流程）——中性扁平小按钮，不抢标签
        var addFill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
        var add = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = addFill,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE710",
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            }
        };
        add.PointerEntered += (_, _) => addFill.Color = Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        add.PointerExited += (_, _) => addFill.Color = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
        add.Tapped += (_, e) => { e.Handled = true; AddDrawerTab(); };
        add.DoubleTapped += (_, e) => e.Handled = true; // 双击"+"不冒泡到面板（避免误弹重命名遮罩）
        row.Children.Add(add);
    }

    /// <summary>按当前抽屉重绘内层磁贴：内容区列数随面板宽度自适应，贪心装箱，整块水平居中；面板高度随内容增长。</summary>
    private void LayoutDrawerContent(LaunchItem item)
    {
        if (_drawerInnerCanvas is not { } canvas) return;
        canvas.Children.Clear();
        _drawerInnerTiles.Clear();
        _drawerAddTile = null;

        var drawers = item.Drawers ?? [];
        if (drawers.Count == 0) return;
        _drawerIndex = Math.Clamp(_drawerIndex, 0, drawers.Count - 1);
        var page = drawers[_drawerIndex];

        // 内容区可用宽度：面板宽度显式指定时（全屏/居中：窗口宽度的 80%）优先用它——避免布局尚未跑完
        // ActualWidth 仍是旧宽度导致列数按上一形态算；未指定（贴边停靠铺满）时取实测宽度
        var panelW = !double.IsNaN(DrawerHost.Width) && DrawerHost.Width > 0
            ? DrawerHost.Width
            : DrawerHost.ActualWidth > 0 ? DrawerHost.ActualWidth : Math.Max(0, TileArea.ActualWidth - 24);
        var contentW = Math.Max(Unit, panelW - DrawerPadH * 2 - 2);
        // 抽屉内层磁贴与主页磁贴同尺寸（Unit），列数按与主页相同的公式由内容区宽度决定——
        // 不再为了多塞一列而缩小磁贴（内层磁贴看起来"默认小"的根因）
        var cols = Math.Max(1, (int)((contentW + Gap) / (Unit + Gap)));
        var unit = Unit;
        var pitch = unit + Gap;
        // "统一磁贴大小"：开启后抽屉内所有磁贴忽略自身大小，一律按 1×1 小磁贴排布与渲染
        var uniformSmall = SettingsService.Current.DrawerUniformSmall;

        // 贪心装箱（与主页同一套 Fits 规则）；放不下（组件改小后遗留）的跳过，由编辑页处理
        var occ = new HashSet<(int, int)>();
        var boxes = new List<(LaunchItem Sub, int C, int R, int W, int H)>();
        foreach (var sub in page.Items)
        {
            var (w, h) = uniformSmall ? (1, 1) : InnerCellsOf(sub);
            if (w > cols) continue; // 单块宽度超过内容区：跳过，避免死循环
            var c = -1;
            var r = -1;
            for (var rr = 0; rr < 1000 && c < 0; rr++)
                for (var cc = 0; cc + w <= cols && c < 0; cc++)
                    if (Fits(occ, cc, rr, w, h)) { c = cc; r = rr; }
            if (c < 0) continue;
            for (var dy = 0; dy < h; dy++)
                for (var dx = 0; dx < w; dx++) occ.Add((c + dx, r + dy));
            boxes.Add((sub, c, r, w, h));
        }

        // 编辑模式：在最后一个空位补一个"添加磁贴"（1×1，与主页编辑模式的加号磁贴同一入口）。
        // 抽屉层 X 在编辑模式下让位给"移入标记"，这个加号磁贴就是鼠标/手柄往当前抽屉添加磁贴的入口。
        (int C, int R)? addSpot = null;
        if (_moving)
        {
            for (var rr = 0; addSpot is null && rr < 1000; rr++)
                for (var cc = 0; cc < cols && addSpot is null; cc++)
                    if (Fits(occ, cc, rr, 1, 1)) addSpot = (cc, rr);
            if (addSpot is { } asp) occ.Add((asp.C, asp.R));
        }

        // 行数取内容实际占用（空抽屉占 1 行用于显示提示），面板高度随之增长
        var rows = boxes.Count == 0 ? 1 : boxes.Max(b => b.R + b.H);
        var usedW = boxes.Count == 0 ? 0 : boxes.Max(b => b.C + b.W) * pitch - Gap;
        if (addSpot is { } sp)
        {
            rows = Math.Max(rows, sp.R + 1);
            usedW = Math.Max(usedW, (sp.C + 1) * pitch - Gap);
        }
        var canvasH = Math.Max(unit, rows * pitch - Gap);
        var offsetX = Math.Max(0, (contentW - usedW) / 2);
        canvas.Height = canvasH;

        if (boxes.Count == 0 && addSpot is null)
        {
            canvas.Children.Add(BuildDrawerHint($"「{page.Name}」为空", "按 X 添加磁贴", contentW, canvasH));
            return;
        }

        for (var k = 0; k < boxes.Count; k++)
        {
            var b = boxes[k];
            var w = b.W * unit + (b.W - 1) * Gap;
            var h = b.H * unit + (b.H - 1) * Gap;
            Border tile;
            // 系统小组件在抽屉里也用专属磁贴（普通磁贴只有名称，看不出当前面板项/读数），
            // 并登记到 _sysRefs，右摇杆切换时才能与主页磁贴一样刷新 + 播滑入动画
            if (b.Sub.Kind == LaunchKind.Widget && b.Sub.WidgetKind == WidgetKind.System)
            {
                var (sysTile, sysRefs) = TileStyle.BuildSystemTile(
                    b.Sub, w, h, TileStyle.AccentFor(b.Sub), TileStyle.IsNoColor(b.Sub));
                TileStyle.FillSystemTile(sysRefs, b.Sub);
                _sysRefs[sysRefs] = b.Sub;
                tile = sysTile;
            }
            else
            {
                tile = TileStyle.BuildStandardTile(b.Sub, null, w, h, TileStyle.AccentFor(b.Sub), TileStyle.IsNoColor(b.Sub), uniformSmall);
            }
            tile.Tag = b.Sub; // 焦点查找用：内层磁贴 ↔ 磁贴项
            Canvas.SetLeft(tile, offsetX + b.C * pitch);
            Canvas.SetTop(tile, b.R * pitch);
            var kk = k;
            var sub = b.Sub;
            // 点击/触摸内层磁贴：聚焦并触发；e.Handled 防冒泡到面板（否则会重复进入区域）
            tile.Tapped += (_, e) =>
            {
                e.Handled = true;
                if (_suppressTap) { _suppressTap = false; return; } // 拖拽结束后的落点不再触发启动
                FocusDrawerItem(kk);
                // 与主页磁贴同一套轻点语义：延迟一个系统双击时长再启动，双击=进编辑页（可改大小/名称/图标）
                DeferTap(sub, () => ActivateDrawerItem(sub), () => HandleDoubleTapOnDrawer(sub));
            };
            // 双击内层磁贴=进编辑页（等同 R3）；吞掉 DoubleTapped 不让它冒泡到面板（否则会弹出"重命名当前抽屉"遮罩）
            tile.DoubleTapped += (_, e) => { e.Handled = true; HandleDoubleTapOnDrawer(sub); };
            AttachInnerTileDrag(tile, sub); // 拖出到磁贴区=变主页磁贴；拖到设置磁贴=删除
            canvas.Children.Add(tile);
            _drawerInnerTiles.Add(tile);
        }

        // 编辑模式的"添加磁贴"（最后追加，保证手柄焦点序号在末位；无 Tag = 不是真实磁贴）：
        // 点击或聚焦后按 A = 弹出添加方式选择，新磁贴直接落进当前抽屉
        if (addSpot is { } spot)
        {
            var ki = _drawerInnerTiles.Count;
            var add = CreateAddTile();
            add.Width = unit;
            add.Height = unit;
            if (FindIcon(add) is { } addIcon) addIcon.FontSize = Math.Round(unit * 0.34);
            Canvas.SetLeft(add, offsetX + spot.C * pitch);
            Canvas.SetTop(add, spot.R * pitch);
            add.Tapped += (_, e) =>
            {
                e.Handled = true;
                FocusDrawerItem(ki);
                BeginAddIntoDrawer();
            };
            add.DoubleTapped += (_, e) => e.Handled = true; // 双击加号磁贴不冒泡到面板（避免弹出重命名遮罩）
            canvas.Children.Add(add);
            _drawerInnerTiles.Add(add);
            _drawerAddTile = add;
        }
    }

    /// <summary>内层磁贴拖拽（鼠标/触摸，与主页磁贴共用同一套拖拽状态字段——同时只会有一处拖拽）：
    /// 拖出到磁贴区 = 移出抽屉变主页磁贴（互通）；拖到设置磁贴（垃圾桶）= 删除；拖回面板内 = 原样放回。</summary>
    private void AttachInnerTileDrag(Border tile, LaunchItem sub)
    {
        tile.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (_dragItem is not null) return; // 已有拖拽进行中
            _dragItem = sub;
            _dragTile = tile;
            _dragStart = e.GetCurrentPoint(TileHost).Position;
            _dragPointerId = e.Pointer.PointerId;
            _dragMoved = false;
            tile.CapturePointer(e.Pointer);
        }), true);

        tile.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (_dragTile is null || !ReferenceEquals(_dragTile, tile) || e.Pointer.PointerId != _dragPointerId) return;
            var p = e.GetCurrentPoint(TileHost).Position;
            if (!_dragMoved && Dist(p, _dragStart) < 10) return; // 未超过阈值仍是轻点
            _dragMoved = true;
            tile.RenderTransform = new TranslateTransform { X = p.X - _dragStart.X, Y = p.Y - _dragStart.Y };
            Canvas.SetZIndex(tile, 10);
            tile.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD3, 0x4D));
            tile.BorderThickness = new Thickness(3);
            // 悬停在设置磁贴上：变垃圾桶（松手即删掉这个内层磁贴）
            _dragOverTrash = _settingsTile is not null && PointInTile(p, _settingsTile);
            RefreshSettingsBadge();
        }), true);

        tile.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            if (_dragTile is null || !ReferenceEquals(_dragTile, tile) || e.Pointer.PointerId != _dragPointerId) return;
            var p = e.GetCurrentPoint(TileHost).Position;
            var overTrash = _dragOverTrash;
            var moved = _dragMoved;
            _dragItem = null;
            _dragTile = null;
            _dragMoved = false;
            _dragOverTrash = false;
            RefreshSettingsBadge();
            if (!moved) return;
            _suppressTap = true;
            if (overTrash) { DeleteInnerTile(sub); return; }
            // 拖出面板且落在磁贴区内 = 移出抽屉回主页网格；其它位置原样放回（抽屉内暂不支持重排）
            var inTileArea = p.X >= 0 && p.Y >= 0 && p.X <= TileHost.ActualWidth && p.Y <= TileHost.ActualHeight;
            if (!PointInDrawer(p) && inTileArea) MoveDrawerItemToHome(sub, p);
            else RevertInnerTileDrag(tile);
        }), true);

        tile.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
        {
            if (_dragTile is null || !ReferenceEquals(_dragTile, tile)) return;
            var moved = _dragMoved;
            _dragItem = null;
            _dragTile = null;
            _dragMoved = false;
            if (moved) RevertInnerTileDrag(tile);
        }), true);
    }

    /// <summary>拖拽没落到目标：内层磁贴外观复位（边框/描边/ZIndex 交选中状态统一重刷）</summary>
    private void RevertInnerTileDrag(Border tile)
    {
        tile.RenderTransform = null;
        UpdateDrawerSelection();
    }

    /// <summary>内层磁贴拖出到主页：移出当前抽屉 → 成为主页顶层磁贴，并落到落点最近的位置（主页 ↔ 抽屉互通）</summary>
    private void MoveDrawerItemToHome(LaunchItem sub, Windows.Foundation.Point p)
    {
        var page = CurrentDrawerPage();
        if (page is null || !page.Items.Remove(sub)) return;
        sub.PageIndex = App.Main?.CurrentHomePage ?? 0;
        _items.Add(sub);
        PersistLayout();
        BuildDisplay();
        LayoutTiles();
        _drawerFocusPos = Math.Clamp(_drawerFocusPos, -1, Math.Max(-1, page.Items.Count - 1));
        SwapToNearest(sub, p); // 落到落点附近（与最近顶层磁贴换位；内部含落盘与重绘）
        UpdateSelection();
        if (_moving) UpdateHintBar();
        ShowToast(string.IsNullOrWhiteSpace(sub.Name) ? "已移出抽屉" : $"已把 {sub.Name} 移出抽屉");
    }

    /// <summary>删除当前抽屉里指定的内层磁贴（拖到垃圾桶用；编辑模式内层 Y 走 DeleteDrawerItem）</summary>
    private void DeleteInnerTile(LaunchItem sub)
    {
        var page = CurrentDrawerPage();
        if (page is null || !page.Items.Remove(sub)) return;
        PersistLayout();
        _drawerFocusPos = Math.Clamp(_drawerFocusPos, -1, Math.Max(-1, page.Items.Count - 1));
        RenderDrawerPanel();
        UpdateSelection();
        if (_moving) UpdateHintBar();
        ShowToast(string.IsNullOrWhiteSpace(sub.Name) ? "已删除磁贴" : $"已删除 {sub.Name}");
    }

    /// <summary>空抽屉提示：内容区中部的抽屉图标 + 两行淡色文字（不拦截命中）</summary>
    private static StackPanel BuildDrawerHint(string title, string sub, double contentW, double canvasH)
    {
        var hint = new StackPanel
        {
            Width = contentW,
            Spacing = 6,
            IsHitTestVisible = false
        };
        hint.Children.Add(new FontIcon
        {
            Glyph = "\uE8F1", // 抽屉图标
            FontSize = 20,
            Opacity = 0.4,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        hint.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12.5, Opacity = 0.7,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center
        });
        if (!string.IsNullOrEmpty(sub))
            hint.Children.Add(new TextBlock
            {
                Text = sub, FontSize = 11, Opacity = 0.45,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center
            });

        Canvas.SetLeft(hint, 0);
        Canvas.SetTop(hint, Math.Max(0, canvasH / 2 - 32));
        return hint;
    }

    /// <summary>抽屉内磁贴占格（音乐固定 2×2、系统固定 2×1，与主页一致）</summary>
    private static (int Cols, int Rows) InnerCellsOf(LaunchItem it)
        => it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.Music ? (2, 2)
         : it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.System ? (2, 1)
         : CellsOf(it.Size);

    /// <summary>进入抽屉区域：选中框停在"抽屉本身"（抽屉层），此时按键只作用于抽屉；按 A 才进入内层磁贴。无抽屉组件时不做任何事。</summary>
    private void EnterDrawer()
    {
        if (!SettingsService.Current.DrawerEnabled) return;   // 抽屉总开关关闭时不进入
        if (_drawerHidden) return;                            // 自动隐藏已收起：面板在屏幕外，先呼出再进入
        var owner = _drawerOwner ?? DrawerService.FindDrawer(_items.ToList());
        if (owner?.Drawers is not { Count: > 0 }) return;
        _inDrawer = true;
        _drawerOwner = owner;
        _drawerIndex = Math.Clamp(_drawerIndex, 0, owner.Drawers.Count - 1);
        _drawerFocusPos = -1; // 抽屉层：焦点不在任何内层磁贴上
        UpdateSelection();
        if (_moving) UpdateHintBar(); // 编辑模式内进入区域：提示条切成抽屉操作
    }

    /// <summary>退出抽屉区域：焦点回到磁贴区（_selected 保持不变）</summary>
    private void ExitDrawer()
    {
        if (!_inDrawer) return;
        _inDrawer = false;
        _drawerFocusPos = -1;
        UpdateSelection();
        if (_moving) UpdateHintBar(); // 编辑模式内退出区域：提示条恢复编辑提示
    }

    /// <summary>抽屉层 → 内层：焦点落到当前抽屉的第一个内层磁贴（空抽屉则提示，留在抽屉层）</summary>
    private void EnterDrawerInner()
    {
        if (_drawerInnerTiles.Count == 0)
        {
            var name = CurrentDrawerPage()?.Name;
            ShowToast(string.IsNullOrWhiteSpace(name) ? "抽屉是空的 · 按 X 添加磁贴" : $"「{name}」是空的 · 按 X 添加磁贴", ToastKind.Warning);
            return;
        }
        _drawerFocusPos = 0;
        UpdateSelection();
        if (_moving) UpdateHintBar(); else UpdateHintForSelection();
    }

    /// <summary>内层 → 抽屉层：焦点收回抽屉本身（再按 B 才退出到磁贴区）</summary>
    private void BackToDrawerLevel()
    {
        if (!_inDrawer || _drawerFocusPos < 0) return;
        _drawerFocusPos = -1;
        UpdateSelection();
        if (_moving) UpdateHintBar(); else UpdateHintForSelection();
    }

    /// <summary>LB/RB 或 ←→ 换抽屉（环形）：保持当前层级——在内层则落到新抽屉首项（新抽屉为空则退回抽屉层）</summary>
    private void CycleDrawer(int dir)
    {
        var owner = _drawerOwner ?? DrawerService.FindDrawer(_items.ToList());
        if (owner?.Drawers is not { Count: > 0 } ds) return;
        var keepInner = _drawerFocusPos >= 0;
        _drawerIndex = ((_drawerIndex + dir) % ds.Count + ds.Count) % ds.Count;
        _drawerFocusPos = keepInner && ds[_drawerIndex].Items.Count > 0 ? 0 : -1;
        LayoutDrawerTabStrip(owner);
        LayoutDrawerContent(owner);
        UpdateSelection();
        BringDrawerPillIntoView();
    }

    /// <summary>切到指定抽屉（点击标签条药丸）：保持当前层级</summary>
    private void SetDrawerIndex(int i)
    {
        var owner = _drawerOwner ?? DrawerService.FindDrawer(_items.ToList());
        if (owner?.Drawers is not { Count: > 0 } ds) return;
        var keepInner = _drawerFocusPos >= 0;
        _drawerIndex = Math.Clamp(i, 0, ds.Count - 1);
        _drawerFocusPos = keepInner && ds[_drawerIndex].Items.Count > 0 ? 0 : -1;
        LayoutDrawerTabStrip(owner);
        LayoutDrawerContent(owner);
        UpdateSelection();
        BringDrawerPillIntoView();
    }

    /// <summary>让当前高亮的抽屉药丸滚入可见（布局完成后再执行）</summary>
    private void BringDrawerPillIntoView()
    {
        if (_drawerSelectedPill is not { } pill) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            try { pill.StartBringIntoView(); } catch { /* 脱离可视树时忽略 */ }
        });
    }

    /// <summary>标签条 "+"：给组件新增一个空抽屉（最多 8 个），名称取默认「抽屉N」</summary>
    private void AddDrawerTab()
    {
        var owner = _drawerOwner ?? DrawerService.FindDrawer(_items.ToList());
        if (owner is null) return;
        owner.Drawers ??= [];
        if (owner.Drawers.Count >= 8) { ShowToast("抽屉最多 8 个", ToastKind.Warning); return; }
        owner.Drawers.Add(new DrawerPage { Name = DrawerService.NextName(owner.Drawers) });
        _drawerIndex = owner.Drawers.Count - 1;
        _drawerFocusPos = -1;
        PersistLayout();
        RenderDrawerPanel();
        UpdateSelection();
        BringDrawerPillIntoView();
        ShowToast($"已新增「{owner.Drawers[_drawerIndex].Name}」");
    }

    // ---- 抽屉改名遮罩（抽屉层 R3 / 双击抽屉标签：只改"当前抽屉"的名字）----

    private bool _renaming;

    /// <summary>打开"重命名当前抽屉"遮罩</summary>
    private void BeginRenameDrawerTab()
    {
        var page = CurrentDrawerPage();
        if (page is null) return;
        _renaming = true;
        RenameTitle.Text = $"重命名「{page.Name}」";
        RenameBox.Text = page.Name;
        RenameOverlay.Visibility = Visibility.Visible;
        RenameBox.Focus(FocusState.Programmatic);
        RenameBox.SelectAll();
    }

    /// <summary>确认改名：写回当前抽屉并重建标签条；空名/重名则提示并保持遮罩开启</summary>
    private void CommitRenameDrawerTab()
    {
        var page = CurrentDrawerPage();
        if (page is null) { CloseRename(); return; }
        var name = RenameBox.Text.Trim();
        if (name.Length == 0) { ShowToast("抽屉名不能为空", ToastKind.Warning); return; }
        if (_drawerOwner?.Drawers is { } ds && ds.Any(d => !ReferenceEquals(d, page) && d.Name == name))
        {
            ShowToast($"已有同名抽屉「{name}」", ToastKind.Warning);
            return;
        }
        page.Name = name;
        CloseRename();
        PersistLayout();
        RenderDrawerPanel();
        UpdateSelection();
        BringDrawerPillIntoView();
        ShowToast($"已重命名为「{name}」");
    }

    private void CancelRenameDrawerTab() => CloseRename();

    private void CloseRename()
    {
        _renaming = false;
        RenameOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>改名遮罩内手柄按键：A=确认，B=取消，其余吞掉（模态，避免误操作到主页）</summary>
    private bool HandleRename(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.A)) { CommitRenameDrawerTab(); return true; }
        if (b.HasFlag(GamepadButtons.B)) { CancelRenameDrawerTab(); return true; }
        return true;
    }

    /// <summary>改名输入框键盘：Enter=确认，Esc=取消（键盘与手柄共用同一入口）</summary>
    private void OnRenameBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { CommitRenameDrawerTab(); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Escape) { CancelRenameDrawerTab(); e.Handled = true; }
    }

    /// <summary>区域内部方向键：在内层磁贴间做最近邻选择。返回 true=发生了移动（用于"最上行 ↑ 退出"）。</summary>
    private bool MoveInDrawer(int dx, int dy)
    {
        if (_drawerInnerTiles.Count == 0) return false;
        if (_drawerFocusPos < 0 || _drawerFocusPos >= _drawerInnerTiles.Count) { FocusDrawerItem(0); return true; }
        var cur = _drawerFocusPos;
        var (cx, cy) = Center(_drawerInnerTiles[cur]);
        int best = -1;
        var bestScore = double.MaxValue;
        for (var i = 0; i < _drawerInnerTiles.Count; i++)
        {
            if (i == cur) continue;
            var (x, y) = Center(_drawerInnerTiles[i]);
            var along = dx != 0 ? (x - cx) * dx : (y - cy) * dy;
            if (along <= 1) continue;
            var cross = dx != 0 ? Math.Abs(y - cy) : Math.Abs(x - cx);
            var score = cross * 10 + along;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best >= 0) { FocusDrawerItem(best); return true; }
        return false;
    }

    /// <summary>设置内层焦点序号</summary>
    private void FocusDrawerItem(int pos)
    {
        if (pos < 0 || pos >= _drawerInnerTiles.Count) return;
        _drawerFocusPos = pos;
        UpdateDrawerSelection();
    }

    /// <summary>区域内 A：启动/触发当前聚焦的内层磁贴</summary>
    private void ActivateDrawerFocus()
    {
        var item = FocusedDrawerItem();
        if (item is null) { ShowToast("这个抽屉是空的 · 按 X 添加磁贴", ToastKind.Warning); return; }
        ActivateDrawerItem(item);
    }

    private void ActivateDrawerItem(LaunchItem item)
    {
        if (item.Kind == LaunchKind.Widget) ActivateWidgetItem(item);
        else LaunchItem(item);
    }

    /// <summary>当前聚焦的内层磁贴（无则 null）</summary>
    private LaunchItem? FocusedDrawerItem()
        => _drawerFocusPos >= 0 && _drawerFocusPos < _drawerInnerTiles.Count
            ? _drawerInnerTiles[_drawerFocusPos].Tag as LaunchItem
            : null;

    /// <summary>当前焦点是否停在编辑模式的"添加磁贴"上（它不是真实磁贴，Tag 为空）</summary>
    private bool FocusedDrawerIsAddTile()
        => _drawerAddTile is not null && _drawerFocusPos >= 0 && _drawerFocusPos < _drawerInnerTiles.Count
           && ReferenceEquals(_drawerInnerTiles[_drawerFocusPos], _drawerAddTile);

    /// <summary>区域内 X：走现有添加流程，新磁贴直接放进当前抽屉（挂起一次性请求）</summary>
    private void BeginAddIntoDrawer()
    {
        if (_drawerOwner is not { } owner || string.IsNullOrEmpty(owner.Target)) return;
        if (App.Main is { } main) main.PendingDrawerInsert = (owner.Target, _drawerIndex);
        OpenAddChoice();
    }

    /// <summary>当前抽屉（下拉列表项；无则 null）</summary>
    private DrawerPage? CurrentDrawerPage()
    {
        var ds = _drawerOwner?.Drawers;
        if (ds is null || ds.Count == 0) return null;
        return ds[Math.Clamp(_drawerIndex, 0, ds.Count - 1)];
    }

    /// <summary>编辑模式内 Y：内层=删除当前聚焦的内层磁贴；抽屉层=删除当前抽屉，
    /// 最后一个抽屉删完则连带删除整个抽屉组件（组件不在磁贴网格中，这里也组件删除的唯一入口）</summary>
    private void DeleteDrawerItem()
    {
        var owner = _drawerOwner ?? DrawerService.FindDrawer(_items.ToList());
        if (owner?.Drawers is not { Count: > 0 } ds) return;
        var page = CurrentDrawerPage();
        if (page is null) return;

        // 内层磁贴层：删除当前聚焦的内层磁贴
        if (FocusedDrawerItem() is { } item)
        {
            page.Items.Remove(item);
            PersistLayout();
            _drawerFocusPos = Math.Clamp(_drawerFocusPos, -1, Math.Max(-1, page.Items.Count - 1));
            RenderDrawerPanel();
            UpdateSelection();
            if (_moving) UpdateHintBar();
            ShowToast(string.IsNullOrWhiteSpace(item.Name) ? "已删除磁贴" : $"已删除 {item.Name}");
            return;
        }

        // 焦点停在编辑模式的"添加磁贴"上：它不是真实磁贴，Y 不动抽屉（否则会误删整个抽屉）
        if (FocusedDrawerIsAddTile()) return;

        // 抽屉层：只剩一个抽屉时删掉它即等于删除整个组件
        if (ds.Count <= 1)
        {
            _inDrawer = false;
            _drawerFocusPos = -1;
            DeleteItem(owner, null);
            ShowToast("已移除磁贴抽屉");
            return;
        }

        var name = page.Name;
        var idx = Math.Clamp(_drawerIndex, 0, ds.Count - 1);
        ds.RemoveAt(idx);
        _drawerIndex = Math.Clamp(idx, 0, ds.Count - 1);
        _drawerFocusPos = -1; // 焦点留在抽屉层（新抽屉上）
        PersistLayout();
        RenderDrawerPanel();
        UpdateSelection();
        if (_moving) UpdateHintBar();
        BringDrawerPillIntoView();
        ShowToast(string.IsNullOrWhiteSpace(name) ? "已删除抽屉" : $"已删除「{name}」");
    }

    /// <summary>编辑模式：把已标记的磁贴移入当前抽屉（高度随内容增长，总能放下）</summary>
    private void MoveMarkedIntoDrawer(int tab)
    {
        var owner = _drawerOwner ?? DrawerService.FindDrawer(_items.ToList());
        if (owner?.Drawers is not { Count: > 0 } ds) return;
        tab = Math.Clamp(tab, 0, ds.Count - 1);
        var page = ds[tab];

        var moved = 0;
        foreach (var it in _marked.ToList())
        {
            if (DrawerService.IsDrawer(it)) continue;
            _items.Remove(it);
            page.Items.Add(it);
            moved++;
        }
        _marked.Clear();

        PersistLayout();
        BuildDisplay();
        LayoutTiles();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _display.Count - 1));
        UpdateSelection();
        if (_moving) UpdateHintBar();
        ShowToast($"已移入「{page.Name}」{moved} 个磁贴");
    }

    /// <summary>内层磁贴焦点描边 + 面板"在区域内"强调色（仅在区域内顶点亮；拖拽悬停高亮期间不覆盖）</summary>
    private void UpdateDrawerSelection()
    {
        for (var i = 0; i < _drawerInnerTiles.Count; i++)
        {
            var t = _drawerInnerTiles[i];
            // 未进入区域时不点亮内层磁贴：焦点在磁贴区时内层不该"看起来被选中"
            var focused = _inDrawer && i == _drawerFocusPos;
            var isAdd = ReferenceEquals(t, _drawerAddTile); // 编辑模式的"添加磁贴"：失焦时保留强调色描边
            t.BorderThickness = new Thickness(focused ? 4 : isAdd ? 1.5 : 1);
            t.BorderBrush = new SolidColorBrush(focused
                ? Microsoft.UI.Colors.White
                : isAdd
                    ? Windows.UI.Color.FromArgb(0x66, 0x4C, 0xC2, 0xFF)
                    : Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(t, focused ? 2 : 0);
        }
        // 面板描边：焦点在区域内=强调色；否则恢复默认（拖拽悬停高亮期间不覆盖）
        if (_dragOverDrawer || _drawerRegion is not { } shell) return;
        if (_inDrawer && _drawerOwner is { } owner)
        {
            // 焦点环：焦点进入抽屉层时描边换成实色强调色（玻璃卡片上的"当前操作对象"提示）
            shell.BorderThickness = new Thickness(2);
            shell.BorderBrush = new SolidColorBrush(DrawerColorSetting ?? TileStyle.AccentFor(owner));
        }
        else
        {
            ResetDrawerFrame();
        }
    }

    /// <summary>区域描边复位：玻璃浮层的常态 1px 高光描边（焦点进入抽屉层时由 UpdateDrawerSelection 换强调色）</summary>
    private void ResetDrawerFrame()
    {
        if (_drawerRegion is { } shell)
        {
            shell.BorderThickness = new Thickness(1);
            shell.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>拖拽悬停高亮复位：面板描边恢复默认</summary>
    private void ResetDrawerHover()
    {
        _dragOverDrawer = false;
        ResetDrawerFrame();
    }

    // ---- 磁贴轻点/双击：单击延迟"系统双击时长"（默认 500ms）执行（启动/切图），
    // 窗口内再点同一磁贴或事件 TapCount≥2 = 双击进编辑（等同 R3）----
    private Microsoft.UI.Xaml.DispatcherTimer? _tapTimer;
    /// <summary>挂起中单击动作的目标标识（主页=磁贴序号，抽屉内层=LaunchItem 本身）</summary>
    private object? _pendingTapKey = -1;
    private Action? _pendingAction;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private DateTime _lastDoubleAt = DateTime.MinValue;

    private void OnTileTapped(int index, Action? singleAction)
    {
        if (_suppressTap) { _suppressTap = false; return; }
        if (_moving) return; // 编辑模式：选择/拖拽由指针手势处理
        _selected = index;
        UpdateSelection();
        DeferTap(index, singleAction, () => HandleDoubleTap(index));
    }

    /// <summary>轻点统一入口：延迟一个系统双击时长（默认 500ms）再执行单击动作；
    /// 双击时长内再点同一目标=双击，取消挂起的单击改用 doubleAction。
    /// 主页磁贴与抽屉内层磁贴共用，保证两者"单击启动、双击进编辑"语义一致。</summary>
    private void DeferTap(object key, Action? singleAction, Action doubleAction)
    {
        // 双击的伴随事件（Tapped/DoubleTapped 先后顺序不定）：已处理则不再排程单击动作
        if ((DateTime.Now - _lastDoubleAt).TotalMilliseconds < 100) return;

        // 双击时长内再点同一目标 = 双击：HandleDoubleTap 内部会取消挂起的单击动作
        if (_tapTimer is { IsEnabled: true } && Equals(_pendingTapKey, key))
        {
            doubleAction();
            return;
        }

        _pendingTapKey = key;
        _pendingAction = singleAction;
        if (_tapTimer == null)
        {
            // 与系统"双击速度"一致（用户可在鼠标设置里调整），兜底不低于 400ms
            var ms = Math.Max(400, (int)GetDoubleClickTime());
            _tapTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            _tapTimer.Tick += (_, _) =>
            {
                _tapTimer!.Stop();
                _pendingTapKey = -1;
                var act = _pendingAction;
                _pendingAction = null;
                act?.Invoke();
            };
        }
        _tapTimer.Stop();
        _tapTimer.Start();
    }

    /// <summary>取消挂起的单击动作（双击已接管时调用）</summary>
    private void CancelPendingTap()
    {
        _tapTimer?.Stop();
        _pendingTapKey = -1;
        _pendingAction = null;
    }

    /// <summary>双击磁贴处理：取消待执行的单击动作并进编辑页；Tapped 与 DoubleTapped 双通道触发，100ms 内去重</summary>
    private void HandleDoubleTap(int index)
    {
        if (_moving) return;
        if ((DateTime.Now - _lastDoubleAt).TotalMilliseconds < 100) return;
        _lastDoubleAt = DateTime.Now;
        CancelPendingTap();
        // 双击的第二次"按下"已启动 600ms 长按计时并 CapturePointer；切页后磁贴脱离可视树，
        // 第二次 PointerReleased 不再送达 → 计时器不会被取消，会在后台误触发关闭/进入编辑。导航前先取消。
        _lpTimer?.Stop();
        _lpTile = null;
        _lpCloseItem = null;
        _lpSyncItem = null;
        _lpOptiItem = null;
        OpenEditorAt(index);
    }

    /// <summary>双击磁贴：进入编辑快捷项页（等同 R3）；组成员不支持直接编辑（与 R3 一致）</summary>
    private void OpenEditorAt(int index)
    {
        if (index < 0 || index >= _display.Count) return;
        var (item, group) = _display[index];
        if (group is not null) return;
        App.Main?.NavigateEditItem(item);
    }

    /// <summary>双击抽屉内层磁贴：进编辑页（等同 R3）——据此可改磁贴大小/名称/图标。
    /// 先取消挂起的启动动作，避免"先启动程序再打开编辑页"。</summary>
    private void HandleDoubleTapOnDrawer(LaunchItem sub)
    {
        if ((DateTime.Now - _lastDoubleAt).TotalMilliseconds < 100) return;
        _lastDoubleAt = DateTime.Now;
        CancelPendingTap();
        App.Main?.NavigateEditItem(sub);
    }

    /// <summary>给磁贴挂编辑手势：普通模式下长按设置磁贴=进入编辑模式；
    /// 编辑模式内按住拖动=移动磁贴（放到别的磁贴/空位=交换位置，放到设置磁贴（垃圾桶）上=删除）；
    /// 编辑模式内轻点磁贴=标记选中，轻点设置磁贴：标记≥2=组合成磁贴组（加号），否则=退出编辑模式。</summary>
    private void AttachEditGestures(Border tile, LaunchItem item, LaunchItem? group, int index, bool isSettings)
    {
        tile.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            _suppressTap = false;
            if (_choiceOpen) return;
            var p = e.GetCurrentPoint(TileHost).Position;

            // 普通模式长按：设置磁贴=进入编辑模式；程序/网站/文件夹磁贴=关闭其前台窗口（等同 Y 键）；
            // 修改器小组件磁贴=关闭正在运行的修改器（等同 Y 键）；游戏存档小组件磁贴=同步存档（等同 Y 键）；
            // 手柄映射小组件磁贴=关闭映射（等同长按关闭）；OptiScaler 小组件磁贴=注入（等同 Y 键）；
            // 前台应用磁贴=强制结束当前前台应用（等同 Y 键）
            if (!_moving && group is null &&
                (isSettings || item.Kind is LaunchKind.App or LaunchKind.Url or LaunchKind.Folder
                 || (item.Kind == LaunchKind.Widget && item.WidgetKind is WidgetKind.Trainer or WidgetKind.Backup or WidgetKind.GamepadMap or WidgetKind.OptiScaler or WidgetKind.Foreground)))
            {
                _lpTimer ??= new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _lpTimer.Stop();
                _lpTimer.Tick -= OnLongPressTick;
                _lpTimer.Tick += OnLongPressTick;
                _lpTile = tile;
                var isBackup = item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Backup;
                var isRemap = item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.GamepadMap;
                var isOpti = item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.OptiScaler;
                _lpCloseItem = isSettings || isBackup || isRemap || isOpti ? null : item;
                _lpSyncItem = isBackup ? item : null;
                _lpRemapItem = isRemap ? item : null;
                _lpOptiItem = isOpti ? item : null;
                _lpStart = p;
                _lpPointerId = e.Pointer.PointerId;
                tile.CapturePointer(e.Pointer);
                _lpTimer.Start();
                return;
            }

            if (!_moving || _dragItem is not null) return;
            if (group is not null)
            {
                // 组成员不可拖拽：轻点仅选中（便于 Y 键移出组）
                _selected = index;
                UpdateSelection();
                return;
            }
            _dragItem = item;
            _dragTile = tile;
            _dragStart = p;
            _dragPointerId = e.Pointer.PointerId;
            _dragMoved = false;
            _selected = index;
            tile.CapturePointer(e.Pointer);
            e.Handled = true; // 吞掉按下事件：拖拽期间不触发 ScrollViewer 触摸滚动
            UpdateSelection();
        }), true);

        tile.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            var p = e.GetCurrentPoint(TileHost).Position;
            if (_lpTile is not null && e.Pointer.PointerId == _lpPointerId)
            {
                if (Dist(p, _lpStart) > 12) { _lpTimer?.Stop(); _lpTile = null; _lpCloseItem = null; _lpSyncItem = null; _lpRemapItem = null; _lpOptiItem = null; } // 移走了=取消长按
                return;
            }
            if (_dragTile is null || !ReferenceEquals(_dragTile, tile) || e.Pointer.PointerId != _dragPointerId) return;
            if (!_dragMoved && Dist(p, _dragStart) < 10) return; // 未超过阈值仍是轻点
            _dragMoved = true;
            tile.RenderTransform = new TranslateTransform { X = p.X - _dragStart.X, Y = p.Y - _dragStart.Y };
            Canvas.SetZIndex(tile, 10);
            tile.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD3, 0x4D));
            tile.BorderThickness = new Thickness(3);
            // 悬停在设置磁贴上：变垃圾桶（松手即删除）；否则按标记数显示加号/普通
            _dragOverTrash = _settingsTile is not null && !ReferenceEquals(_settingsTile, tile) &&
                             PointInTile(p, _settingsTile);
            // 悬停在抽屉区域上：高亮面板（松手即放入当前抽屉）；抽屉固定在下方，用坐标换算命中
            _dragOverDrawer = !_dragOverTrash && _drawerRegion is not null &&
                              !DrawerService.IsDrawer(item) &&
                              PointInDrawer(p);
            RefreshSettingsBadge();
            if (_drawerRegion is { } hoverShell)
            {
                if (_dragOverDrawer)
                {
                    hoverShell.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
                    hoverShell.BorderThickness = new Thickness(3);
                }
                else
                {
                    // 常态高光描边（玻璃卡片）；只有拖到面板上才出现蓝色整圈提示
                    ResetDrawerFrame();
                }
            }
        }), true);

        tile.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            if (_lpTile is not null && ReferenceEquals(_lpTile, tile) && e.Pointer.PointerId == _lpPointerId)
            {
                _lpTimer?.Stop();
                _lpTile = null;
                _lpCloseItem = null;
                _lpSyncItem = null;
                _lpRemapItem = null;
                _lpOptiItem = null;
                return;
            }
            if (_dragTile is null || !ReferenceEquals(_dragTile, tile) || e.Pointer.PointerId != _dragPointerId) return;
            var p = e.GetCurrentPoint(TileHost).Position;
            var dragged = _dragItem;
            var dragTile = _dragTile;
            _dragItem = null;
            _dragTile = null;
            var moved = _dragMoved;
            _dragMoved = false;
            if (moved)
            {
                _suppressTap = true;
                if (dragged is not null) DropDragged(p, dragged, dragTile);
            }
            else if (isSettings)
            {
                _suppressTap = true;
                // 编辑模式轻点设置磁贴：已选中≥2=组合成磁贴组（加号），否则=退出编辑
                if (_marked.Count >= 2) CreateGroupFromMarked();
                else EndMove();
            }
            else if (dragged is not null)
            {
                _selected = index;
                // 编辑模式轻点组磁贴=展开/收起；轻点普通磁贴=标记/取消标记（≥2 后设置磁贴变加号）
                if (dragged.Kind == LaunchKind.Group) ToggleGroup(dragged);
                else ToggleMark();
            }
        }), true);

        tile.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
        {
            if (_dragTile is not null && ReferenceEquals(_dragTile, tile))
            {
                _dragItem = null;
                _dragTile = null;
                _dragMoved = false;
            }
            if (_lpTile is not null && ReferenceEquals(_lpTile, tile))
            {
                _lpTimer?.Stop();
                _lpTile = null;
                _lpCloseItem = null;
                _lpSyncItem = null;
                _lpRemapItem = null;
                _lpOptiItem = null;
            }
        }), true);
    }

    private void OnLongPressTick(object sender, object args)
    {
        ((Microsoft.UI.Xaml.DispatcherTimer)sender).Stop();
        _lpTile = null;
        var closeItem = _lpCloseItem;
        var syncItem = _lpSyncItem;
        var remapItem = _lpRemapItem;
        var optiItem = _lpOptiItem;
        _lpCloseItem = null;
        _lpSyncItem = null;
        _lpRemapItem = null;
        _lpOptiItem = null;
        // 兜底守卫：若已切走（双击进编辑页等），绝不在后台执行关闭程序/进入编辑模式
        if (App.Main?.IsCurrentPage(this) != true) return;
        _suppressTap = true;
        if (syncItem is not null) SaveBackupService.Sync(syncItem);              // 长按游戏存档磁贴=同步存档
        else if (remapItem is not null) { GamepadRemapService.Disable(remapItem); ShowToast("已关闭映射", ToastKind.Warning); } // 长按手柄映射磁贴=关闭映射
        else if (optiItem is not null) OptiScalerService.Activate(optiItem);      // 长按 OptiScaler 磁贴=注入（等同 Y 键）
        else if (closeItem is { Kind: LaunchKind.Widget, WidgetKind: WidgetKind.Foreground }) CloseForegroundApp(); // 长按前台应用磁贴=结束进程（等同 Y 键）
        else if (closeItem is not null) CloseItem(closeItem);                      // 长按可关闭磁贴=关程序/网站/文件夹
        else EnterEditMode();                                                     // 长按设置磁贴=进入编辑模式
    }

    private static double Dist(Windows.Foundation.Point a, Windows.Foundation.Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>拖拽放下：命中设置磁贴（垃圾桶）=删除；命中抽屉区域=放入当前抽屉；命中其他磁贴或空位=交换位置移动</summary>
    private void DropDragged(Windows.Foundation.Point p, LaunchItem dragged, Border? dragTile)
    {
        var wasOverDrawer = _dragOverDrawer;
        _dragOverTrash = false;
        RefreshSettingsBadge();
        ResetDrawerHover();
        // 拖到"添加磁贴"上无意义：不换位、不删除，直接原样收回
        if (_addTile is not null && PointInTile(p, _addTile)) return;

        // 拖到磁贴抽屉面板上：放进当前抽屉（高度随内容增长，总能放下）
        if (wasOverDrawer && _drawerOwner is { } downer && !DrawerService.IsDrawer(dragged)
            && downer.Drawers is { Count: > 0 } dds)
        {
            var dtab = Math.Clamp(_drawerIndex, 0, dds.Count - 1);
            var dpage = dds[dtab];
            _items.Remove(dragged);
            dpage.Items.Add(dragged);
            PersistLayout();
            BuildDisplay();
            LayoutTiles();
            UpdateSelection();
            ShowToast($"已放入「{dpage.Name}」");
            return;
        }

        var draggedIsSettings = dragged.Kind == LaunchKind.Widget && dragged.WidgetKind == WidgetKind.Settings;

        LaunchItem? tItem = null;
        LaunchItem? tGroup = null;
        var hit = false;
        for (var i = 0; i < _tiles.Count && i < _display.Count; i++)
        {
            var t = _tiles[i];
            if (dragTile is not null && ReferenceEquals(t, dragTile)) continue;
            if (!PointInTile(p, t)) continue;
            (tItem, tGroup) = _display[i];
            hit = true;
            break;
        }

        if (hit && tItem is not null && !ReferenceEquals(tItem, dragged))
        {
            var targetIsSettings = tItem.Kind == LaunchKind.Widget && tItem.WidgetKind == WidgetKind.Settings;
            if (targetIsSettings)
            {
                if (draggedIsSettings) return; // 设置磁贴拖到自身：不动
                // 垃圾桶：组按展开状态区分（展开=仅删组本体，收起=整组删除）；普通磁贴=删除
                DeleteItem(dragged, null);
                ShowToast(string.IsNullOrWhiteSpace(dragged.Name) ? "已删除磁贴" : $"已删除 {dragged.Name}");
                return;
            }
            // 拖到普通磁贴/组成员（成员算其组本体）上：交换位置=移动
            var targetTop = tGroup ?? tItem;
            if (!ReferenceEquals(targetTop, dragged)) SwapItems(dragged, targetTop);
            return;
        }
        // 空位：与中心距落点最近的顶层磁贴交换
        SwapToNearest(dragged, p);
    }

    private static FontIcon? FindIcon(Microsoft.UI.Xaml.DependencyObject root)
    {
        var n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (c is FontIcon fi) return fi;
            var r = FindIcon(c);
            if (r is not null) return r;
        }
        return null;
    }

    private static bool PointInTile(Windows.Foundation.Point p, Border t)
    {
        var x = Canvas.GetLeft(t);
        var y = Canvas.GetTop(t);
        return p.X >= x && p.X <= x + t.Width && p.Y >= y && p.Y <= y + t.Height;
    }

    /// <summary>拖拽命中抽屉面板：面板在 TileHost 之外（下方固定区），需把面板矩形换算到 TileHost 坐标系再比较</summary>
    private bool PointInDrawer(Windows.Foundation.Point pInHost)
    {
        if (_drawerRegion is not { } shell || shell.ActualWidth <= 0) return false;
        try
        {
            var r = shell.TransformToVisual(TileHost)
                         .TransformBounds(new Windows.Foundation.Rect(0, 0, shell.ActualWidth, shell.ActualHeight));
            return pInHost.X >= r.X && pInHost.X <= r.X + r.Width
                && pInHost.Y >= r.Y && pInHost.Y <= r.Y + r.Height;
        }
        catch { return false; }
    }

    /// <summary>放到空位：与中心距落点最近的顶层磁贴交换位置</summary>
    private void SwapToNearest(LaunchItem dragged, Windows.Foundation.Point p)
    {
        var gi = _display.IndexOf((dragged, null));
        if (gi < 0) return;
        int best = -1;
        double bd = double.MaxValue;
        for (var i = 0; i < _tiles.Count && i < _display.Count; i++)
        {
            if (i == gi) continue;
            var (_, group) = _display[i];
            if (group is not null) continue; // 组成员不参与
            var (x, y) = Center(_tiles[i]);
            var d = (x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y);
            if (d < bd) { bd = d; best = i; }
        }
        if (best < 0) return;
        var (itemT, _) = _display[best];
        SwapItems(dragged, itemT);
    }

    /// <summary>两个顶层磁贴交换位置（拖拽移动），带 200ms 滑动换位动画</summary>
    private void SwapItems(LaunchItem a, LaunchItem b)
    {
        var ia = _items.IndexOf(a);
        var ib = _items.IndexOf(b);
        if (ia < 0 || ib < 0) return;

        // 交换前记录两磁贴当前画布坐标（旧位置）
        var (ta, tb) = (TileOf(a), TileOf(b));
        (double x, double y)? oldA = ta is null ? null : (Canvas.GetLeft(ta), Canvas.GetTop(ta));
        (double x, double y)? oldB = tb is null ? null : (Canvas.GetLeft(tb), Canvas.GetTop(tb));

        (_items[ia], _items[ib]) = (_items[ib], _items[ia]);
        CommitLayout(a);

        StopSwapAnim(); // 清理上一段未播完的动画
        AnimateToNewPos(TileOf(a), oldA);
        AnimateToNewPos(TileOf(b), oldB);
        if (_swapTiles.Count > 0) StartSwapAnim();
    }

    private Border? TileOf(LaunchItem item)
    {
        var di = _display.IndexOf((item, null));
        return di >= 0 && di < _tiles.Count ? _tiles[di] : null;
    }

    // 换位动画状态：磁贴先偏移到旧位置，再逐帧滑回新位置
    private readonly List<(Border tile, double dx, double dy)> _swapTiles = [];
    private System.Diagnostics.Stopwatch? _swapSw;
    private EventHandler<object>? _swapRender;
    private const int SwapAnimMs = 200;

    private void AnimateToNewPos(Border? newTile, (double x, double y)? oldPos)
    {
        if (newTile is null || oldPos is null) return;
        var dx = oldPos.Value.x - Canvas.GetLeft(newTile);
        var dy = oldPos.Value.y - Canvas.GetTop(newTile);
        if (dx == 0 && dy == 0) return;
        _swapTiles.Add((newTile, dx, dy));
        newTile.RenderTransform = new TranslateTransform { X = dx, Y = dy };
        Canvas.SetZIndex(newTile, 5);
    }

    private void StartSwapAnim()
    {
        _swapSw = System.Diagnostics.Stopwatch.StartNew();
        _swapRender = (_, _) =>
        {
            var t = Math.Clamp(_swapSw!.ElapsedMilliseconds / (double)SwapAnimMs, 0, 1);
            var k = 1 - Math.Pow(1 - t, 3); // ease-out
            foreach (var (tile, dx, dy) in _swapTiles)
                tile.RenderTransform = new TranslateTransform { X = dx * (1 - k), Y = dy * (1 - k) };
            if (t >= 1) StopSwapAnim();
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _swapRender;
    }

    private void StopSwapAnim()
    {
        if (_swapRender is not null)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _swapRender;
            _swapRender = null;
        }
        foreach (var (tile, _, _) in _swapTiles)
        {
            tile.RenderTransform = null;
            Canvas.SetZIndex(tile, 0);
        }
        _swapTiles.Clear();
    }

    private void CommitLayout(LaunchItem follow)
    {
        PersistLayout();
        BuildDisplay();
        LayoutTiles();
        var ni = _display.IndexOf((follow, null));
        if (ni >= 0) _selected = ni;
        else _selected = Math.Clamp(_selected, 0, Math.Max(0, _display.Count - 1));
        UpdateSelection();
    }

    /// <summary>点击/ A 键统一动作（按显示序列索引）</summary>
    private void ActivateAt(int index)
    {
        if (index < 0 || index >= _display.Count) return;
        var (item, group) = _display[index];
        if (item.Kind == LaunchKind.Widget && group is null)
        {
            ActivateWidgetItem(item);
            return;
        }
        if (group is not null || item.Kind != LaunchKind.Group)
        {
            LaunchItem(item);
            return;
        }
        if (_groupAnimating) return; // 展开/收起动画进行中，忽略重复触发
        ToggleGroup(item);
    }

    /// <summary>触发一个小组件磁贴（主页顶层与抽屉内层共用；抽屉内为静态外观但动作语义一致）</summary>
    private void ActivateWidgetItem(LaunchItem item)
    {
        if (item.WidgetKind == WidgetKind.Photos)
        {
            // 图片组：A = 手动切到下一张
            SlideshowService.Skip(item.WidgetFolder ?? "", 1);
        }
        else if (item.WidgetKind == WidgetKind.Settings)
        {
            // 设置磁贴：A = 进入软件设置页
            App.Main?.NavigateSettings(typeof(SettingsPage));
        }
        else if (item.WidgetKind == WidgetKind.Weather)
        {
            // 天气小组件：A = 刷新当前天气
            WeatherService.Refresh();
        }
        else if (item.WidgetKind == WidgetKind.Mapping)
        {
            // 映射小组件：A = 触发映射（键盘键对外发送 / 手柄键内部派发）
            TriggerMapping(item);
        }
        else if (item.WidgetKind == WidgetKind.Trainer)
        {
            // 修改器小组件：A = 按当前游戏进程自动下载并打开最新修改器
            TrainerService.Activate();
        }
        else if (item.WidgetKind == WidgetKind.Backup)
        {
            // 游戏存档小组件：A = 备份当前正在玩的游戏的存档
            SaveBackupService.Activate(item);
        }
        else if (item.WidgetKind == WidgetKind.OptiScaler)
        {
            // OptiScaler 小组件：A/单点 = 发 Insert 键呼出游戏内 OptiScaler 菜单（注入走 Y/长按）
            SendOptiScalerMenuKey();
        }
        else if (item.WidgetKind == WidgetKind.GamepadMap)
        {
            // 手柄映射小组件：A/点击 = 开/关映射（长按 = 关闭，见 OnLongPressTick）
            GamepadRemapService.Toggle(item);
            ShowToast(GamepadRemapService.IsEnabled(item) ? "映射已开启" : "映射已关闭",
                GamepadRemapService.IsEnabled(item) ? ToastKind.Success : ToastKind.Warning);
        }
        else if (item.WidgetKind == WidgetKind.RecycleBin)
        {
            // 回收站小组件：A/点击 = 打开系统回收站（与启动程序一致：按设置收起窗口，否则置顶窗口会挡住资源管理器）
            RecycleBinService.Open();
            if (SettingsService.Current.HideAfterLaunch) App.Main?.HideToTray();
        }
        else if (item.WidgetKind == WidgetKind.Foreground)
        {
            // 前台应用监测小组件：A/点击 = 冻结/解冻当前监测到的前台应用
            ToggleForegroundFreeze();
        }
        else if (item.WidgetKind == WidgetKind.System)
        {
            // 系统小组件：A/点击 = 打开入口（设置/我的电脑/资源管理器）或切换开关（静音/熄屏/WiFi/蓝牙）
            ActivateSystemPanel(item);
        }
        else
        {
            // 音乐小组件：A = 播放/暂停系统当前媒体
            SystemMediaService.TogglePlay();
        }
    }

    /// <summary>系统小组件 A 键语义：打开对应入口 / 切换开关（音量=静音、亮度=熄屏、WiFi/蓝牙=射频）；
    /// 入口类在按设置收起窗口时隐藏启动器（避免全屏窗口挡住刚打开的资源管理器/设置）</summary>
    private void ActivateSystemPanel(LaunchItem item)
    {
        var panel = SystemPanelService.FromOption(
            SystemPanelService.NormalizeOption(item.SystemOptionMask, item.SystemOption));
        _ = ActivateSystemPanelAsync(item, panel);
    }

    private async Task ActivateSystemPanelAsync(LaunchItem item, SystemPanel panel)
    {
        var message = await SystemPanelService.ToggleAsync(panel);
        // A 键后再按一次即复原（静音/熄屏/射频都是开关语义）：先刷新读数再提示，提示内容与磁贴一致
        if (panel == SystemPanel.Brightness) await SystemPanelService.RefreshBrightnessAsync();
        RefreshSystemTiles();
        ShowToast($"{SystemPanelService.Label(panel)}：{message}");
        if (SystemPanelService.IsLauncher(panel) && SettingsService.Current.HideAfterLaunch) App.Main?.HideToTray();
    }

    /// <summary>清空回收站（回收站小组件 Y 键语义）：先查项目数（空桶时 SHEmptyRecycleBin 会返回失败），静默清空并提示结果</summary>
    private void EmptyRecycleBin()
    {
        if (RecycleBinService.ItemCount() == 0) { ShowToast("回收站无垃圾", ToastKind.Success); return; }
        var hr = RecycleBinService.Empty();
        if (hr == 0) ShowToast("回收站已清空", ToastKind.Success);
        else ShowToast($"清空失败 (0x{hr:X8})：回收站可能正被占用", ToastKind.Warning);
    }

    /// <summary>冻结/解冻前台应用（前台应用监测小组件 A 键语义）：A 再按一次即为解冻</summary>
    private void ToggleForegroundFreeze()
    {
        var app = ForegroundWatcherService.Current;
        if (app is null) { ShowToast("未检测到前台应用", ToastKind.Warning); return; }
        if (ForegroundWatcherService.IsProtected(app)) { ShowToast($"{app.ProcessName} 为系统关键进程，不可操作", ToastKind.Warning); return; }
        var (ok, frozen) = ForegroundWatcherService.ToggleFreeze();
        if (!ok) { ShowToast("冻结/解冻失败：权限不足或应用已退出", ToastKind.Warning); return; }
        ShowToast(frozen ? $"已冻结 {app.ProcessName}" : $"已解冻 {app.ProcessName}", ToastKind.Success);
    }

    /// <summary>强制结束前台应用（前台应用监测小组件 Y 键/长按语义）</summary>
    private void CloseForegroundApp()
    {
        var app = ForegroundWatcherService.Current;
        if (app is null) { ShowToast("未检测到前台应用", ToastKind.Warning); return; }
        if (ForegroundWatcherService.IsProtected(app)) { ShowToast($"{app.ProcessName} 为系统关键进程，不可操作", ToastKind.Warning); return; }
        if (ForegroundWatcherService.Kill()) ShowToast($"已结束 {app.ProcessName}", ToastKind.Success);
        else ShowToast("结束失败：应用可能已退出或权限不足", ToastKind.Warning);
    }

    /// <summary>映射触发中（防止"手柄键=A"这类映射递归触发自身）</summary>
    private bool _mappingInjecting;

    /// <summary>
    /// 触发映射小组件：
    /// 键盘键 → 收起窗口到托盘后，用 SendInput 把该键（含 Ctrl/Shift/Alt/Win 组合）发给此前的前台程序；
    /// 手柄键 → Windows 无法把 XInput 注入其它程序，改为等价于在本应用内按下该手柄键（注入当前页处理）。
    /// </summary>
    private void TriggerMapping(LaunchItem item)
    {
        if (item.MappingPad != GamepadButtons.None)
        {
            if (_mappingInjecting) return;
            _mappingInjecting = true;
            try { HandleGamepad(item.MappingPad); }
            finally { _mappingInjecting = false; }
            return;
        }
        if (item.MappingKey != 0)
        {
            var vk = item.MappingKey;
            var mods = item.MappingMods;
            App.Main?.HideToTray();
            // 等窗口滑出/隐藏后再发送，确保按键落到此前的前台程序（而非本窗口）
            _ = DelaySendAsync(vk, mods);
        }
    }

    private static async Task DelaySendAsync(int vk, int mods)
    {
        await Task.Delay(320);
        KeySendService.Send(vk, mods);
    }

    /// <summary>
    /// 呼出 OptiScaler 游戏内菜单：收起窗口到托盘后用 SendInput 把 Insert 键发给此前的前台游戏
    /// （OptiScaler 默认用 Insert 开关它的设置菜单）。注入走 Y 键 / 触屏长按，见 HandleGamepad 与 OnLongPressTick。
    /// </summary>
    private void SendOptiScalerMenuKey()
    {
        if (!TrainerService.IsGameRunning())
        {
            ShowToast("未检测到游戏进程，请先运行游戏", ToastKind.Warning);
            return;
        }
        App.Main?.HideToTray();
        // 等窗口滑出/隐藏后再发送，确保 Insert 落到此前的前台游戏，而不是本窗口
        _ = DelaySendAsync(KeySendService.VkInsert, 0);
    }

    /// <summary>展开/收起磁贴组（按外观设置开关决定是否播放成员交错浮现动画）</summary>
    private void ToggleGroup(LaunchItem group)
    {
        var expanding = !ReferenceEquals(_expanded, group);
        var anim = SettingsService.Current.GroupExpandAnimation;

        if (anim && !expanding)
        {
            // 收起：成员磁贴先淡出缩小，动画结束后再重排（避免瞬间消失）
            var outgoing = new List<Border>();
            for (var i = 0; i < _tiles.Count && i < _display.Count; i++)
                if (ReferenceEquals(_display[i].group, group)) outgoing.Add(_tiles[i]);
            if (outgoing.Count > 0)
            {
                _groupAnimating = true;
                AnimateTiles(outgoing, false, () =>
                {
                    _groupAnimating = false;
                    ApplyGroupToggle(group);
                });
                return;
            }
        }

        if (!anim)
        {
            ApplyGroupToggle(group);
            return;
        }

        // 展开：重排后，新出现的成员磁贴交错淡入浮现
        ApplyGroupToggle(group);
        var incoming = new List<Border>();
        for (var i = 0; i < _tiles.Count && i < _display.Count; i++)
            if (ReferenceEquals(_display[i].group, group)) incoming.Add(_tiles[i]);
        if (incoming.Count > 0)
        {
            _groupAnimating = true;
            // 立即置隐藏初态（防止下一帧前闪现完整磁贴），
            // 再延迟到下一帧启动 Storyboard——新建磁贴需先完成 measure/arrange，
            // 否则动画对未布局元素静默失效（"只有收缩没有展开"的根因）
            foreach (var t in incoming)
            {
                t.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                t.RenderTransform = new ScaleTransform { ScaleX = 0.7, ScaleY = 0.7 };
                t.Opacity = 0;
            }
            DispatcherQueue.TryEnqueue(() =>
                AnimateTiles(incoming, true, () => _groupAnimating = false));
        }
    }

    /// <summary>真正切换展开状态并重排</summary>
    private void ApplyGroupToggle(LaunchItem group)
    {
        _expanded = ReferenceEquals(_expanded, group) ? null : group;
        BuildDisplay();
        LayoutTiles();
        UpdateSelection();
    }

    /// <summary>
    /// 对一批磁贴播放交错动画：@in=true 从 0.7 缩放淡入浮现，false 淡出缩小。
    /// WinUI Storyboard 无 per-child delay，每个磁贴独立 Storyboard + BeginTime 递增实现交错；
    /// 全部结束后执行 done。
    /// </summary>
    private static void AnimateTiles(List<Border> tiles, bool @in, Action done)
    {
        var final = Math.Clamp(SettingsService.Current.TileOpacity, 0.3, 1.0);
        var per = TimeSpan.FromMilliseconds(@in ? 200 : 130);
        var pending = tiles.Count;

        for (var k = 0; k < tiles.Count; k++)
        {
            var t = tiles[k];
            var scale = new ScaleTransform { ScaleX = @in ? 0.7 : 1.0, ScaleY = @in ? 0.7 : 1.0 };
            t.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            t.RenderTransform = scale;
            t.Opacity = @in ? 0 : final;

            var solo = BuildSoloStoryboard(t, @in, final, per);
            solo.BeginTime = TimeSpan.FromMilliseconds(k * 45);
            solo.Completed += (_, _) =>
            {
                t.RenderTransform = null;
                if (--pending == 0) done();
            };
            solo.Begin();
        }
    }

    /// <summary>单个磁贴的淡入/淡出 + 缩放 Storyboard（缩放用属性路径指向元素 RenderTransform）</summary>
    private static Storyboard BuildSoloStoryboard(Border t, bool @in, double final, TimeSpan per)
    {
        var ease = new CubicEase { EasingMode = @in ? EasingMode.EaseOut : EasingMode.EaseIn };
        var sb = new Storyboard();

        var o = new DoubleAnimation { From = @in ? 0 : final, To = @in ? final : 0, Duration = new Duration(per), EasingFunction = ease };
        Storyboard.SetTarget(o, t);
        Storyboard.SetTargetProperty(o, "Opacity");
        sb.Children.Add(o);

        var from = @in ? 0.7 : 1.0;
        var to = @in ? 1.0 : 0.7;
        var sx = new DoubleAnimation { From = from, To = to, Duration = new Duration(per), EasingFunction = ease };
        Storyboard.SetTarget(sx, t);
        Storyboard.SetTargetProperty(sx, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
        sb.Children.Add(sx);
        var sy = new DoubleAnimation { From = from, To = to, Duration = new Duration(per), EasingFunction = ease };
        Storyboard.SetTarget(sy, t);
        Storyboard.SetTargetProperty(sy, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
        sb.Children.Add(sy);
        return sb;
    }

    /// <summary>当前选中的是否为顶层音乐小组件磁贴（决定 LB/RB 是切歌还是翻页）</summary>
    private bool IsMusicSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Music;
    }

    /// <summary>对一个指定磁贴执行 Y 键语义（OptiScaler=注入、游戏存档=同步存档、回收站=清空、前台应用=结束进程、其余=关闭其程序）；抽屉内层复用</summary>
    private void HandleItemY(LaunchItem? item)
    {
        if (item is null) return;
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.OptiScaler) { OptiScalerService.Activate(item); return; }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Backup) { SaveBackupService.Sync(item); return; }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.RecycleBin) { EmptyRecycleBin(); return; }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Foreground) { CloseForegroundApp(); return; }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.System) return; // 系统小组件没有"可关闭"的对象
        CloseItem(item);
    }

    /// <summary>当前选中的是否为顶层图片组磁贴（决定 LB/RB 是切图还是翻页）</summary>
    private bool IsPhotoSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Photos;
    }

    /// <summary>当前选中的是否为顶层天气小组件磁贴（决定 A 键是刷新天气还是启动）</summary>
    private bool IsWeatherSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Weather;
    }

    /// <summary>当前选中的是否为顶层映射小组件磁贴（决定 A 键是触发映射还是启动）</summary>
    private bool IsMappingSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Mapping;
    }

    /// <summary>当前选中的是否为顶层修改器小组件磁贴（决定 A 键是打开修改器还是启动）</summary>
    private bool IsTrainerSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Trainer;
    }

    /// <summary>当前选中的是否为顶层游戏存档小组件磁贴（决定 A 键是备份存档还是启动）</summary>
    private bool IsBackupSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Backup;
    }

    /// <summary>当前选中的是否为顶层回收站小组件磁贴（决定 A 键是打开回收站、Y 键是清空回收站）</summary>
    private bool IsRecycleBinSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.RecycleBin;
    }

    /// <summary>当前选中的是否为顶层前台应用监测小组件磁贴（决定 A 键是冻结/解冻、Y 键是结束进程）</summary>
    private bool IsForegroundSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Foreground;
    }

    /// <summary>当前选中的是否为顶层系统小组件磁贴（决定 LB/RB 是调整音量亮度还是翻页）</summary>
    private bool IsSystemSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.System;
    }

    /// <summary>当前选中的是否为顶层手柄映射小组件磁贴（决定 A 键是开/关映射还是启动）</summary>
    private bool IsGamepadMapSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.GamepadMap;
    }

    /// <summary>当前选中的是否为顶层 OptiScaler 小组件磁贴（决定 Y 键是注入还是关闭程序）</summary>
    private bool IsOptiSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.OptiScaler;
    }

    /// <summary>当前选中磁贴定向的图片文件夹（未选中/无文件夹返回空串）</summary>
    private string SelectedFolder()
    {
        if (_selected < 0 || _selected >= _display.Count) return "";
        return _display[_selected].item.WidgetFolder ?? "";
    }

    // ---- 添加方式选择遮罩（X 键弹出：自定义 / 小组件 / 副页块） ----

    private bool _choiceOpen;
    private Storyboard? _choiceIn; // 遮罩入场动画
    private int _choiceIndex; // 0=自定义 1=小组件 2=副页块

    /// <summary>打开添加方式选择遮罩（MainWindow 的 X 键调用；编辑模式"添加磁贴"也复用此入口，故不再按 _moving 拦截）</summary>
    public void OpenAddChoice()
    {
        _choiceOpen = true;
        _choiceIndex = 0;
        ChoiceOverlay.Visibility = Visibility.Visible;
        UpdateChoiceCursor();
        PlayChoiceIn(); // 淡入 + 轻微放大，避免遮罩硬弹
    }

    private void CloseChoice()
    {
        _choiceOpen = false;
        _choiceIn?.Stop(); // 打断入场动画，避免下次打开残留
        ChoiceOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>遮罩入场：遮罩层淡入 + 面板由 0.94 缓动放大到 1</summary>
    private void PlayChoiceIn()
    {
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140) };
        Storyboard.SetTarget(fade, ChoiceMask);
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
        Storyboard.SetTarget(scale, ChoicePanelScale);
        Storyboard.SetTargetProperty(scale, "ScaleX");
        Storyboard.SetTarget(scaleY, ChoicePanelScale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");

        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(scale);
        sb.Children.Add(scaleY);
        _choiceIn?.Stop();
        _choiceIn = sb;
        sb.Begin();
    }

    /// <summary>遮罩内手柄按键：方向=循环切换，A=确认，B/View/X=取消</summary>
    private bool HandleChoice(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.DPadUp) || b.HasFlag(GamepadButtons.DPadLeft))
        { _choiceIndex = (_choiceIndex + 2) % 3; UpdateChoiceCursor(); return true; }
        if (b.HasFlag(GamepadButtons.DPadDown) || b.HasFlag(GamepadButtons.DPadRight))
        { _choiceIndex = (_choiceIndex + 1) % 3; UpdateChoiceCursor(); return true; }
        if (b.HasFlag(GamepadButtons.A))
        {
            var target = _choiceIndex;
            CloseChoice();
            NavigateAddChoice(target);
            return true;
        }
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View) || b.HasFlag(GamepadButtons.X))
        {
            // 取消添加：清掉可能残留的"放进抽屉"一次性请求，避免后续新建磁贴误入抽屉
            if (App.Main is { } m0) m0.PendingDrawerInsert = null;
            CloseChoice();
            return true;
        }
        return true; // 遮罩打开时吞掉其余按键，避免误触发主页操作
    }

    private void UpdateChoiceCursor()
    {
        var accentBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
        var idleBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        var accentBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0x4C, 0xC2, 0xFF));
        var idleBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        Style(ChoiceCustom, _choiceIndex == 0);
        Style(ChoiceWidget, _choiceIndex == 1);
        Style(ChoiceSubPage, _choiceIndex == 2);

        // 选中项：强调色描边 + 底色高亮；未选中：淡描边 + 卡片底
        void Style(Border card, bool on)
        {
            card.BorderBrush = on ? accentBr : idleBr;
            card.BorderThickness = new Thickness(on ? 2 : 1.5);
            card.Background = on ? accentBg : idleBg;
        }
    }

    private void NavigateAddChoice(int index)
    {
        if (index == 1) App.Main?.RootNavigateWidget();
        else if (index == 2)
        {
            // 副页块：不是"添加磁贴"，清掉抽屉插入请求
            if (App.Main is { } m1) m1.PendingDrawerInsert = null;
            AddSubPage();
        }
        else App.Main?.RootNavigateAddItem();
    }

    /// <summary>副页块：主页多一页（可添加多个）。新增后直接在主页切到新页并提示（本页即主页，无需导航）。</summary>
    private void AddSubPage()
    {
        SettingsService.Update(s => s.HomePageCount++);
        _page = Math.Max(0, PageCount() - 1);   // 切到新页
        _expanded = null;
        _grabbed = null;
        _marked.Clear();
        _inDrawer = false;
        _drawerOwner = null;
        _selected = 0;
        if (App.Main is { } main) main.CurrentHomePage = _page;
        Reload();          // 内部 BuildDisplay/LayoutTiles/UpdateSelection
        LayoutPageDots();
        ShowToast($"已添加副页块（共 {PageCount()} 页）");
    }

    /// <summary>刷新所有音乐磁贴的系统媒体状态显示（曲目名/播放暂停按钮/封面）</summary>
    private void RefreshMusicTiles()
    {
        if (_musicRefs.Count == 0) return;
        var playing = SystemMediaService.IsPlaying;
        var has = SystemMediaService.HasSession;
        var title = has ? SystemMediaService.Title : "";
        if (has && title.Length == 0) title = "未知曲目";
        var glyph = playing ? "\uE769" : "\uE768"; // pause / play

        // 封面：服务层已在曲目变化时拉取入缓存，UI 直接读取应用（无并发竞态）
        var cover = has ? SystemMediaService.CoverImage : null;

        foreach (var (refs, index) in _musicRefs)
        {
            if (index < 0 || index >= _display.Count) continue;
            refs.Title.Text = has ? title : "未在播放";
            refs.PlayGlyph.Glyph = glyph;
            if (cover != null)
            {
                refs.CoverBox.Background = new ImageBrush
                {
                    ImageSource = cover,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
                };
                refs.CoverBox.Visibility = Visibility.Visible;
            }
            else refs.CoverBox.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>刷新所有图片组磁贴的当前轮播图片（换图时只换 Source，不重建磁贴）</summary>
    private void RefreshPhotoTiles()
    {
        foreach (var (refs, (index, folder, item)) in _photoRefs)
        {
            if (index < 0 || index >= _display.Count) continue;
            var img = SlideshowService.CurrentImage(folder);
            if (img != null && !ReferenceEquals(refs.Img.Source, img))
            {
                refs.Img.Source = img;
                refs.Img.Visibility = Visibility.Visible;
                refs.Placeholder.Visibility = Visibility.Collapsed;
                if (item.PhotoAnimation) FadeIn(refs.Img); // 轮播动画：淡入
            }
            else if (img == null)
            {
                refs.Img.Visibility = Visibility.Collapsed;
                refs.Placeholder.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>刷新所有天气小组件磁贴的显示（数据变化时只改文字不重建磁贴）</summary>
    private void RefreshWeatherTiles()
    {
        foreach (var (refs, _) in _weatherRefs) TileStyle.FillWeatherTile(refs);
    }

    /// <summary>手柄映射开关状态变化（在 UI 线程触发）：刷新各映射磁贴的"映射开/关"显示</summary>
    private void OnRemapStateChanged() => RefreshRemapTiles();

    /// <summary>刷新所有手柄映射磁贴的开关状态显示（只改状态文字不重建磁贴）</summary>
    private void RefreshRemapTiles()
    {
        foreach (var (refs, (_, item)) in _remapRefs) TileStyle.FillGamepadMapState(refs, item);
    }

    /// <summary>前台应用/冻结状态变化（可能来自后台轮询线程）：marshal 回 UI 线程后刷新磁贴内容</summary>
    private void OnForegroundChanged() => DispatcherQueue.TryEnqueue(RefreshForegroundTiles);

    /// <summary>刷新所有系统小组件磁贴的读数（右摇杆切换面板项、音量/亮度/射频变化后调用）</summary>
    private void RefreshSystemTiles()
    {
        foreach (var (refs, item) in _sysRefs) TileStyle.FillSystemTile(refs, item);
    }

    /// <summary>当前焦点所在的系统小组件磁贴项（磁贴区选中 或 抽屉内层焦点）；不是系统小组件返回 null</summary>
    private LaunchItem? SystemItemInFocus()
    {
        if (_display.Count == 0) return null;
        if (_inDrawer)
        {
            if (FocusedDrawerItem() is not { Kind: LaunchKind.Widget, WidgetKind: WidgetKind.System } inner) return null;
            return inner;
        }
        if (_selected < 0 || _selected >= _display.Count) return null;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.System ? item : null;
    }

    /// <summary>右摇杆 ←/→：在启用的项里循环切换（回绕），播左右滑动动画并持久化（不弹提示条）</summary>
    private void CycleSystemPanel(LaunchItem item, int delta)
    {
        var option = SystemPanelService.CycleOption(item.SystemOptionMask, item.SystemOption, delta);
        if (option == item.SystemOption) return; // 只启用了一项：无需切换
        item.SystemOption = option;
        ConfigService.Save(_items.ToList());
        AnimateSystemSwitch(delta);
    }

    /// <summary>系统小组件切换面板项动画：旧内容向切换方向反向滑出淡出，新内容从另一侧滑入淡入</summary>
    private void AnimateSystemSwitch(int delta)
    {
        var dir = delta >= 0 ? 1 : -1;
        foreach (var (refs, item) in _sysRefs.ToList())
        {
            var slide = refs.SlideRow;
            if (slide.RenderTransform is not TranslateTransform t)
            {
                t = new TranslateTransform();
                slide.RenderTransform = t;
            }
            var outAnim = new DoubleAnimation
            {
                To = -dir * 26,
                Duration = TimeSpan.FromMilliseconds(80),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(outAnim, t);
            Storyboard.SetTargetProperty(outAnim, "X");
            var opacityOut = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(80),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(opacityOut, slide);
            Storyboard.SetTargetProperty(opacityOut, "Opacity");
            var sbOut = new Storyboard();
            sbOut.Children.Add(outAnim);
            sbOut.Children.Add(opacityOut);
            sbOut.Completed += (_, _) =>
            {
                TileStyle.FillSystemTile(refs, item); // 换成新面板项的内容
                t.X = dir * 26;
                var inAnim = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(inAnim, t);
                Storyboard.SetTargetProperty(inAnim, "X");
                var opacityIn = new DoubleAnimation
                {
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(opacityIn, slide);
                Storyboard.SetTargetProperty(opacityIn, "Opacity");
                var sbIn = new Storyboard();
                sbIn.Children.Add(inAnim);
                sbIn.Children.Add(opacityIn);
                sbIn.Begin();
            };
            sbOut.Begin();
        }
    }

    /// <summary>LB/RB：调整当前系统小组件（音量/亮度 ±）；不是系统小组件或当前项不支持调整时返回 false（交回原语义）</summary>
    private bool TryAdjustSystem(int delta)
    {
        if (SystemItemInFocus() is not { } item) return false;
        var panel = SystemPanelService.FromOption(
            SystemPanelService.NormalizeOption(item.SystemOptionMask, item.SystemOption));
        if (!SystemPanelService.SupportsAdjust(panel)) return false;
        if (panel == SystemPanel.Brightness)
        {
            // 亮度走 WMI，必须离开 UI 线程执行（否则会阻塞界面，且在部分设备上会和 XAML 争用 COM 导致闪退）
            _ = AdjustBrightnessAsync(delta);
            return true;
        }
        if (!SystemPanelService.Adjust(panel, delta))
        {
            ShowToast("音量控制不可用", ToastKind.Warning);
            return true;
        }
        RefreshSystemTiles();
        return true;
    }

    /// <summary>后台调整亮度，完成后再回 UI 线程刷新读数</summary>
    private async Task AdjustBrightnessAsync(int delta)
    {
        var ok = await Task.Run(() => SystemPanelService.AdjustBrightness(delta));
        if (!ok)
        {
            ShowToast("当前设备不支持亮度调节", ToastKind.Warning);
            return;
        }
        await SystemPanelService.RefreshBrightnessAsync();
        RefreshSystemTiles();
    }

    /// <summary>刷新所有前台应用监测磁贴的内容（只改图标与文字不重建磁贴）</summary>
    private void RefreshForegroundTiles()
    {
        foreach (var (refs, _) in _fgRefs) TileStyle.FillForegroundTile(refs);
    }

    /// <summary>磁贴换图淡入动画（1 秒）</summary>
    private static void FadeIn(UIElement el)
    {
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromSeconds(1))
        };
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, el);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Begin();
    }

    // ---- 选择 / 导航 ----

    private void UpdateSelection()
    {
        for (var i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            // 焦点在抽屉区域时不点亮磁贴区选中框（避免与内层焦点/面板强调色混淆）
            if (!_inDrawer && i == _selected)
            {
                // 选中效果用粗内描边 + 提升 Z 序：Border 描边向内绘制不会溢出边界被相邻磁贴盖住
                t.BorderThickness = new Thickness(4);
                t.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(t, 1);
            }
            else
            {
                // 移动模式标记/抓取描边优先于默认描边（否则标记看起来"没效果"）
                var (item, _) = i < _display.Count ? _display[i] : default;
                if (_moving && item is not null && _marked.Contains(item))
                {
                    t.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
                    t.BorderThickness = new Thickness(3);
                }
                else if (_moving && item is not null && ReferenceEquals(_grabbed, item))
                {
                    t.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD3, 0x4D));
                    t.BorderThickness = new Thickness(3);
                }
                else
                {
                    t.BorderThickness = new Thickness(1);
                    t.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                }
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(t, 0);
            }
        }
        if (!_inDrawer && _selected >= 0 && _selected < _tiles.Count)
            TileScroll.ChangeView(null,
                Math.Max(0, Canvas.GetTop(_tiles[_selected]) - 40), null, false);
        UpdateDrawerSelection(); // 区域内部焦点描边 / 面板"在区域内"强调色
        UpdateHintForSelection();
    }

    /// <summary>按当前选中项替换底部提示条：音乐/图片组磁贴=只显示对应操作提示（主页提示暂时隐藏），其他=恢复主页提示（编辑模式不处理，由 UpdateHintBar 负责）</summary>
    private void UpdateHintForSelection()
    {
        if (_moving) return;
        if (_inDrawer)
            HintBar.SetHints(_drawerFocusPos < 0
                ? "DPAD:切抽屉|A:进入|X:添加磁贴|Y:添加抽屉|B:退出" // 抽屉层：按键只作用于抽屉
                : FocusedDrawerItem() is { Kind: LaunchKind.Widget, WidgetKind: WidgetKind.System }
                    ? "DPAD:选择内层|←→:切换项|A:开关/打开|LB/RB:调整|B:返回抽屉" // 内层系统小组件
                    : "DPAD:选择内层|A:启动|X:添加|Y:关闭|B:返回抽屉"); // 内层：按键作用于内层磁贴
        else if (IsMusicSelected())
            HintBar.SetHints("DPAD:切换选择|A:播放/暂停|LB:上一曲|RB:下一曲");
        else if (IsPhotoSelected())
            HintBar.SetHints("DPAD:切换选择|A:切换|LB:上一张|RB:下一张");
        else if (IsWeatherSelected())
            HintBar.SetHints("DPAD:切换选择|A:刷新天气|R3:编辑|L3:排列|B:隐藏");
        else if (IsMappingSelected())
            HintBar.SetHints("DPAD:切换选择|A:触发映射|R3:编辑|L3:排列|B:隐藏");
        else if (IsTrainerSelected())
            HintBar.SetHints("DPAD:切换选择|A:打开修改器|Y:关闭修改器|R3:编辑|L3:排列|B:隐藏");
        else if (IsBackupSelected())
            HintBar.SetHints("DPAD:切换选择|A:备份存档|Y:同步存档|R3:编辑|L3:排列|B:隐藏");
        else if (IsRecycleBinSelected())
            HintBar.SetHints("DPAD:切换选择|A:打开回收站|Y:清空回收站|R3:编辑|L3:排列|B:隐藏");
        else if (IsForegroundSelected())
            HintBar.SetHints("DPAD:切换选择|A:冻结/解冻|Y:结束进程|R3:编辑|L3:排列|B:隐藏");
        else if (IsSystemSelected())
            HintBar.SetHints("DPAD:切换选择|←→:切换项|A:开关/打开|LB/RB:调整|R3:编辑|L3:排列|B:隐藏");
        else if (IsGamepadMapSelected())
            HintBar.SetHints("DPAD:切换选择|A:开启/关闭映射|R3:编辑|L3:排列|B:隐藏");
        else if (IsOptiSelected())
            HintBar.SetHints("DPAD:切换选择|A:呼出OptiScaler菜单|Y:注入|R3:编辑|L3:排列|B:隐藏");
        else if (IsClosableSelected())
            HintBar.SetHints("DPAD:选择|A:启动|Y:关闭|X:添加|R3:编辑|L3:排列|VIEW:设置|B:隐藏");
        else
            HintBar.SetHints(DrawerService.FindDrawer(_items.ToList()) is null
                ? "DPAD:选择|A:启动|X:添加|R3:编辑|L3:排列|VIEW:设置|B:隐藏"
                : "DPAD:选择(↓:抽屉)|A:启动|X:添加|R3:编辑|L3:排列|VIEW:设置|B:隐藏");
    }

    /// <summary>选中磁贴是否可 Y 关闭（程序/网站/文件夹=可；组/小组件=不可）</summary>
    private bool IsClosableSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind is LaunchKind.App or LaunchKind.Url or LaunchKind.Folder;
    }

    /// <summary>方向选择：按磁贴中心坐标找指定方向最近邻。返回 true=发生了移动（供"最底行 ↓ 进入抽屉"判断）</summary>
    private bool MoveSelection(int dx, int dy)
    {
        if (_tiles.Count == 0) return false;
        var cur = Math.Clamp(_selected, 0, _tiles.Count - 1);
        var (cx, cy) = Center(_tiles[cur]);

        int best = -1;
        double bestScore = double.MaxValue;
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (i == cur) continue;
            var (x, y) = Center(_tiles[i]);
            var along = dx != 0 ? (x - cx) * dx : (y - cy) * dy;
            if (along <= 1) continue;
            var cross = dx != 0 ? Math.Abs(y - cy) : Math.Abs(x - cx);
            var score = cross * 10 + along;
            if (score < bestScore) { bestScore = score; best = i; }
        }

        if (best >= 0)
        {
            _selected = best;
            UpdateSelection();
            return true;
        }
        return false;
    }

    private static (double X, double Y) Center(Border b) =>
        (Canvas.GetLeft(b) + b.Width / 2, Canvas.GetTop(b) + b.Height / 2);

    /// <summary>移动模式（已抓取）：把抓取中的磁贴与指定方向最近邻磁贴交换位置</summary>
    private void SwapInDirection(int dx, int dy)
    {
        if (_grabbed is null || _tiles.Count == 0) return;
        var gi = _display.IndexOf((_grabbed, null));
        if (gi < 0) return;
        var (gx, gy) = Center(_tiles[gi]);

        int best = -1;
        double bestScore = double.MaxValue;
        for (var i = 0; i < _tiles.Count && i < _display.Count; i++)
        {
            if (i == gi) continue;
            var (itemB, groupB) = _display[i];
            if (groupB is not null) continue; // 组成员不参与交换
            var (x, y) = Center(_tiles[i]);
            var along = dx != 0 ? (x - gx) * dx : (y - gy) * dy;
            if (along <= 1) continue;
            var cross = dx != 0 ? Math.Abs(y - gy) : Math.Abs(x - gx);
            var score = cross * 10 + along;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return;

        // 交换 _items 中两个顶层磁贴的位置，选中跟随抓取项
        var itemA = _grabbed;
        var (itemT, _) = _display[best];
        var ia = _items.IndexOf(itemA);
        var ib = _items.IndexOf(itemT);
        if (ia < 0 || ib < 0) return;
        (_items[ia], _items[ib]) = (_items[ib], _items[ia]);
        PersistLayout();
        BuildDisplay();
        LayoutTiles();
        var newIdx = _display.IndexOf((itemA, null));
        if (newIdx >= 0) _selected = newIdx;
        UpdateSelection();
    }

    /// <summary>移动模式 X 键：标记/取消标记当前顶层磁贴（已标记青色描边）</summary>
    private void ToggleMark()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        if (group is not null || item.Kind == LaunchKind.Group) return; // 组成员/组本体不参与
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Settings) return; // 设置磁贴不参与组合
        if (DrawerService.IsDrawer(item)) return; // 抽屉组件不参与标记/组合（避免自指容器）
        if (_marked.Contains(item)) _marked.Remove(item);
        else _marked.Add(item);
        UpdateHintBar();
        LayoutTiles();
        UpdateSelection();
    }

    /// <summary>移动模式 A 键：把已标记（≥2）的磁贴组合为一个组磁贴（放在第一个标记项的位置）</summary>
    private void CreateGroupFromMarked()
    {
        var positions = _marked.Select(it => _items.IndexOf(it)).Where(i => i >= 0).OrderBy(i => i).ToList();
        if (positions.Count < 2) { EndMove(); return; }

        var group = new LaunchItem
        {
            Name = "磁贴组",
            Target = "",
            Kind = LaunchKind.Group,
            Size = TileSize.Wide
        };
        group.Members.AddRange(_marked);

        var first = positions[0];
        for (var i = positions.Count - 1; i >= 0; i--) _items.RemoveAt(positions[i]);
        _items.Insert(Math.Min(first, _items.Count), group);

        _marked.Clear();
        _selected = Math.Min(first, _items.Count);
        PersistLayout();
        BuildDisplay();
        LayoutTiles();
        UpdateSelection();
        UpdateHintBar();
    }

    private void UpdateHintBar()
    {
        // 编辑模式内焦点在抽屉区域：抽屉层 Y=删除当前抽屉（最后一个删完则移除整个组件）、A=移入标记（无标记则进内层）、焦点在加号磁贴上时 A=添加磁贴
        if (_inDrawer)
        {
            HintBar.SetHints(_drawerFocusPos < 0
                ? "DPAD:切抽屉|A:移入标记|Y:删除抽屉|B:退出"
                : FocusedDrawerIsAddTile()
                    ? "A:添加磁贴|B:返回抽屉"
                    : "DPAD:选择内层|A:移入标记|Y:删除磁贴|B:返回抽屉");
            return;
        }
        // 编辑（移动）模式：底部提示条临时换成编辑操作提示（徽章形式）
        HintBar.SetHints(_marked.Count >= 2
            ? $"DPAD:移动选择|X:取消标记|A:组合({_marked.Count})|Y:删除|B:退出"
            : _grabbed is not null
                ? $"DPAD:移动「{_grabbed.Name}」|A:放下|X:标记|Y:删除|B:退出"
                : "DPAD:选择|X:多选标记|A:抓取移动|Y:删除|B:退出");
    }

    /// <summary>删除当前选中磁贴：展开中的组=仅删组本体（成员回到顶层原位）；未展开的组=整组删除（含成员）；成员=移出组；普通=删除</summary>
    public void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        DeleteItem(item, group);
    }

    /// <summary>删除指定磁贴（Y 键/拖到设置磁贴上共用）：组=拆分组；成员=移出组；普通=删除</summary>
    private void DeleteItem(LaunchItem item, LaunchItem? group)
    {
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Settings) return; // 设置磁贴不可删除
        if (group is not null)
        {
            group.Members.Remove(item);
            if (group.Members.Count < 2)
            {
                // 组只剩 1 个成员：解散，剩余成员回到组的位置
                var gi = _items.IndexOf(group);
                _items.Remove(group);
                if (ReferenceEquals(_expanded, group)) _expanded = null;
                foreach (var m in group.Members) _items.Insert(Math.Min(gi + 1, _items.Count), m);
                group.Members.Clear();
            }
        }
        else if (item.Kind == LaunchKind.Group)
        {
            // 展开中的组：仅删除组本体，成员回到顶层原位；未展开的组：整组删除（含全部成员）
            var gi = _items.IndexOf(item);
            _items.Remove(item);
            if (ReferenceEquals(_expanded, item))
            {
                _expanded = null;
                for (var k = 0; k < item.Members.Count; k++)
                    _items.Insert(Math.Min(gi + k, _items.Count), item.Members[k]);
            }
            item.Members.Clear();
        }
        else
        {
            _marked.Remove(item);
            if (ReferenceEquals(_grabbed, item)) _grabbed = null; // 移动模式抓取中被删：复位抓取状态
            _items.Remove(item);
        }

        if (_selected >= _display.Count) _selected = Math.Max(0, _display.Count - 1);
        ConfigService.Save(_items.ToList());
        BuildDisplay();
        LayoutTiles();
        UpdateSelection();
        UpdateHintBar();
    }

    private void LaunchItem(LaunchItem item)
    {
        LaunchService.Launch(item);
        if (SettingsService.Current.HideAfterLaunch) App.Main?.HideToTray();
    }

    /// <summary>Y=关闭：结束选中磁贴对应的运行中程序；未运行则提示（小组件/组磁贴不响应；修改器小组件=关闭修改器）</summary>
    private void CloseSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        if (group is not null || item.Kind is LaunchKind.Widget or LaunchKind.Group)
        {
            // 修改器小组件例外：Y 键关闭正在运行的修改器
            if (group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Trainer)
                CloseItem(item);
            return;
        }
        CloseItem(item);
    }

    /// <summary>关闭磁贴对应的前台窗口（Y 键与长按共用）：程序/网站/文件夹；修改器小组件=关闭修改器</summary>
    private void CloseItem(LaunchItem item)
    {
        // 系统小组件：一个磁贴里没有"可关闭的程序"，Y 交给上层做提示（避免提示"未找到运行中的程序"）
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.System)
        {
            ShowToast("系统小组件：右摇杆←→切换项 · A 开关/打开 · LB/RB 调整");
            return;
        }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Trainer)
        {
            var closed = TrainerService.CloseTrainer();
            ShowToast(closed > 0 ? "已关闭修改器" : "修改器未在运行",
                closed > 0 ? ToastKind.Success : ToastKind.Warning);
            if (closed > 0) App.Main?.KeepForeground(); // 目标关闭后启动器保持前台不最小化
            return;
        }

        var killed = LaunchService.Close(item);
        ShowToast(killed > 0 ? $"已关闭 {item.Name}" : $"{item.Name} 未在运行",
            killed > 0 ? ToastKind.Success : ToastKind.Warning);
        if (killed > 0) App.Main?.KeepForeground(); // 目标关闭后启动器保持前台不最小化
    }

    private void PersistLayout()
    {
        ConfigService.Save(_items.ToList());
    }

    /// <summary>LB/RB 翻页（页内竖直滚动）</summary>
    public void PageScroll(bool forward)
    {
        TileScroll.ChangeView(null,
            TileScroll.VerticalOffset + (forward ? 1 : -1) * (TileScroll.ViewportHeight - 40), null);
    }

    /// <summary>重建页码圆点（每页一枚，当前页高亮）。默认隐藏，仅在切页时由 ShowPageDots 短暂显示</summary>
    private void LayoutPageDots()
    {
        PageDots.Children.Clear();
        var n = PageCount();
        PageDots.Visibility = Visibility.Collapsed;
        if (n <= 1) return;
        for (var i = 0; i < n; i++)
            PageDots.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = new SolidColorBrush(i == _page
                    ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)   // 当前页：不透明白
                    : Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))  // 其它页：半透明白
            });
    }

    /// <summary>切页时短暂显示页码圆点，3 秒后自动隐藏（仅多于一页时）</summary>
    private void ShowPageDots()
    {
        if (PageCount() <= 1) return;
        PageDots.Visibility = Visibility.Visible;
        _pageDotsTimer.Stop();
        _pageDotsTimer.Start();
    }

    /// <summary>触摸横滑切换副页（主页中间左右快滑）：方向与 LT/RT 一致，-1=上一页，1=下一页</summary>
    public void SwitchPageBySwipe(int dir) => SwitchPage(dir);

    /// <summary>
    /// LT/RT 切页：当前页整体沿切换方向滑出并淡出，换页后从反方向滑入（左右滑动动画）。
    /// 页号循环（到头绕回）。切页动画或组展开动画进行中时忽略，避免重入。
    /// </summary>
    private void SwitchPage(int dir)
    {
        if (_pageAnimating || _groupAnimating) return;
        var n = PageCount();
        if (n <= 1) return;

        _pageAnimating = true;
        var width = TileScroll.ViewportWidth > 0 ? TileScroll.ViewportWidth : TileHost.ActualWidth;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 第 1 段：滑出 + 淡出
        var outSb = new Storyboard();
        var outTx = new DoubleAnimation { From = 0, To = -dir * width, Duration = new Duration(TimeSpan.FromMilliseconds(160)), EasingFunction = ease };
        Storyboard.SetTarget(outTx, PageSlide);
        Storyboard.SetTargetProperty(outTx, "X");
        outSb.Children.Add(outTx);
        var outOp = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(160)), EasingFunction = ease };
        Storyboard.SetTarget(outOp, TileScroll);
        Storyboard.SetTargetProperty(outOp, "Opacity");
        outSb.Children.Add(outOp);

        outSb.Completed += (_, _) =>
        {
            // 换页：更新页号并重建本页磁贴（回到顶部）
            _page = ((_page + dir) % n + n) % n;
            _expanded = null;
            _marked.Clear();
            _grabbed = null;
            _inDrawer = false;
            _drawerOwner = null;
            _selected = 0;
            BuildDisplay();
            LayoutTiles();
            LayoutPageDots();
            ShowPageDots();   // 切页时短暂显示页码圆点（3 秒后自动隐藏）
            UpdateSelection();
            TileScroll.ChangeView(null, 0, null, true);
            if (App.Main is { } main) main.CurrentHomePage = _page;

            // 第 2 段：从反方向滑入 + 淡入
            PageSlide.X = dir * width;
            TileScroll.Opacity = 0;
            var inEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            var inSb = new Storyboard();
            var inTx = new DoubleAnimation { From = dir * width, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(200)), EasingFunction = inEase };
            Storyboard.SetTarget(inTx, PageSlide);
            Storyboard.SetTargetProperty(inTx, "X");
            inSb.Children.Add(inTx);
            var inOp = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(200)), EasingFunction = inEase };
            Storyboard.SetTarget(inOp, TileScroll);
            Storyboard.SetTargetProperty(inOp, "Opacity");
            inSb.Children.Add(inOp);
            inSb.Completed += (_, _) =>
            {
                PageSlide.X = 0;
                TileScroll.Opacity = 1;
                _pageAnimating = false;
            };
            inSb.Begin();
        };
        outSb.Begin();
    }

    public void FocusGrid()
    {
        if (_tiles.Count > 0) UpdateSelection();
    }
}
