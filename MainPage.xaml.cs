using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.Devices.Radios;
using GameLocker.Models;
using GameLocker.Services;
using GameLocker.Controls;
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
/// 手柄：方向=选择，A=启动（已运行则最大化置前台；组磁贴=展开/收起），Y=最小化窗口（编辑模式内 Y=删除，组=拆分组，成员=移出组），
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
    private LaunchItem? _lpItem;  // 长按目标，统一执行 Y 动作
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

    /// <summary>当前显示的性能控制磁贴引用（重排时重建；右摇杆切换查看项/读数变化时只改内容）</summary>
    private readonly Dictionary<TileStyle.PerformanceTileRefs, LaunchItem> _perfRefs = [];

    /// <summary>当前显示的性能监控磁贴引用（重排时重建；开关或读数变化时只改文字）</summary>
    private readonly Dictionary<TileStyle.MonitorTileRefs, LaunchItem> _monRefs = [];

    /// <summary>当前显示的下载模式磁贴引用（重排时重建；剩余时间每秒刷新）</summary>
    private readonly Dictionary<TileStyle.DownloadModeTileRefs, LaunchItem> _dlRefs = [];

    /// <summary>当前显示的小黄鸭磁贴引用（重排时重建；安装/运行状态变化时只改文字与圆点）</summary>
    private readonly Dictionary<TileStyle.LosslessTileRefs, LaunchItem> _lossRefs = [];

    /// <summary>当前显示的小猫咪磁贴引用（重排时重建；连接状态/服务器变化时只改文字与圆点）</summary>
    private readonly Dictionary<TileStyle.ProxyTileRefs, LaunchItem> _proxyRefs = [];

    /// <summary>当前显示的屏幕翻译磁贴引用（开关状态变化时只改文字与圆点）</summary>
    private readonly Dictionary<TileStyle.TranslateTileRefs, LaunchItem> _translateRefs = [];

    /// <summary>当前显示的壁纸小组件磁贴引用（重排时重建；value=显示序列索引）</summary>
    private readonly Dictionary<TileStyle.WallpaperTileRefs, int> _wallpaperRefs = [];

    /// <summary>磁贴间距（像素，可在外观设置调节）；磁贴基本单位固定</summary>
    private static double Gap => TileStyle.Gap;
    private const double Unit = TileStyle.Unit;
    /// <summary>最近一次布局的列数与磁贴位置（窗口缩放时据此判断能否只挪位置而不重建）</summary>
    private int _layoutCols = -1;
    private bool _resizeQueued;
    private readonly List<(Border tile, int col, int row)> _placedTiles = new();

    public MainPage()
    {
        InitializeComponent();
        UiSound.Open(); // 提前合成并打开音频设备，第一次手柄按键就有声
        HookStatusBubbles(); // 顶栏状态图标：单击快捷动作，长按进系统设置
        // 已读时间持久化：重启后仍能提示"上次打开通知中心之后到达"的通知；
        // 从未打开过时按"最近一天"起算，避免历史旧通知也亮绿点
        _notificationSeen = ParseSeenTime(SettingsService.Current.NotificationSeenTime)
                            ?? DateTime.Now.AddDays(-1);

        Loaded += async (_, _) =>
        {
            ApplyDrawerWidth(); // 全屏/居中模式下抽屉按窗口宽度比例收窄并居中（须在首次重排之前生效）
            LayoutTiles();
            UpdateSelection();
            RefreshPerformanceCacheAsync(); // 磁贴建好后立即补读性能缓存（含 CPU 频率/睿频），避免显示“不可用”
            await UpgradeIconCacheAsync();
        };

        // 顶栏时钟：每秒刷新（页面离开时停止，避免后台空转）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClock();
            if (_perfOpen)
            {
                UpdatePerformanceCursor();
                _ = RefreshDeviceStatusAsync();
                if (_perfHelpRow != null) ShowPerformanceHelp(_perfHelpRow);
            }
        };

        // 系统小组件读数刷新：音量/亮度/射频被外部改动（系统音量键、设置页、硬件开关）时同步磁贴
        _sysTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _sysTimer.Tick += async (_, _) =>
        {
            if (!_pageWorkActive || _sysRefreshBusy || _sysRefs.Count == 0) return;
            _sysRefreshBusy = true;
            try
            {
                await SystemPanelService.RefreshRadiosAsync();
                if (!_pageWorkActive) return;
                await SystemPanelService.RefreshBrightnessAsync();
                if (_pageWorkActive) RefreshSystemTiles();
            }
            finally { _sysRefreshBusy = false; }
        };

        // 抽屉"自动隐藏"：开关开启时，启动就处于收起状态（由右摇杆↑ / 底部上划呼出）
        _autoHidePrev = SettingsService.Current.DrawerAutoHide;
        _drawerHidden = SettingsService.Current.DrawerAutoHide;

        // 玻璃拟态浮层：ThemeShadow 需要元素有 Z 方向位移才会投影（Z 只影响投影，不影响 X/Y 布局与命中测试）
        DrawerHost.Translation = new System.Numerics.Vector3(0, 0, 32);

        // 页面尺寸变化（窗口/全屏切换）时重算列数重排；连续拖动窗口只合并成一次布局
        SizeChanged += (_, _) =>
        {
            ApplyDrawerWidth();
            QueueResizeLayout();
        };

        // 添加方式遮罩：鼠标点击选项/面板外空白（遮罩层）
        // 自绘卡片不是标准控件：点中即标记已处理，全局点击音效据此发声（点遮罩空白仍安静）
        ChoiceCustom.Tapped += (_, e) => { e.Handled = true; CloseChoice(); App.Main?.RootNavigateAddItem(); };
        ChoiceWidget.Tapped += (_, e) => { e.Handled = true; CloseChoice(); App.Main?.RootNavigateWidget(); };
        ChoiceSubPage.Tapped += (_, e) => { e.Handled = true; CloseChoice(); AddSubPage(); };
        ChoiceMask.Tapped += (_, _) => CloseChoice();

        // 抽屉改名遮罩：点击面板外空白（遮罩层）取消
        RenameMask.Tapped += (_, _) => CancelRenameDrawerTab();

        // 电源菜单遮罩：点空白取消，点某一项直接执行对应电源操作
        PowerMask.Tapped += (_, _) => ClosePowerMenu();
        _powerCards = [PowerSignOut, PowerSleep, PowerHibernate, PowerRestart, PowerShutdown];
        for (var i = 0; i < _powerCards.Length; i++)
        {
            var index = i;
            _powerCards[i].Tapped += (_, e) => { e.Handled = true; ChoosePower(index); };
        }

        // 触屏：弹窗底部放大的提示图标可直接点按，走与手柄按键相同的处理入口
        ChoiceHintBar.HintTapped += key => HandleChoice(GamepadHintBar.ToGamepadButtons(key));
        RenameHintBar.HintTapped += key => HandleRename(GamepadHintBar.ToGamepadButtons(key));
        PowerHintBar.HintTapped += key => HandlePower(GamepadHintBar.ToGamepadButtons(key));
        PerformanceMask.Tapped += (_, _) => ClosePerformanceMenu();
        _perfCards = [PerfEco, PerfBalanced, PerfPerformance];
        for (var i = 0; i < _perfCards.Length; i++)
        {
            var index = i;
            // 点一次选中，再点一次应用（与添加磁贴卡片一致）；手柄 A 才是主要入口
            _perfCards[i].Tapped += (_, e) => { e.Handled = true; ChoosePerformanceCard(index); };
        }
        BuildPerformanceSettingsUi();
        PerformanceHintBar.HintTapped += key => HandlePerformance(GamepadHintBar.ToGamepadButtons(key));
        WallpaperHintBar.HintTapped += key => HandleWallpaper(GamepadHintBar.ToGamepadButtons(key));
        FileTransferHintBar.HintTapped += key => HandleFileTransfer(GamepadHintBar.ToGamepadButtons(key));
        // 文件传输浮层：点空白处关闭
        FileTransferMask.Tapped += (_, _) => CloseFileTransfer();
        // 搜索浮层：点遮罩关闭
        SearchMask.Tapped += (_, _) => CloseSearch();

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
        // 悬浮提示条本身高度随提示内容变化（提示条换文案/隐藏）：toast 跟着重新定位
        HintBarShell.SizeChanged += (_, _) => UpdateToastOffset();
    }

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _sysTimer;
    private bool _pageWorkActive;
    private bool _sysRefreshBusy;
    private bool _perfRefreshBusy;
    private bool _perfRefreshAgain;   // 刷新进行中又被请求：本轮结束后再补一次
    private bool _imagesReleased;
    private int _backgroundVersion;

    public async void SetBackground(bool background)
    {
        var version = ++_backgroundVersion;
        if (!background)
        {
            if (_imagesReleased)
            {
                _imagesReleased = false;
                LayoutTiles();
            }
            ResumePageWork();
            return;
        }

        PausePageWork();
        // 快速切换时保留图片；持续隐藏后再释放，避开收起动画。
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (version != _backgroundVersion || _pageWorkActive) return;
        ReleaseTileImages(TileHost);
        ReleaseTileImages(DrawerHost);
        SlideshowService.ReleaseImageCache();
        WallpaperService.ReleaseImageCache();
        _imagesReleased = true;
    }

    private static void ReleaseTileImages(DependencyObject root)
    {
        if (root is Image image) image.Source = null;
        if (root is Border { Background: ImageBrush brush }) brush.ImageSource = null;
        if (root is Panel { Background: ImageBrush panelBrush }) panelBrush.ImageSource = null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            ReleaseTileImages(VisualTreeHelper.GetChild(root, i));
    }

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
        // 新通知到达 → 亮绿点（3 秒一次，读注册表开销很小）
        if (_tickCount % 3 == 0) UpdateNotificationDot();
        _tickCount++;
        // 音乐磁贴进度秒级刷新
        if (_musicRefs.Count > 0) RefreshMusicTiles();
        // 性能监控磁贴读数秒级刷新
        if (_monRefs.Count > 0) RefreshMonitorTiles();
        // 下载模式：磁贴剩余时间与遮罩读数秒级刷新
        if (_dlRefs.Count > 0 || _dlRunning) RefreshDownloadTiles();
        if (_dlRunning) UpdateDownloadRunningTexts();
        // 面板打开时每秒同步系统模式；关闭时磁贴每 10 秒刷新缓存。
        if (_perfOpen) _ = RefreshPerformanceStateAsync();
        else if (_perfRefs.Count > 0 && _tickCount % 10 == 0) RefreshPerformanceCacheAsync();
        // 小黄鸭磁贴安装/运行状态秒级刷新
        if (_lossRefs.Count > 0) RefreshLosslessTiles();
        // 小猫咪磁贴连接状态/节点：每 3 秒探测一次内核（异步，不阻塞 UI）
        if (_proxyRefs.Count > 0 && _tickCount % 3 == 0)
        {
            var first = _proxyRefs.Values.First();
            _ = ProxyService.RefreshAsync(first).ContinueWith(_ => QueuePageRefresh(RefreshProxyTiles));
        }
    }

    private int _tickCount;

    /// <summary>顶栏 Wi-Fi / 蓝牙图标：无线电开着=绿色，关着=白色降透明度（点了就能开关，所以按开关状态着色）；
    /// 读不到无线电（无该硬件/无权限）时退回"是否有连接"判断</summary>
    private async void UpdateNetwork()
    {
        var s = SettingsService.Current;
        var wifiOn = await RadioService.IsOnAsync(RadioKind.WiFi) ?? NetworkService.IsWifiConnected();
        var btOn = await RadioService.IsOnAsync(RadioKind.Bluetooth) ?? NetworkService.IsBluetoothConnected();
        UpdateStatusIcon(WifiBubble, WifiIcon, s.ShowWifi, wifiOn);
        UpdateStatusIcon(BluetoothBubble, BluetoothIcon, s.ShowBluetooth, btOn);
    }

    private static void UpdateStatusIcon(Border bubble, Microsoft.UI.Xaml.Controls.FontIcon icon,
                                         bool show, bool on)
    {
        bubble.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        icon.Foreground = new SolidColorBrush(on
            ? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B)   // 绿=开着
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)); // 白=关着
        icon.Opacity = on ? 1.0 : 0.35;                          // 关着时降透明度，一眼看出是"关"
    }

    /// <summary>夜间模式图标：开=琥珀色月牙，关=灰白太阳（不亮白，避免比旁边的图标更抢眼）</summary>
    private void UpdateNightIcon()
    {
        var on = SystemPanelService.NightLightOn;
        SunIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        MoonIcon.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var amber = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB8, 0x6B);   // 琥珀色=已开启
        var gray = Windows.UI.Color.FromArgb(0xFF, 0xB9, 0xBE, 0xC6);    // 灰白=已关闭
        SunIcon.Stroke = new SolidColorBrush(on ? amber : gray);
        MoonIcon.Fill = new SolidColorBrush(on ? amber : gray);
    }

    /// <summary>最近一次"已读"通知的时间：新通知到达时间晚于它才亮绿点</summary>
    private DateTime _notificationSeen = DateTime.Now;

    /// <summary>解析持久化的已读时间（UTC ISO 8601 → 本地时间）；空/异常返回 null</summary>
    private static DateTime? ParseSeenTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>标记通知已读（打开通知中心时调用），并写入设置持久化</summary>
    private void MarkNotificationsSeen()
    {
        _notificationSeen = DateTime.Now;
        NotificationDot.Visibility = Visibility.Collapsed;
        var iso = _notificationSeen.ToUniversalTime().ToString("o");
        SettingsService.Update(s => s.NotificationSeenTime = iso);
    }

    /// <summary>通知小绿点：有比上次打开通知中心更新的通知时显示（读注册表里各应用的最后通知时间）</summary>
    private void UpdateNotificationDot()
    {
        var s = SettingsService.Current;
        NotificationHost.Visibility = s.ShowNotifications ? Visibility.Visible : Visibility.Collapsed;
        if (!s.ShowNotifications)
        {
            NotificationDot.Visibility = Visibility.Collapsed;
            return;
        }

        var last = NotificationService.LastAddedTime();
        NotificationDot.Visibility = last is not null && last > _notificationSeen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>顶栏电池图标：每 10% 一格（Segoe MDL2 Battery0~10），颜色按电量分档；无电池设备/关闭开关时隐藏</summary>
    private void UpdateBattery()
    {
        var pct = BatteryService.GetPercent();
        if (!SettingsService.Current.ShowBattery || pct is null)
        {
            BatteryBubble.Visibility = Visibility.Collapsed;
            return;
        }
        BatteryBubble.Visibility = Visibility.Visible;

        var (glyph, color) = BatteryService.Describe(pct.Value);
        BatteryIcon.Glyph = glyph;
        BatteryIcon.Foreground = new SolidColorBrush(color);
    }

    private void TileHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 组展开/收起动画进行中：LayoutTiles 改 Height 会触发本事件，
        // 若此时重排会 Children.Clear 重建、把正在播放动画的磁贴摘除（展开动画"没效果"的根因）
        if (_groupAnimating) return;
        // 只有高度变化（LayoutTiles 自己撑高）时无需重建；列数只在宽度变化时才会变
        if (e.PreviousSize.Width == e.NewSize.Width) return;
        QueueResizeLayout();
    }

    /// <summary>合并连续 SizeChanged：拖窗时只跑一次布局；列数不变时仅重算位置，不销毁重建磁贴</summary>
    private void QueueResizeLayout()
    {
        if (_resizeQueued) return;
        _resizeQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _resizeQueued = false;
            if (_imagesReleased) return;
            var availW = TileAvailWidth();
            if (availW <= 0) return;
            var cols = Math.Max(2, (int)((availW + Gap) / (Unit + Gap)));
            if (cols != _layoutCols || _placedTiles.Count == 0)
            {
                LayoutTiles();
                return;
            }
            // 列数没变：按磁贴右边界计算整组宽度，不能只取起始列，否则会漏掉宽磁贴的跨度。
            var usedW = 0.0;
            foreach (var p in _placedTiles)
                usedW = Math.Max(usedW, p.col * (Unit + Gap) + p.tile.Width);
            var offsetX = Math.Max(0, (availW - usedW) / 2);
            foreach (var (tile, c, r) in _placedTiles)
            {
                Canvas.SetLeft(tile, offsetX + c * (Unit + Gap));
                Canvas.SetTop(tile, r * (Unit + Gap));
            }
        });
    }

    public LaunchItem? SelectedItem
    {
        get
        {
            // 焦点在抽屉区域：抽屉层 R3 打开抽屉组件编辑页（命名/增删抽屉）；内层 R3 编辑当前聚焦的内层磁贴
            if (_inDrawer) return FocusedDrawerItem() ?? _drawerOwner;
            if (_selected < 0 || _selected >= _display.Count) return null;
            var (item, _) = _display[_selected];
            // 展开组内的成员同样可进编辑页（只改名称/大小/颜色或组件配置，不影响组本身）；
            // 展开的组磁贴本身仍按 R3 编辑组本体
            return item;
        }
    }

    /// <summary>顶栏手柄状态：连接=绿色，未连接=红色</summary>
    public void SetGamepadStatus(bool connected)
    {
        GamepadIcon.Foreground = new SolidColorBrush(connected
            ? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B)   // 绿
            : Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x50, 0x4A)); // 红
    }

    /// <summary>顶栏状态图标的快捷动作 / 对应系统设置页</summary>
    private enum StatusBubbleAction { Wifi, Bluetooth, Battery, Gamepad, Notifications, NightLight, Search }

    /// <summary>给状态气泡挂指针交互：单击=快捷动作，长按 600ms=打开对应 Windows 设置</summary>
    private void HookStatusBubbles()
    {
        HookStatusBubble(WifiBubble, StatusBubbleAction.Wifi);
        HookStatusBubble(BluetoothBubble, StatusBubbleAction.Bluetooth);
        HookStatusBubble(BatteryBubble, StatusBubbleAction.Battery);
        HookStatusBubble(GamepadBubble, StatusBubbleAction.Gamepad);
        HookStatusBubble(NotificationBubble, StatusBubbleAction.Notifications);
        HookStatusBubble(NightBubble, StatusBubbleAction.NightLight);
        HookStatusBubble(SearchBubble, StatusBubbleAction.Search);
        UpdateNightIcon();
        // 状态图标全被设置关闭时，外面那枚玻璃框也一起收起来（宽度归零即视为没有图标）
        StatusIcons.SizeChanged += (_, e) =>
            StatusTray.Visibility = e.NewSize.Width > 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HookStatusBubble(Border bubble, StatusBubbleAction action)
    {
        var idle = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
        var hover = new SolidColorBrush(Windows.UI.Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF));
        var down = new SolidColorBrush(Windows.UI.Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));

        var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        var heldFired = false;   // 已按长按处理过，松开时不再触发单击

        // 触屏/鼠标悬停时的轻微提亮，让这些气泡看起来是可点的
        bubble.PointerEntered += (_, _) => bubble.Background = hover;
        bubble.PointerExited += (_, _) => bubble.Background = idle;

        bubble.PointerPressed += (_, e) =>
        {
            heldFired = false;
            bubble.CapturePointer(e.Pointer);
            bubble.Background = down;
            hold.Start();
        };

        hold.Tick += (_, _) =>
        {
            hold.Stop();
            heldFired = true;
            // 夜间模式/搜索只响应单击，长按不做动作（它们没有对应的系统设置页）
            if (action is not (StatusBubbleAction.NightLight or StatusBubbleAction.Search))
                OpenStatusSetting(action);   // 长按：直接打开对应的 Windows 设置页
        };

        bubble.PointerReleased += (_, e) =>
        {
            hold.Stop();
            bubble.ReleasePointerCapture(e.Pointer);
            bubble.Background = hover;
            if (!heldFired) _ = ActivateStatusBubbleAsync(action);
        };

        bubble.PointerCaptureLost += (_, _) =>
        {
            hold.Stop();
            bubble.Background = idle;
        };
    }

    /// <summary>单击：夜间模式=切换暖色滤镜（不收起启动器），搜索=打开自制搜索浮层，
    /// Wi-Fi/蓝牙 直接开关无线电，通知=打开通知中心，电池/手柄 打开对应系统设置页</summary>
    private async Task ActivateStatusBubbleAsync(StatusBubbleAction action)
    {
        if (action == StatusBubbleAction.NightLight)
        {
            var text = await SystemPanelService.ToggleAsync(SystemPanel.NightLight);
            UpdateNightIcon();
            ShowToast(text, sound: text.Contains("已") ? UiSoundKind.Select : UiSoundKind.Error);
            return;
        }

        if (action == StatusBubbleAction.Search)
        {
            OpenSearch();   // 系统搜索会被开始菜单接管逻辑拦截，这里用自建浮层代替
            return;
        }

        App.Main?.HideToTray();   // 先收起启动器，把系统设置/桌面让出来

        if (action == StatusBubbleAction.Notifications)
        {
            // 等启动器收起动画结束，Win+N 才不会落到启动器自己身上
            await Task.Delay(220);
            NotificationService.OpenCenter();
            MarkNotificationsSeen();   // 打开即视为已读（并持久化，重启后不再亮）
            return;
        }

        if (action is not (StatusBubbleAction.Wifi or StatusBubbleAction.Bluetooth))
        {
            OpenStatusSetting(action);
            return;
        }

        var isWifi = action == StatusBubbleAction.Wifi;
        var name = isWifi ? "Wi-Fi" : "蓝牙";
        var state = await RadioService.ToggleAsync(isWifi ? RadioKind.WiFi : RadioKind.Bluetooth);
        if (state is null)
        {
            // 系统不给关（无权限/无该无线电）：退化成打开对应设置页，不让点击落空
            OpenStatusSetting(action);
            return;
        }

        ShowToast($"{name}已{(state.Value ? "开启" : "关闭")}", sound: UiSoundKind.Select);
        await Task.Delay(400);   // 等系统更新连接状态后再刷新图标颜色
        UpdateNetwork();
        SetGamepadStatus(App.Main?.Gamepad.HasGamepad ?? false);
    }

    /// <summary>搜索浮层是否打开</summary>
    private bool _searchOpen;
    /// <summary>当前命中的结果（回车启动第一条）</summary>
    private List<SearchEntry> _searchHits = [];
    /// <summary>应用/磁贴命中（同步、立即可得）</summary>
    private List<SearchEntry> _appHits = [];
    /// <summary>系统应用命中（shell:AppsFolder：控制面板/设置/商店应用等，异步返回）</summary>
    private List<SearchEntry> _shellHits = [];
    /// <summary>文件命中（来自 Windows 搜索索引，异步返回）</summary>
    private List<SearchEntry> _fileHits = [];
    /// <summary>文件结果是否还在查询中（决定空状态显示"正在搜索"还是"没有找到"）</summary>
    private bool _searchPending;
    /// <summary>文件搜索的取消源：输入变化时取消上一次查询</summary>
    private CancellationTokenSource? _searchCts;

    /// <summary>打开自制搜索浮层（顶栏搜索图标）：启动器不收起，面板浮在主页上方</summary>
    private void OpenSearch()
    {
        if (_searchOpen)
        {
            CloseSearch();
            return;
        }
        _searchOpen = true;
        SearchOverlay.Visibility = Visibility.Visible;
        SearchBox.Text = "";
        SearchList.ItemsSource = null;
        SearchList.Visibility = Visibility.Collapsed;
        SearchHint.Text = "输入名称开始搜索";
        SearchHint.Visibility = Visibility.Visible;
        // 打开即聚焦输入框，可直接键入（触屏会带出系统触摸键盘）。
        // 用低优先级排队，等面板进入可视树后再聚焦，否则同一帧内 Focus 会落空。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => SearchBox.Focus(FocusState.Programmatic));
    }

    /// <summary>搜索面板右上角的关闭图标（X）：与 Esc 等效，触屏/手柄也能关</summary>
    private void OnSearchCloseTapped(object sender, TappedRoutedEventArgs e) => CloseSearch();

    /// <summary>关闭搜索浮层（Esc / 点遮罩 / 启动结果后）；隐藏启动器时也会被调用</summary>
    public void CloseSearch()
    {
        if (!_searchOpen) return;
        _searchOpen = false;
        _searchCts?.Cancel();
        _searchHits = [];
        _appHits = [];
        _shellHits = [];
        _fileHits = [];
        _searchPending = false;
        SearchOverlay.Visibility = Visibility.Collapsed;
        SearchBox.Text = "";
        SearchList.ItemsSource = null;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            _appHits = [];
            _shellHits = [];
            _fileHits = [];
            _searchHits = [];
            _searchPending = false;
            RenderSearchHits();
            return;
        }

        // 应用/磁贴先同步出结果，系统应用与文件交给后台异步补上
        _appHits = CollectSearchHits(query);
        _shellHits = [];
        _fileHits = [];
        _searchHits = _appHits;
        _searchPending = true;
        RenderSearchHits();
        SearchIndexAsync(query);
    }

    /// <summary>后台检索：先查 shell:AppsFolder（控制面板等系统应用），再查 Windows 文件索引；
    /// 停手 250ms 后才查，并丢弃过期返回，避免每个字符都打一次索引</summary>
    private async void SearchIndexAsync(string query)
    {
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            await Task.Delay(250, cts.Token);

            // 快捷方式索引只在后台建一次；等它的同时先查系统应用，避免首次搜索卡 UI
            var indexTask = AppSearchService.GetIndexAsync();
            var shellTask = Task.Run(() => AppSearchService.SearchShellApps(query, 9), cts.Token);

            var index = await indexTask.WaitAsync(cts.Token);
            if (cts.IsCancellationRequested || cts != _searchCts || !_searchOpen) return;

            // 快捷方式结果补进"启动器/磁贴"一组（索引就绪前这一组只有磁贴命中）
            var names = _appHits.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var indexed = AppSearchService.SearchIn(index, query, Math.Max(0, 9 - _appHits.Count))
                .Where(e => !names.Contains(e.Name))
                .ToList();
            if (indexed.Count > 0)
            {
                names.UnionWith(indexed.Select(s => s.Name));
                _appHits = _appHits.Concat(indexed).ToList();
                _searchHits = _appHits;
                RenderSearchHits();
            }

            var shell = await shellTask;
            if (cts.IsCancellationRequested || cts != _searchCts || !_searchOpen) return;

            // 同名已经在前面出现的不重复列出（先出系统应用，文件索引慢，别一起等）
            _shellHits = shell.Where(s => !names.Contains(s.Name)).ToList();
            names.UnionWith(_shellHits.Select(s => s.Name));
            _searchHits = _appHits.Concat(_shellHits).ToList();
            RenderSearchHits();

            var files = await AppSearchService.SearchFilesAsync(query, 9, cts.Token);
            if (cts.IsCancellationRequested || cts != _searchCts || !_searchOpen) return;
            _fileHits = files
                .Where(f => !names.Contains(f.Name))
                .Where(f => !_appHits.Any(a =>
                    string.Equals(a.Target, f.Target, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            _searchHits = _appHits.Concat(_shellHits).Concat(_fileHits).ToList();
            _searchPending = false;
            RenderSearchHits();
            DebugLog.Write($"搜索「{query.Trim()}」: 磁贴/快捷={_appHits.Count} 系统应用={_shellHits.Count} 文件={_fileHits.Count}");
        }
        catch (OperationCanceledException)
        {
            // 输入变化导致的取消：等新的查询结果
        }
    }

    /// <summary>把当前结果刷到列表；没有结果时按"是否还在查文件"给出不同提示</summary>
    private void RenderSearchHits()
    {
        if (_searchHits.Count == 0)
        {
            SearchList.ItemsSource = null;
            SearchList.Visibility = Visibility.Collapsed;
            SearchHint.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? "输入名称开始搜索"
                : _searchPending ? "正在搜索本机…" : $"没有找到与「{SearchBox.Text.Trim()}」匹配的内容";
            SearchHint.Visibility = Visibility.Visible;
            return;
        }
        SearchList.ItemsSource = _searchHits;
        SearchList.Visibility = Visibility.Visible;
        SearchHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>搜索命中：先取启动器里已有的磁贴（名称与图标都是用户自己配的），再用开始菜单/桌面快捷方式补满</summary>
    private List<SearchEntry> CollectSearchHits(string query)
    {
        var q = query.Trim();
        var hits = new List<SearchEntry>();
        foreach (var item in _items)
        {
            if (hits.Count >= 9) break;
            if (item.Kind is not (LaunchKind.App or LaunchKind.Url or LaunchKind.Folder)) continue;
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            if (!item.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            hits.Add(new SearchEntry
            {
                Name = item.Name,
                Detail = item.Kind switch
                {
                    LaunchKind.Url => "启动器 · 网站",
                    LaunchKind.Folder => "启动器 · 文件夹",
                    _ => "启动器 · 磁贴"
                },
                Glyph = item.Glyph,
                Target = item.Target,
                Item = item
            });
        }
        var taken = hits.Select(h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        hits.AddRange(AppSearchService.Search(query, 9 - hits.Count)
            .Where(e => !taken.Contains(e.Name)));
        return hits;
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseSearch();
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && _searchHits.Count > 0)
        {
            e.Handled = true;
            ActivateSearchEntry(_searchHits[0]);
        }
    }

    private void OnSearchItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchEntry entry) ActivateSearchEntry(entry);
    }

    /// <summary>启动搜索到的结果：磁贴走原有启动流程，快捷方式交给 shell；按设置决定是否收起启动器</summary>
    private void ActivateSearchEntry(SearchEntry entry)
    {
        CloseSearch();
        if (entry.Item is { } tile)
        {
            LaunchItem(tile);
            return;
        }
        if (!AppSearchService.Launch(entry))
        {
            ShowToast($"打开失败：{entry.Name}", ToastKind.Warning);
            return;
        }
        if (AutoHideAfterLaunch) App.Main?.HideToTray();
    }

    private void OpenStatusSetting(StatusBubbleAction action)
    {
        App.Main?.HideToTray();   // 长按/点击进设置时同样收起启动器
        var uri = action switch
        {
            StatusBubbleAction.Wifi => "ms-settings:network-wifi",
            StatusBubbleAction.Bluetooth => "ms-settings:bluetooth",
            StatusBubbleAction.Battery => "ms-settings:batterysaver",  // 设置 → 系统 → 电源和电池
            StatusBubbleAction.Notifications => "ms-settings:notifications",
            _ => "ms-settings:bluetooth"                                // 手柄：蓝牙和其他设备（"设备"页）
        };
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开设置页失败 {uri}: {ex.Message}");
        }
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
        _imagesReleased = false;
        Reload(); // 内部 UpdateSelection 会按当前选中项设置底部提示条
        LayoutPageDots();
        // 页面实例可能重建：主动同步手柄连接状态，避免图标回退默认白色
        SetGamepadStatus(App.Main?.Gamepad.HasGamepad ?? false);
        ApplyTopbarVisibility();
        ResumePageWork();
    }

    private void ResumePageWork()
    {
        if (_pageWorkActive) return;
        _pageWorkActive = true;
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
        // 壁纸小组件：定时轮播本地壁纸，换图时只刷新磁贴内容不重建
        WallpaperService.Changed += OnWallpaperChanged;
        WallpaperService.Start();
        RefreshWallpaperTiles();
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
        // 性能控制小组件：后台读一次当前电源方案（磁贴"档位"读数先显示缓存）
        RefreshPerformanceCacheAsync();
        // 全局锁帧：启动时后台读一次，读完刷新磁贴（磁贴可直接显示当前锁帧值）
        _ = FrameLimitService.RefreshCacheAsync().ContinueWith(_ => QueuePageRefresh(RefreshPerformanceTiles));
    }

    /// <summary>系统小组件首次读数：射频 + 亮度都在后台线程读，读完刷新磁贴（避免 UI 线程做 WMI 调用）</summary>
    private async Task InitSystemReadingsAsync()
    {
        if (!_pageWorkActive || _sysRefreshBusy || _sysRefs.Count == 0) return;
        _sysRefreshBusy = true;
        try
        {
            await SystemPanelService.RefreshRadiosAsync();
            if (!_pageWorkActive) return;
            await SystemPanelService.RefreshBrightnessAsync();
            if (_pageWorkActive) RefreshSystemTiles();
        }
        finally { _sysRefreshBusy = false; }
    }

    private void QueuePageRefresh(Action refresh) =>
        DispatcherQueue.TryEnqueue(() => { if (_pageWorkActive) refresh(); });

    private void OnMusicStateChanged() => QueuePageRefresh(RefreshMusicTiles);

    private void OnSlideshowChanged() =>
        QueuePageRefresh(RefreshPhotoTiles);

    private void OnWeatherChanged() =>
        QueuePageRefresh(RefreshWeatherTiles);

    /// <summary>修改器取回进度：事件来自后台线程，marshal 回 UI 线程后用顶部提示条反馈（失败用警告样式）</summary>
    private void OnTrainerStatus(TrainerMessage m) =>
        DispatcherQueue.TryEnqueue(() => ShowToast(m.Text, m.Error ? ToastKind.Warning : ToastKind.Success, m.Progress));

    /// <summary>游戏存档进度：事件来自后台线程，marshal 回 UI 线程后用顶部提示条反馈（失败用警告样式）</summary>
    private void OnBackupStatus(BackupMessage m) =>
        DispatcherQueue.TryEnqueue(() => ShowToast(m.Text, m.Error ? ToastKind.Warning : ToastKind.Success));

    /// <summary>OptiScaler 进度：事件来自后台线程，marshal 回 UI 线程后用顶部提示条反馈（失败用警告样式）</summary>
    private void OnOptiScalerStatus(OptiScalerMessage m) =>
        DispatcherQueue.TryEnqueue(() => ShowToast(m.Text, m.Error ? ToastKind.Warning : ToastKind.Success, m.Progress));

    /// <summary>按设置显示/隐藏顶栏时钟、网络/电池、手柄、通知、夜间模式与搜索图标</summary>
    public void ApplyTopbarVisibility()
    {
        var s = SettingsService.Current;
        ClockPanel.Visibility = s.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        GamepadBubble.Visibility = s.ShowGamepadIcon ? Visibility.Visible : Visibility.Collapsed;
        NotificationHost.Visibility = s.ShowNotifications ? Visibility.Visible : Visibility.Collapsed;
        NightBubble.Visibility = s.ShowNightLight ? Visibility.Visible : Visibility.Collapsed;
        SearchBubble.Visibility = s.ShowSearch ? Visibility.Visible : Visibility.Collapsed;
        UpdateBattery();
        UpdateNetwork();
        UpdateNotificationDot();
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
    /// 呼出由右摇杆↑持续 1 秒（GamepadService 判定）或软件底部上划触发；收起由右摇杆↓/下划即时触发。
    /// 收起时若焦点正在抽屉内则退回磁贴区。</summary>
    public void SetDrawerSummoned(bool show)
    {
        if (!DrawerAutoHideActive) return;
        if (show == !_drawerHidden) return; // 已经是目标状态
        _drawerHidden = !show;
        if (_drawerHidden && _inDrawer) ExitDrawer(); // 收起时焦点不能留在屏幕外的面板上
        AnimateDrawerSlide(_drawerHidden, animate: true);
        // 呼出后选中框直接落在抽屉上（抽屉层），不用再按 ↓ 才能对焦
        if (show) EnterDrawer();
    }

    /// <summary>抽屉面板下移/复位：hidden=true 滑出窗口下缘，false 滑回原位。
    /// animate=false 直接摆位（重绘后校正用，避免闪回可见）。
    /// 收起动画结束后把抽屉行高度归零并折叠，让磁贴区铺满整屏、底部不再被空白条遮住。</summary>
    private void AnimateDrawerSlide(bool hidden, bool animate)
    {
        UpdateToastOffset();

        if (!animate)
        {
            if (hidden)
            {
                DrawerSlide.Y = DrawerHideOffset();
                CollapseDrawerRow();
            }
            else
            {
                ExpandDrawerRow();
                DrawerSlide.Y = 0;
            }
            return;
        }

        if (!hidden)
        {
            // 先恢复布局占位并摆到窗口下方，再滑入，避免从屏幕中间闪出
            ExpandDrawerRow();
            DrawerSlide.Y = DrawerHideOffset();
        }

        var target = hidden ? DrawerHideOffset() : 0;
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
        if (hidden)
        {
            // 滑出完成后再收回抽屉行，磁贴区随即铺满整屏
            sb.Completed += (_, _) =>
            {
                if (!_drawerHidden) return;
                DrawerSlide.Y = 0;
                CollapseDrawerRow();
            };
        }
        sb.Begin();
    }

    /// <summary>收起抽屉：折叠悬浮面板（不参与布局与命中测试）</summary>
    private void CollapseDrawerRow()
    {
        DrawerHost.Visibility = Visibility.Collapsed;
        UpdateToastOffset(); // 底部提示条随磁贴区一起落到底部
    }

    /// <summary>展开抽屉：显示悬浮面板</summary>
    private void ExpandDrawerRow()
    {
        DrawerHost.Visibility = Visibility.Visible;
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

    /// <summary>底部布局随提示条高度同步：提示条钉在窗口最底部，toast 落在它上方；
    /// 抽屉为悬浮面板，展开时 toast 再让到面板上方，避免被面板盖住。</summary>
    private void UpdateToastOffset()
    {
        var barHeight = HintBarShell.Visibility == Visibility.Visible ? HintBarShell.ActualHeight : 0;
        var bar = barHeight > 0 ? barHeight + 26 : 20;
        var drawerHeight = DrawerHost.Visibility == Visibility.Visible && !_drawerHidden
            ? DrawerHost.ActualHeight : 0;
        MoveHintBar.Margin = new Thickness(0, 0, 0, bar + (drawerHeight > 0 ? drawerHeight + 8 : 0));
        // 提示条底边距 18 + 自身高度 + 8 间距，正好等于 barHeight + 26
        DrawerHost.Margin = new Thickness(12, 6, 12, barHeight > 0 ? bar : 8);
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
        ++_backgroundVersion;
        PausePageWork();
    }

    private void PausePageWork()
    {
        if (!_pageWorkActive) return;
        _pageWorkActive = false;
        if (_perfOpen) ClosePerformanceMenu();
        CancelPendingTap();
        _pageDotsTimer.Stop();
        // 双保险：任何导航离开都取消未决长按，避免计时器在后台触发
        CancelLongPress();
        _clockTimer.Stop();
        SystemMediaService.StateChanged -= OnMusicStateChanged;
        SlideshowService.CurrentChanged -= OnSlideshowChanged;
        SlideshowService.Stop();
        WallpaperService.Changed -= OnWallpaperChanged;
        WallpaperService.Stop();
        WeatherService.StateChanged -= OnWeatherChanged;
        TrainerService.StatusChanged -= OnTrainerStatus;
        SaveBackupService.StatusChanged -= OnBackupStatus;
        OptiScalerService.StatusChanged -= OnOptiScalerStatus;
        GamepadRemapService.StateChanged -= OnRemapStateChanged;
        ForegroundWatcherService.StateChanged -= OnForegroundChanged;
        ForegroundWatcherService.Pause();
        _sysTimer.Stop();
    }

    /// <summary>旧版图标缓存经 Shell 放大发糊：首次启动按新算法重渲染一次，完成后刷新磁贴</summary>
    private async Task UpgradeIconCacheAsync()
    {
        if (IconExtractService.CacheUpgraded) return;
        if (!await IconExtractService.UpgradeCachedIconsAsync(_items)) return;
        GameLocker.Models.LaunchItem.ClearImageCache();
        BuildDisplay();
        LayoutTiles();
        UpdateSelection();
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

    /// <summary>项所属页号；设置/电源磁贴固定在第 0 页（保证任意页都能进设置/电源菜单）</summary>
    private static int PageIndexOf(LaunchItem it) =>
        it.Kind == LaunchKind.Widget && it.WidgetKind is WidgetKind.Settings or WidgetKind.Power ? 0 : it.PageIndex;

    // ---- 桌面拖入：快捷方式/程序/网站/文件夹 → 自动识别生成磁贴 ----

    private void OnDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        var hasFiles = e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);
        // 文件传输浮层打开时：拖入 = 加入待发送列表，不再创建主页磁贴
        if (_fileTransferOpen)
        {
            e.AcceptedOperation = hasFiles
                ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
                : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            HighlightFileTransferDrop(hasFiles);
            if (e.DragUIOverride != null) e.DragUIOverride.Caption = "释放以加入待发送";
            return;
        }
        // 仅接受文件拖放（从桌面/资源管理器拖入）
        e.AcceptedOperation = hasFiles
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

    private void OnDragLeave(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (_fileTransferOpen) { HighlightFileTransferDrop(false); return; }
        HighlightDrawerFileDrop(false);
    }

    /// <summary>文件传输浮层拖动文件时的描边反馈</summary>
    private void HighlightFileTransferDrop(bool on)
    {
        if (on)
        {
            FileTransferPanel.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
            FileTransferPanel.BorderThickness = new Thickness(2);
        }
        else
        {
            FileTransferPanel.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            FileTransferPanel.BorderThickness = new Thickness(1.2);
        }
        FileTransferDropZone.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)(on ? 0x30 : 0x10), 0x4C, 0xC2, 0xFF));
        FileTransferDropZone.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)(on ? 0xFF : 0x66), 0x4C, 0xC2, 0xFF));
        FileTransferDropText.Text = on ? "释放以加入待发送" : (_ftDropped.Count == 0 ? "将文件拖到这里" : "继续拖入文件");
    }

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

        // 文件传输浮层打开时：拖入的文件直接加入待发送列表
        if (_fileTransferOpen)
        {
            HighlightFileTransferDrop(false);
            AddDroppedFiles(items
                .Where(x => x.IsOfType(Windows.Storage.StorageItemTypes.File))
                .Select(x => x.Path));
            return;
        }

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

    private void ShowToast(string text, ToastKind kind = ToastKind.Success, double? progress = null, UiSoundKind? sound = null)
    {
        var warn = kind == ToastKind.Warning;
        // 正常关闭仍可显示警告色，但使用开关成功音；真正失败才播放错误音。
        if (SettingsService.Current.UiSoundEnabled && (sound ?? (warn ? UiSoundKind.Error : null)) is { } cue)
            UiSound.Play(cue);
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
        // A/B 的操作反馈音由 MainWindow 的全局音效统一播放（所有页面共用），这里不再重复播放

        // 添加方式选择遮罩 / 抽屉改名遮罩 / 电源菜单 / 文件传输浮层 打开：按键优先交给遮罩
        if (_choiceOpen) return HandleChoice(buttons);
        if (_renaming) return HandleRename(buttons);
        if (_powerOpen) return HandlePower(buttons);
        if (_perfOpen) return HandlePerformance(buttons);
        if (_dlOpen) return HandleDownloadMode(buttons);
        if (_wallpaperOpen) return HandleWallpaper(buttons);
        if (_fileTransferOpen) return HandleFileTransfer(buttons);

        if (!_moving && buttons.HasFlag(GamepadButtons.Y) && PerformanceItemInFocus() is not null)
        {
            OpenPerformanceMenu();
            return true;
        }

        // LT/RT：左右循环切换主页（副页），带滑动动画；本身不参与其它语义
        if (buttons.HasFlag(GamepadButtons.LeftTrigger)) { SwitchPage(-1); return true; }
        if (buttons.HasFlag(GamepadButtons.RightTrigger)) { SwitchPage(1); return true; }

        // 右摇杆↑/↓：抽屉"自动隐藏"的呼出/收起（仅在 抽屉 + 自动隐藏 都开启时接管）。
        // 呼出由 GamepadService 判定"持续上推 1 秒"（防误触）；这里上推只消费、不触发，收起仍即时生效
        if (SettingsService.Current.DrawerAutoHide && SettingsService.Current.DrawerEnabled)
        {
            if (buttons.HasFlag(GamepadButtons.RightStickUp)) return true;
            if (buttons.HasFlag(GamepadButtons.RightStickDown)) { SetDrawerSummoned(false); return true; }
        }

        // 右摇杆←/→：系统小组件在焦点内时循环切换面板项（设置/我的电脑/资源管理器/音量/亮度/WiFi/蓝牙）；
        // 性能控制磁贴在焦点内时在已开启的快捷项之间切换（电源模式 ⇄ 全局锁帧）；编辑模式不响应
        if (!_moving && (buttons.HasFlag(GamepadButtons.RightStickLeft) || buttons.HasFlag(GamepadButtons.RightStickRight)))
        {
            var stickDir = buttons.HasFlag(GamepadButtons.RightStickRight) ? 1 : -1;
            if (SystemItemInFocus() is { } sysItem)
            {
                CycleSystemPanel(sysItem, stickDir);
                return true;
            }
            if (TryCyclePerformanceQuick(stickDir)) return true;
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
            if (buttons.HasFlag(GamepadButtons.LeftShoulder)) { if (!TryAdjustPerformancePower(-1) && !TryAdjustSystem(-1)) CycleDrawer(-1); return true; }
            if (buttons.HasFlag(GamepadButtons.RightShoulder)) { if (!TryAdjustPerformancePower(1) && !TryAdjustSystem(1)) CycleDrawer(1); return true; }
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
        // 回收站磁贴=一键清空回收站；前台应用磁贴=强制结束当前前台应用；系统磁贴无定义（消费按键）；
        // 壁纸磁贴=收藏当前壁纸；否则关闭选中磁贴对应的运行中程序
        if (buttons.HasFlag(GamepadButtons.Y))
        {
            if (_selected >= 0 && _selected < _display.Count)
            {
                HandleItemY(_display[_selected].item);
            }
            return true;
        }
        // 普通模式不响应其他键（防误删）：仅 L3 编辑/移动模式内 Y=删除
        if (buttons.HasFlag(GamepadButtons.LeftShoulder))
        {
            // 选中性能控制（电源模式查看项）：LB=上一档；选中系统小组件：LB=调整（音量/亮度减小）；
            // 选中音乐小组件：LB=上一曲；选中图片组：LB=上一张；否则翻页
            if (TryAdjustPerformancePower(-1)) return true;
            if (TryAdjustSystem(-1)) return true;
            if (IsMusicSelected()) { SystemMediaService.Prev(); return true; }
            if (IsPhotoSelected()) { SlideshowService.Skip(SelectedFolder(), -1); return true; }
            PageScroll(false); return true;
        }
        if (buttons.HasFlag(GamepadButtons.RightShoulder))
        {
            if (TryAdjustPerformancePower(1)) return true;
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

    /// <summary>
    /// 磁贴可视宽度：按页面根宽度扣除 ScrollViewer 的左右内边距。
    /// 不用 ViewportWidth / TileHost.ActualWidth —— 窗口尺寸变化或内容超宽时它们会给出滞后/偏大的值，
    /// 导致列数多算一列，磁贴整体右移、最右列被窗口边缘裁掉。
    /// </summary>
    private double TileAvailWidth()
    {
        var root = Content as FrameworkElement;
        var total = root is { ActualWidth: > 0 } ? root.ActualWidth : TileScroll.ActualWidth;
        var pad = TileScroll.Padding.Left + TileScroll.Padding.Right;
        return Math.Max(0, total - pad);
    }

    /// <summary>自动流式排布：每个磁贴放入第一个能容纳它的空位（按 _display 显示序列）</summary>
    private void LayoutTiles()
    {
        if (_imagesReleased) return;
        TileHost.Children.Clear();
        _placedTiles.Clear();
        _tiles.Clear();
        _musicRefs.Clear();
        _photoRefs.Clear();
        _weatherRefs.Clear();
        _remapRefs.Clear();
        _fgRefs.Clear();
        _sysRefs.Clear();
        _perfRefs.Clear();
        _monRefs.Clear();
        _dlRefs.Clear();
        _lossRefs.Clear();
        _proxyRefs.Clear();
        _translateRefs.Clear();
        _wallpaperRefs.Clear();
        // 磁贴重建：旧的徽章引用失效
        _settingsTile = null;
        _badge = SettingsBadge.None;
        _badgeIcon = null;
        _badgeOrigGlyph = null;
        _addTile = null;
        // 抽屉区域引用由 RenderDrawerPanel 重建

        // 列数以可视区宽度为准（ViewportWidth 已扣除 Padding，保证最右列不被裁切）
        var availW = TileAvailWidth();
        if (availW <= 0)
        {
            _layoutCols = -1; // 首帧未测量：下次尺寸变化必须完整重排
            return;
        }

        var cols = Math.Max(2, (int)((availW + Gap) / (Unit + Gap)));
        _layoutCols = cols;
        var occupied = new HashSet<(int, int)>();
        int maxRow = -1;
        int maxCol = -1;

        for (var i = 0; i < _display.Count; i++)
        {
            var (item, group) = _display[i];
            // 组成员保持自身原有尺寸/样式；音乐小组件固定 2×2，系统小组件固定 2×1，
            // 设置/电源常驻磁贴固定 1×1，其余可编辑尺寸
            var (w, h) = item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Music
                ? (2, 2)
                : item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Wallpaper
                    ? (2, 2)
                : item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.System
                    ? (2, 1)
                    : TileStyle.IsFixedTile(item) ? (1, 1)
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
            _placedTiles.Add((tile, spot.c, spot.r));
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
                _addTile.Tapped += (_, e) => { e.Handled = true; OpenAddFromEdit(); };
                _placedTiles.Add((_addTile, add.c, add.r));
                TileHost.Children.Add(_addTile);
                _tiles.Add(_addTile);
            }
        }

        // 水平居中：整块磁贴按实际占用宽度在可视区内居中（窗口大小改变时随 SizeChanged 重算）；垂直保持靠左上
        var usedW = maxCol < 0 ? 0 : (maxCol + 1) * (Unit + Gap) - Gap;
        var offsetX = Math.Max(0, (availW - usedW) / 2);
        foreach (var (tile, c, r) in _placedTiles)
        {
            Canvas.SetLeft(tile, offsetX + c * (Unit + Gap));
            Canvas.SetTop(tile, r * (Unit + Gap));
        }

        // 清掉上次切页动画可能残留的横向位移：动画被打断（切页途中呼出/隐藏启动器）时
        // PageSlide.X 会停在非零值，导致整块磁贴偏移、最右列被窗口边缘遮住。
        // 切页流程在 LayoutTiles 之后会重新设置滑入起点，因此这里归零不影响切页动画。
        PageSlide.X = 0;
        TileScroll.Opacity = 1;

        // 撑满视口靠 XAML 的 MinWidth/MinHeight 绑定实现（垂直靠左上，暂不居中）；这里只设内容实际高度供超出时滚动
        TileHost.Height = Math.Max(200, (maxRow + 1) * (Unit + Gap));

        // 回收已移除小组件的轮播状态，避免后台继续扫描目录/补下载
        SyncWidgetServices();

        // 主页下方固定抽屉区域：按面板实际宽度重排（内容随宽度换行、高度随内容增长）
        RenderDrawerPanel();

        // 磁贴是每次重排新建的 Border，CreateTile 只画默认/标记/抓取描边；
        // 选中框由 UpdateSelection 叠加。呼出显示、组展开/收起、返回主页等都会触发
        // SizeChanged→LayoutTiles 重建磁贴，若不在此重绘选中框，手柄选中框就会丢失。
        UpdateSelection();
        RefreshSettingsBadge();
    }

    /// <summary>按当前所有磁贴（含未展开的组和其他页）回收轮播服务里已失效的状态，
    /// 删除小组件后不再扫描/下载/轮播；仍在配置里的不回收，避免组展开或翻页时重新扫描</summary>
    private void SyncWidgetServices()
    {
        var folders = new List<string>();
        var wallpapers = new List<LaunchItem>();
        var hasTranslate = false;
        Collect(_items);

        void Collect(IEnumerable<LaunchItem> list)
        {
            foreach (var item in list)
            {
                if (DrawerService.IsDrawer(item))
                {
                    if (item.Drawers is { } pages)
                        foreach (var page in pages) Collect(page.Items);
                    continue;
                }
                if (item.Kind == LaunchKind.Group)
                {
                    Collect(item.Members);
                    continue;
                }
                if (item.Kind != LaunchKind.Widget) continue;
                if (item.WidgetKind == WidgetKind.Photos)
                {
                    if (!string.IsNullOrWhiteSpace(item.WidgetFolder)) folders.Add(item.WidgetFolder);
                }
                else if (item.WidgetKind == WidgetKind.Wallpaper)
                {
                    wallpapers.Add(item);
                }
                else if (item.WidgetKind == WidgetKind.ScreenTranslate)
                {
                    hasTranslate = true;
                }
            }
        }

        SlideshowService.Retain(folders);
        WallpaperService.Retain(wallpapers);
        // 屏幕翻译挂件被删掉后停止实时翻译与悬浮字幕，避免后台继续截屏/OCR
        if (!hasTranslate && ScreenTranslateService.IsRunning) ScreenTranslateService.Stop();
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
            musicTile.Tapped += (_, e) =>
            {
                if (!IsTileButtonSource(e.OriginalSource, musicTile))
                    OnTileTapped(index, () => ActivateAt(index));
            };
            musicTile.DoubleTapped += (_, e) =>
            {
                if (!IsTileButtonSource(e.OriginalSource, musicTile)) HandleDoubleTap(index);
            };
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

        // 壁纸小组件磁贴：铺满当前轮播壁纸；A/点击 = 弹窗询问设为桌面还是锁屏壁纸
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Wallpaper)
        {
            WallpaperService.Register(item);
            var (wallpaperTile, wallpaperRefs) = TileStyle.BuildWallpaperTile(
                width, height, WallpaperService.CurrentImage(item), WallpaperService.Placeholder(item));
            wallpaperRefs.InfoText.Text = WallpaperService.InfoOf(item);
            wallpaperTile.Tag = index;
            _wallpaperRefs[wallpaperRefs] = index;
            wallpaperTile.Tapped += (_, _) => OnTileTapped(index, () => OpenWallpaperMenu(item));
            wallpaperTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(wallpaperTile, item, group, index, false);
            return wallpaperTile;
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

        // 性能控制小组件磁贴：右摇杆←→切换查看档位/CPU 功耗/CPU 温度/GPU 温度，A=打开性能调节弹窗
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Performance)
        {
            var (perfTile, perfRefs) = TileStyle.BuildPerformanceTile(item, width, height, accent, noColor);
            perfTile.Tag = index;
            _perfRefs[perfRefs] = item;
            perfTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            perfTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(perfTile, item, group, index, false);
            TileStyle.FillPerformanceTile(perfRefs, item); // 建好即显示当前查看项（档位先显示缓存）
            return perfTile;
        }

        // 下载模式小组件磁贴：A/点击 = 先设时长与完成动作，再进入熄屏静音的黑色遮罩
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.DownloadMode)
        {
            var (dlTile, dlRefs) = TileStyle.BuildDownloadTile(item, width, height, accent, noColor);
            dlTile.Tag = index;
            _dlRefs[dlRefs] = item;
            dlTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            dlTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(dlTile, item, group, index, false);
            TileStyle.FillDownloadState(dlRefs, item);
            return dlTile;
        }

        // 性能监控小组件磁贴：A/点击 = 开启/关闭置顶监控条，磁贴显示运行状态与读数摘要
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Monitor)
        {
            var (monTile, monRefs) = TileStyle.BuildMonitorTile(item, width, height, accent, noColor);
            monTile.Tag = index;
            _monRefs[monRefs] = item;
            monTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            monTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(monTile, item, group, index, false);
            TileStyle.FillMonitorState(monRefs, item); // 建好即显示当前开关状态
            return monTile;
        }

        // 小黄鸭联动小组件磁贴：A/点击 = 启动并切回游戏后发送补帧热键，磁贴显示安装/运行状态
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.LosslessScaling)
        {
            var (lossTile, lossRefs) = TileStyle.BuildLosslessTile(item, width, height, accent, noColor);
            lossTile.Tag = index;
            _lossRefs[lossRefs] = item;
            lossTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            lossTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(lossTile, item, group, index, false);
            TileStyle.FillLosslessState(lossRefs, item); // 建好即显示安装/运行状态
            return lossTile;
        }

        // 小猫咪小组件磁贴：A/点击 = 开关系统代理连接，Y = 测速并选择最快的服务器
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Clash)
        {
            var (proxyTile, proxyRefs) = TileStyle.BuildProxyTile(item, width, height, accent, noColor);
            proxyTile.Tag = index;
            _proxyRefs[proxyRefs] = item;
            proxyTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            proxyTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(proxyTile, item, group, index, false);
            ProxyService.Poll(item);              // 建好即显示当前连接状态（同步，不阻塞）
            TileStyle.FillProxyTile(proxyRefs, item);
            _ = ProxyService.RefreshAsync(item).ContinueWith(_ => QueuePageRefresh(RefreshProxyTiles));
            return proxyTile;
        }

        // 屏幕翻译小组件磁贴：A/点击 = 开启/关闭实时翻译
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.ScreenTranslate)
        {
            var (trTile, trRefs) = TileStyle.BuildScreenTranslateTile(item, width, height, accent, noColor);
            trTile.Tag = index;
            _translateRefs[trRefs] = item;
            trTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            trTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(trTile, item, group, index, false);
            TileStyle.FillScreenTranslateState(trRefs, item);
            return trTile;
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
        CancelLongPress();
        CancelPendingTap();
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
            // 左内边距 2（+ 标签自身 10 的内边距 = 文字与内容区左对齐于 DrawerPadH）；标签贴基线靠下。
            // 右侧只留少量空隙："+"单独放在标题行最右侧（见下方 header），标签再多也挤不走它
            Padding = new Thickness(2, 0, 8, 0)
        };
        strip.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Bottom
        };

        // 标题行：标签条（占满剩余宽度） + 最右侧"+"新增按钮
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetColumn(strip, 0);
        header.Children.Add(strip);
        var addButton = CreateDrawerAddButton();
        Grid.SetColumn(addButton, 1);
        addButton.Margin = new Thickness(6, 0, DrawerPadH, 0);
        addButton.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(addButton);

        // 标签条基线：贴标签条底部、两端留内边距的细分隔线，把抽屉与上方磁贴区分开。
        // 先加入（在标签条之下），让当前标签的强调色下划线能压在这条线上；
        // 透明模式下抽屉没有玻璃底，基线与外框一起加重，避免整块区域"糊"进窗口
        var strong = SettingsService.Current.DrawerTransparent;
        var divider = new Border
        {
            Height = strong ? 2 : 1,
            Margin = new Thickness(DrawerPadH, 0, DrawerPadH, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(strong
                ? Windows.UI.Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            IsHitTestVisible = false
        };
        Grid.SetRow(divider, 0);
        grid.Children.Add(divider);
        grid.Children.Add(header);

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
        ApplyDrawerSurface(); // 玻璃渐变底 / 透明模式
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

    /// <summary>重建标签条：每个抽屉一个标签（名称 + 当前项强调色下划线）。
    /// 观感：常态标签无底色（干净），悬停微亮，当前项用所选颜色淡底 + 贴基线实色下划线（3px 视觉基线）。
    /// "+"按钮不在这里，固定在标题行最右侧（见 CreateDrawerAddButton）</summary>
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
    }

    /// <summary>"+"新增抽屉按钮（鼠标/触摸直达；手柄用编辑页增删或 X 添加流程）——中性扁平小按钮，不抢标签。
    /// 固定在标题行最右侧，不随标签滚动</summary>
    private Border CreateDrawerAddButton()
    {
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
        return add;
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
            // 性能控制同样用专属磁贴，登记到 _perfRefs 才能响应右摇杆切换 / A 打开调节
            else if (b.Sub.Kind == LaunchKind.Widget && b.Sub.WidgetKind == WidgetKind.Performance)
            {
                var (perfTile, perfRefs) = TileStyle.BuildPerformanceTile(
                    b.Sub, w, h, TileStyle.AccentFor(b.Sub), TileStyle.IsNoColor(b.Sub));
                TileStyle.FillPerformanceTile(perfRefs, b.Sub);
                _perfRefs[perfRefs] = b.Sub;
                tile = perfTile;
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
            _suppressTap = false;
            if (!_moving) StartLongPress(tile, sub, e);
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
            CancelLongPress();
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
            CancelLongPress();
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
            if (ReferenceEquals(_lpTile, tile)) CancelLongPress();
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

    /// <summary>抽屉内磁贴占格（音乐固定 2×2、系统固定 2×1、设置/电源固定 1×1，与主页一致）</summary>
    private static (int Cols, int Rows) InnerCellsOf(LaunchItem it)
        => it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.Music ? (2, 2)
         : it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.System ? (2, 1)
         : TileStyle.IsFixedTile(it) ? (1, 1)
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
        _inDrawer = true;
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
        SyncPerformanceFocus(); // 抽屉内移动光标同样要还原性能磁贴的待应用档位
        // 抽屉层（焦点停在抽屉本身、未进内层磁贴）：整个抽屉面板画白色粗描边，
        // 内层时框落在内层磁贴上，面板恢复默认（拖拽悬停高亮期间不覆盖）
        if (_dragOverDrawer || _drawerRegion is not { } shell) return;
        if (_inDrawer && _drawerFocusPos < 0)
        {
            shell.BorderThickness = new Thickness(3);
            shell.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            ResetDrawerFrame();
        }
    }

    /// <summary>抽屉面板表面：常态=玻璃渐变底 + 投影；透明模式=与窗口一致的透明底、去掉投影，
    /// 仅靠加重的外框与标签基线区分范围</summary>
    private void ApplyDrawerSurface()
    {
        if (SettingsService.Current.DrawerTransparent)
        {
            // 用 Transparent 而不是 null：空白处仍参与命中测试（点面板空白可进入抽屉区域）
            DrawerHost.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            DrawerHost.Shadow = null;
            DrawerHost.Translation = new System.Numerics.Vector3(0, 0, 0);
            return;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop
        {
            Color = Windows.UI.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF),
            Offset = 0
        });
        brush.GradientStops.Add(new GradientStop
        {
            Color = Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF),
            Offset = 1
        });
        DrawerHost.Background = brush;
        DrawerHost.Shadow ??= new ThemeShadow();
        DrawerHost.Translation = new System.Numerics.Vector3(0, 0, 32);
    }

    /// <summary>区域描边复位：玻璃浮层的常态 1px 高光描边；透明模式加重到 2px 更亮描边（焦点进入时由 UpdateDrawerSelection 换强调色）</summary>
    private void ResetDrawerFrame()
    {
        if (_drawerRegion is { } shell)
        {
            var strong = SettingsService.Current.DrawerTransparent;
            shell.BorderThickness = new Thickness(strong ? 2 : 1);
            shell.BorderBrush = new SolidColorBrush(strong
                ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
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

    private static bool IsTileButtonSource(object source, Border tile)
    {
        var current = source as DependencyObject;
        while (current is not null && !ReferenceEquals(current, tile))
        {
            if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void CancelLongPress()
    {
        _lpTimer?.Stop();
        _lpTile = null;
        _lpItem = null;
    }

    private void StartLongPress(Border tile, LaunchItem item, PointerRoutedEventArgs e)
    {
        CancelLongPress();
        _lpTimer ??= new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _lpTimer.Tick -= OnLongPressTick;
        _lpTimer.Tick += OnLongPressTick;
        _lpTile = tile;
        _lpItem = item;
        _lpStart = e.GetCurrentPoint(TileHost).Position;
        _lpPointerId = e.Pointer.PointerId;
        _lpTimer.Start();
    }

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
        // 设置/电源常驻磁贴不可编辑：双击按普通单击处理（不取消挂起的 A/点击动作）
        if (index >= 0 && index < _display.Count)
        {
            var (fixedItem, _) = _display[index];
            if (TileStyle.IsFixedTile(fixedItem)) return;
        }
        if ((DateTime.Now - _lastDoubleAt).TotalMilliseconds < 100) return;
        _lastDoubleAt = DateTime.Now;
        CancelPendingTap();
        // 双击的第二次"按下"已启动 600ms 长按计时并 CapturePointer；切页后磁贴脱离可视树，
        // 第二次 PointerReleased 不再送达 → 计时器不会被取消，会在后台误触发关闭/进入编辑。导航前先取消。
        CancelLongPress();
        OpenEditorAt(index);
    }

    /// <summary>双击磁贴：进入编辑快捷项页（等同 R3）；展开组内的成员同样可编辑</summary>
    private void OpenEditorAt(int index)
    {
        if (index < 0 || index >= _display.Count) return;
        var (item, _) = _display[index];
        if (TileStyle.IsFixedTile(item)) return; // 设置/电源常驻磁贴不可编辑
        App.Main?.NavigateEditItem(item);
    }

    /// <summary>双击抽屉内层磁贴：进编辑页（等同 R3）——据此可改磁贴大小/名称/图标。
    /// 先取消挂起的启动动作，避免"先启动程序再打开编辑页"。</summary>
    private void HandleDoubleTapOnDrawer(LaunchItem sub)
    {
        if ((DateTime.Now - _lastDoubleAt).TotalMilliseconds < 100) return;
        _lastDoubleAt = DateTime.Now;
        CancelPendingTap();
        CancelLongPress();
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

            // 常规模式长按与 Y 共用动作；组成员仅限制编辑/拖拽，不限制组件动作。
            if (!_moving && !IsTileButtonSource(e.OriginalSource, tile))
            {
                StartLongPress(tile, item, e);
                tile.CapturePointer(e.Pointer);
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
                if (Dist(p, _lpStart) > 12) CancelLongPress(); // 移走了=取消长按
                return;
            }
            if (_dragTile is null || !ReferenceEquals(_dragTile, tile) || e.Pointer.PointerId != _dragPointerId) return;
            if (!_dragMoved && Dist(p, _dragStart) < 10) return; // 未超过阈值仍是轻点
            CancelLongPress();
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
                CancelLongPress();
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
                CancelLongPress();
            }
        }), true);
    }

    private void OnLongPressTick(object sender, object args)
    {
        ((Microsoft.UI.Xaml.DispatcherTimer)sender).Stop();
        var item = _lpItem;
        CancelLongPress();
        // 兜底守卫：若已切走（双击进编辑页等），绝不在后台执行关闭程序/进入编辑模式
        if (App.Main?.IsCurrentPage(this) != true) return;
        _suppressTap = true;
        CancelPendingTap();
        // 抽屉长按已触发 Y，后续移动/松手不能再把同一磁贴当作拖拽处理。
        _dragItem = null;
        _dragTile = null;
        _dragMoved = false;
        HandleItemY(item);
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

        var draggedIsFixed = dragged.Kind == LaunchKind.Widget &&
                             dragged.WidgetKind is WidgetKind.Settings or WidgetKind.Power;

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
                if (draggedIsFixed) return; // 设置/电源磁贴拖到设置磁贴：不动
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
        if (item.Kind == LaunchKind.Widget)
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
                GamepadRemapService.IsEnabled(item) ? ToastKind.Success : ToastKind.Warning, sound: UiSoundKind.Select);
        }
        else if (item.WidgetKind == WidgetKind.RecycleBin)
        {
            // 回收站小组件：A/点击 = 打开系统回收站（与启动程序一致：按设置收起窗口，否则置顶窗口会挡住资源管理器）
            RecycleBinService.Open();
            if (AutoHideAfterLaunch) App.Main?.HideToTray();
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
        else if (item.WidgetKind == WidgetKind.Performance)
        {
            // 性能控制小组件：A/点击 = 应用所选快捷键项（睿频为直接开关并保存）；性能面板统一由 Y 打开
            ApplyPerformancePowerMode(item);
        }
        else if (item.WidgetKind == WidgetKind.Power)
        {
            // 电源磁贴：A/点击 = 弹出电源菜单（注销/睡眠/休眠/重启/关机）
            OpenPowerMenu();
        }
        else if (item.WidgetKind == WidgetKind.DownloadMode)
        {
            // 下载模式小组件：A/点击 = 打开进入前设置（下载时长 + 完成后动作）
            OpenDownloadSetup();
        }
        else if (item.WidgetKind == WidgetKind.Monitor)
        {
            // 性能监控小组件：A/点击 = 开启/关闭置顶悬浮监控条
            MonitorService.Toggle();
            RefreshMonitorTiles();
            if (!MonitorService.IsRunning)
            {
                ShowToast("监控已关闭", ToastKind.Warning, sound: UiSoundKind.Select);
            }
            else if (MonitorService.LastError.Length > 0)
            {
                // 未提权且用户取消了 UAC：其余指标正常，仅帧率不可用
                ShowToast("监控已开启（" + MonitorService.LastError + "）", ToastKind.Warning, sound: UiSoundKind.Select);
            }
            else
            {
                ShowToast("监控已开启", ToastKind.Success, sound: UiSoundKind.Select);
            }
        }
        else if (item.WidgetKind == WidgetKind.FileTransfer)
        {
            // 文件传输小组件：A/点击 = 打开局域网传输浮层（搜索设备 / 勾选文件 / 发送与接收）
            OpenFileTransfer(item);
        }
        else if (item.WidgetKind == WidgetKind.LosslessScaling)
        {
            // 小黄鸭联动小组件：A/点击 = 启动并切回游戏后发送补帧热键
            _ = ActivateLosslessAsync(item);
        }
        else if (item.WidgetKind == WidgetKind.Keyboard)
        {
            // 屏幕键盘：A/点击 = 打开/关闭贴边竖向键盘（不抢焦点，字符直接打进当前前台应用）
            if (!OnScreenKeyboard.Toggle(item.KeyboardDockRight, item.KeyboardQwerty,
                    (KeyboardMode)item.KeyboardMode))
                ShowToast("打开屏幕键盘失败：" + OnScreenKeyboard.LastError, ToastKind.Warning);
            else if (SettingsService.Current.UiSoundEnabled) UiSound.Play(UiSoundKind.Select);
        }
        else if (item.WidgetKind == WidgetKind.Clash)
        {
            // 小猫咪：A/点击 = 打开/关闭连接（系统代理开关）
            _ = ToggleProxyAsync(item);
        }
        else if (item.WidgetKind == WidgetKind.Wallpaper)
        {
            // 壁纸小组件：A/点击 = 弹窗询问设为桌面还是锁屏壁纸
            OpenWallpaperMenu(item);
        }
        else if (item.WidgetKind == WidgetKind.ScreenTranslate)
        {
            // 屏幕翻译：A/点击 = 隐藏启动器并进入选窗翻译，再按一次停止
            var on = ScreenTranslateService.Toggle(item);
            RefreshTranslateTiles();
            ShowToast(on ? "点击要翻译的窗口" : "翻译已停止", sound: UiSoundKind.Select);
        }
        else
        {
            // 音乐小组件：A = 播放/暂停系统当前媒体
            SystemMediaService.TogglePlay();
        }
    }

    /// <summary>小黄鸭一键补帧：按结果给出提示，并刷新磁贴状态</summary>
    private async Task ActivateLosslessAsync(LaunchItem item)
    {
        if (LosslessScalingService.IsActivating)
        {
            ShowToast("正在启动小黄鸭…", ToastKind.Warning);
            return;
        }
        var result = await LosslessScalingService.ActivateAsync(item);
        RefreshLosslessTiles();
        // 小黄鸭以管理员身份运行时，普通权限的启动器无法收起它的窗口，提示里说明一下
        var minSuffix = LosslessScalingService.MinimizeBlocked ? "（小黄鸭以管理员运行，窗口无法最小化）" : "";
        var minKind = minSuffix.Length > 0 ? ToastKind.Warning : ToastKind.Success;
        // 游戏里按 A 时启动器已收进托盘，页面内的提示根本看不见，改用独立悬浮提示。
        void Report(string text, ToastKind kind = ToastKind.Success, UiSoundKind? sound = null)
        {
            if (App.Main is { IsShown: false })
                FloatingToastService.Show(text, warning: kind == ToastKind.Warning);
            else
                ShowToast(text, kind, sound: sound);
        }
        switch (result)
        {
            case LosslessScalingService.ScaleResult.NotInstalled:
                Report("未检测到小黄鸭：请先安装，或按 R3 指定程序", ToastKind.Warning);
                break;
            case LosslessScalingService.ScaleResult.LaunchFailed:
                Report("小黄鸭启动失败", ToastKind.Warning);
                break;
            case LosslessScalingService.ScaleResult.NoGame:
                Report("未检测到游戏：小黄鸭已打开并最小化，进入游戏后再按 A 开启插帧", ToastKind.Warning);
                break;
            case LosslessScalingService.ScaleResult.FocusFailed:
                Report("切不回游戏窗口：请进入游戏后再按 A", ToastKind.Warning);
                break;
            case LosslessScalingService.ScaleResult.FrameGenOn:
                Report("已开启插帧" + minSuffix, minKind, UiSoundKind.Select);
                break;
            case LosslessScalingService.ScaleResult.FrameGenOff:
                Report("已关闭插帧" + minSuffix, minKind, UiSoundKind.Select);
                break;
            case LosslessScalingService.ScaleResult.ToggleFailed:
                Report("未能切换插帧：请在小黄鸭里按 Ctrl+Alt+S 确认热键", ToastKind.Warning);
                break;
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
        ShowToast($"{SystemPanelService.Label(panel)}：{message}", sound: SystemPanelService.IsLauncher(panel)
            ? null : message.Contains("已") && !message.Contains("设置页") ? UiSoundKind.Select : UiSoundKind.Error);
        if (SystemPanelService.IsLauncher(panel) && AutoHideAfterLaunch) App.Main?.HideToTray();
    }

    /// <summary>启动/唤醒小黄鸭（小黄鸭小组件 Y 键语义）：启动后最小化，已运行则直接最小化</summary>
    private async void LaunchLossless(LaunchItem item)
    {
        if (!LosslessScalingService.IsInstalled(item))
        {
            ShowToast("未检测到小黄鸭：请先安装，或按 R3 指定程序", ToastKind.Warning);
            return;
        }
        var ok = await LosslessScalingService.LaunchMinimizedAsync(item);
        var hint = ok && LosslessScalingService.MinimizeBlocked ? "（管理员运行，无法最小化）" : "";
        ShowToast(ok ? "已打开小黄鸭（最小化）" + hint : "小黄鸭启动失败", ok && hint.Length == 0 ? ToastKind.Success : ToastKind.Warning);
        RefreshLosslessTiles();
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
        ShowToast(frozen ? $"已冻结 {app.ProcessName}" : $"已解冻 {app.ProcessName}", ToastKind.Success, sound: UiSoundKind.Select);
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
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.System) { ShowToast("系统小组件：右摇杆←→切换 · A 开关 · LB/RB 调整"); return; }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Performance) { OpenPerformanceMenu(); return; } // 性能控制：Y=打开性能面板
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Clash) { _ = RefreshFastestProxyAsync(item); return; }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Wallpaper) { LikeWallpaper(item); return; }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.GamepadMap)
        {
            var wasEnabled = GamepadRemapService.IsEnabled(item);
            GamepadRemapService.Disable(item);
            if (wasEnabled) ShowToast("已关闭映射", ToastKind.Warning, sound: UiSoundKind.Select);
            return;
        }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Settings) { EnterEditMode(); return; }
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Power) return;  // 电源磁贴没有"可关闭"的对象
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.FileTransfer) return; // 文件传输：Y 已用于浮层内刷新列表
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.LosslessScaling) { LaunchLossless(item); return; } // 小黄鸭：Y=启动/唤醒

        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Monitor)
        {
            // 性能监控：Y 键直接关闭监控（未开启时无动作）
            if (MonitorService.IsRunning)
            {
                MonitorService.Stop();
                RefreshMonitorTiles();
                ShowToast("监控已关闭", ToastKind.Warning, sound: UiSoundKind.Select);
            }
            return;
        }
        if (item.Kind is LaunchKind.App or LaunchKind.Url or LaunchKind.Folder
            || item is { Kind: LaunchKind.Widget, WidgetKind: WidgetKind.Trainer })
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

    /// <summary>当前选中的是否为顶层性能控制磁贴（决定提示条显示应用/性能设置）</summary>
    private bool IsPerformanceSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Performance;
    }

    /// <summary>性能磁贴底部提示：按已开启的快捷项动态生成（右摇杆切换项 / LB/RB 动作）</summary>
    private static string PerformanceHintText(LaunchItem item, string head, string tail)
    {
        var enabled = TileStyle.EnabledQuickItems(item);
        var quick = TileStyle.CurrentQuickItem(item);
        var switchText = enabled.Length > 1 ? "←→:切换项|" : "";
        var adjustText = enabled.Length == 0 ? ""
            : quick is TileStyle.PerformanceQuickItem.FrameLimit or TileStyle.PerformanceQuickItem.PowerLimit
                or TileStyle.PerformanceQuickItem.CpuFrequency ? "LB/RB:调整|"
            : quick == TileStyle.PerformanceQuickItem.CpuBoost ? "A:开关|"
            : "LB/RB:选择模式|";
        // 睿频是开关型：A 直接开关并保存，不再显示“A:应用”
        var applyText = quick == TileStyle.PerformanceQuickItem.CpuBoost && enabled.Length > 0 ? "" : "A:应用|";
        return head + switchText + adjustText + applyText + "Y:打开面板|" + tail;
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

    /// <summary>当前选中的是否为顶层小黄鸭小组件磁贴（决定 Y 键是启动/唤醒而不是关闭程序）</summary>
    private bool IsLosslessSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.LosslessScaling;
    }

    /// <summary>当前选中的是否为顶层小猫咪小组件磁贴（决定 Y 键是测速选最快服务器而不是关闭程序）</summary>
    private bool IsProxySelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Clash;
    }

    /// <summary>当前选中的是否为顶层性能监控小组件磁贴（决定 A 键是开/关监控、Y 键是关闭监控）</summary>
    private bool IsMonitorSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Monitor;
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

    // ---- 电源菜单遮罩（电源磁贴 A/点击打开：注销/睡眠/休眠/重启/关机） ----

    private bool _powerOpen;
    private bool _powerBusy;   // 已确认某项、正在执行（防止重复触发）
    private int _powerIndex;
    private Storyboard? _powerIn;
    private Border[] _powerCards = [];

    /// <summary>打开电源菜单：默认停在第一项（最温和的"注销"），A 才执行</summary>
    private void OpenPowerMenu()
    {
        if (_powerOpen) return;
        _powerOpen = true;
        _powerBusy = false;
        _powerIndex = 0;
        PowerOverlay.Visibility = Visibility.Visible;
        UpdatePowerCursor();
        PlayPowerIn();
    }

    private void ClosePowerMenu()
    {
        _powerIn?.Stop();
        PowerPanel.Opacity = 1;
        PowerOverlay.Visibility = Visibility.Collapsed;
        _powerOpen = false;
    }

    /// <summary>电源菜单入场：遮罩淡入 + 面板淡入、轻微放大并自下而上归位（手机电源菜单的弹出感）</summary>
    private void PlayPowerIn()
    {
        var maskFade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(maskFade, PowerMask);
        Storyboard.SetTargetProperty(maskFade, "Opacity");

        var panelFade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180) };
        Storyboard.SetTarget(panelFade, PowerPanel);
        Storyboard.SetTargetProperty(panelFade, "Opacity");

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation { From = 0.92, To = 1, Duration = TimeSpan.FromMilliseconds(230), EasingFunction = ease };
        Storyboard.SetTarget(scaleX, PowerPanelTransform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        var scaleY = new DoubleAnimation { From = 0.92, To = 1, Duration = TimeSpan.FromMilliseconds(230), EasingFunction = ease };
        Storyboard.SetTarget(scaleY, PowerPanelTransform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        var rise = new DoubleAnimation { From = 26, To = 0, Duration = TimeSpan.FromMilliseconds(260), EasingFunction = ease };
        Storyboard.SetTarget(rise, PowerPanelTransform);
        Storyboard.SetTargetProperty(rise, "TranslateY");

        var sb = new Storyboard();
        sb.Children.Add(maskFade);
        sb.Children.Add(panelFade);
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Children.Add(rise);
        _powerIn?.Stop();
        _powerIn = sb;
        sb.Begin();
    }

    // ---- 性能控制遮罩（Windows 三档电源模式及 CPU 设置） ----

    private bool _perfOpen;
    private bool _perfBusy;      // 正在应用设置，防止重复触发
    private bool _perfTouched;   // 用户已操作面板：后台刷新不覆盖待应用选择和焦点
    private int _perfIndex;      // 0=最佳能效 1=平衡 2=最佳性能
    private bool _perfStateRefreshBusy;
    private int _perfSessionVersion;
    private PerformanceService.PerformanceState _perfSystemState = PerformanceService.PerformanceState.Unknown;
    private Storyboard? _perfIn;
    private Border[] _perfCards = [];
    private readonly List<Border> _perfSettingRows = [];
    private readonly List<Border> _perfPowerRows = [];
    private readonly List<Border> _perfCpuRows = [];
    private int _perfFocus;
    private int _perfLastColumn;       // 0=功耗列 1=CPU 列，用于从锁帧行向下进入上次停留的列
    private static string? _gpuName;   // 主显卡名称（WMI 查询一次后缓存）
    private PerformanceService.CpuSettingsState _perfCpuState = new(false, 0, false, 0);
    private bool _perfCpuSyncing;
    private bool _perfCpuLoading;
    private FrameLimitService.State _perfFrameState = new(false, "", 0, "正在检测锁帧后端…");
    private bool _perfFrameLoading, _perfFrameSyncing;
    private Slider? _perfFrameSlider;
    private TextBlock? _perfFrameRead;
    private TextBlock? _perfFrameBrand;   // 全局锁帧卡片右上角的限帧后端标识（NVIDIA / AMD / intel / RTSS）
    private Border? _perfFrameRow;
    private CpuPowerService.State _perfPowerState = new(false, false, "尚未启用", []);
    private bool _perfPowerLoading, _perfPowerSyncing;
    private Border? _perfPowerPill, _perfPowerDot, _perfPowerRow;
    private readonly List<(Border Row, Slider Slider, TextBlock Read)> _perfPowerControls = [];
    private int _perfFrequencyMhz;
    private Slider? _perfFrequencySlider;
    private TextBlock? _perfFrequencyRead;
    private Border? _perfBoostPill;
    private Border? _perfBoostDot;
    private bool? _perfBoostPending;   // null=未改动；true/false=待应用的睿频开关
    private bool _perfSaving;          // 正在执行 X 保存，防止重复提交
    private ComboBox? _perfCoreSelection;
    private ComboBox? _perfCoreMode;
    private bool _perfCoreSyncing;     // 打开面板时回填核心选项，避免误标“待应用”
    private Border? _perfCoreRow;
    private Border? _perfModeRow;
    private const string PerfCoreHint = "仅对本启动器新启动的进程生效，保留所选核的超线程";
    private const string PerfModeHint = "按性能或能效排序选核；全部核心时仅选对应类别";
    private readonly Dictionary<Border, Action> _perfActions = [];
    /// <summary>当前用手柄展开了下拉框的 ComboBox（展开期间方向键上下选、A/B 收起）</summary>
    private ComboBox? _perfOpenCombo;
    private readonly Dictionary<Border, Action<int>> _perfAdjusters = [];
    private readonly Dictionary<Border, TextBlock> _perfRowHints = [];
    private Border? _perfBoostRow;
    private Border? _perfFrequencyRow;
    private Border? _perfHelpRow;
    private int _perfHelpFocus = -1;
    private Microsoft.UI.Xaml.DispatcherTimer? _perfStatusTimer;   // 面板内状态提示的自动恢复计时
    private bool _perfTelemetryLoading;
    private int _perfTelemetryVersion;

    /// <summary>设备状态数值按数据类型着色：CPU 系暖色、GPU 系冷色、电池绿色、核心数中性；无数据弱化</summary>
    private static void Stamp(TextBlock target, double value, string unit, Brush color)
    {
        var ok = double.IsFinite(value) && value >= 0;
        target.Text = ok ? $"{value:0.0} {unit}" : "—";
        target.Foreground = ok ? color : TelemetryDimBrush;
    }

    private static readonly SolidColorBrush TelemetryCpuPowerBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9A, 0x4D));
    private static readonly SolidColorBrush TelemetryCpuTempBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB3, 0x66));
    private static readonly SolidColorBrush TelemetryCpuFreqBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xC1, 0x4E));
    private static readonly SolidColorBrush TelemetryGpuPowerBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x8E, 0x7B, 0xFF));
    private static readonly SolidColorBrush TelemetryGpuTempBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xB9, 0xA2, 0xFF));
    private static readonly SolidColorBrush TelemetryGpuFreqBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x7A, 0xB8, 0xFF));
    private static readonly SolidColorBrush TelemetryBatteryBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x66, 0xE0, 0x8A));
    private static readonly SolidColorBrush TelemetryCoresBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xC9, 0xCF, 0xD8));
    private static readonly SolidColorBrush TelemetryDimBrush =
        new(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    /// <summary>未读取 / 读取失败：数值统一回到「—」并用弱化色，避免残留上一次颜色</summary>
    private void ClearTelemetryValues()
    {
        foreach (var block in new[]
                 {
                     PerfTelemetryPower, PerfTelemetryCpuTemp, PerfTelemetryGpuTemp,
                     PerfTelemetryCpuFreq, PerfTelemetryGpuPower, PerfTelemetryGpuFreq,
                     PerfTelemetryBatteryPower, PerfTelemetryCores
                 })
        {
            block.Text = "—";
            block.Foreground = TelemetryDimBrush;
        }
        PerfTelemetryBatteryLabel.Text = "电池功耗";
    }

    private async Task RefreshDeviceStatusAsync()
    {
        if (!_perfOpen || _perfTelemetryLoading) return;
        var version = _perfTelemetryVersion;
        _perfTelemetryLoading = true;
        try
        {
            var sample = await Task.Run(MonitorService.ReadDeviceStatus);
            if (!_perfOpen || version != _perfTelemetryVersion || sample is null) return;
            Stamp(PerfTelemetryPower, sample.CpuPower, "W", TelemetryCpuPowerBrush);
            Stamp(PerfTelemetryCpuTemp, sample.CpuTemp, "°C", TelemetryCpuTempBrush);
            Stamp(PerfTelemetryGpuTemp, sample.GpuTemp, "°C", TelemetryGpuTempBrush);
            Stamp(PerfTelemetryCpuFreq, sample.CpuMhz / 1000, "GHz", TelemetryCpuFreqBrush);
            Stamp(PerfTelemetryGpuPower, sample.GpuPower, "W", TelemetryGpuPowerBrush);
            Stamp(PerfTelemetryGpuFreq, sample.GpuMhz, "MHz", TelemetryGpuFreqBrush);
            PerfTelemetryBatteryLabel.Text = sample.Charging ? "电池充电功率" : "电池功耗";
            Stamp(PerfTelemetryBatteryPower, sample.BatteryPower, "W", TelemetryBatteryBrush);
            var topology = GameCpuService.Topology;
            if (topology.CoreCount > 0)
            {
                PerfTelemetryCores.Text = $"{topology.CoreCount} 核";
                PerfTelemetryCores.Foreground = TelemetryCoresBrush;
            }
            else
            {
                PerfTelemetryCores.Text = "—";
                PerfTelemetryCores.Foreground = TelemetryDimBrush;
            }
            var missing = new List<string>();
            if (!double.IsFinite(sample.CpuPower)) missing.Add("CPU 功耗计数器不可用");
            if (!double.IsFinite(sample.CpuTemp)) missing.Add("CPU 温度需管理员权限及受支持的传感器");
            if (!double.IsFinite(sample.GpuTemp)) missing.Add("GPU 温度传感器不可用");
            if (!double.IsFinite(sample.CpuMhz)) missing.Add("CPU 频率不可用");
            if (!double.IsFinite(sample.GpuPower)) missing.Add("GPU 功耗传感器不可用");
            if (!double.IsFinite(sample.GpuMhz)) missing.Add("GPU 频率传感器不可用");
            if (!double.IsFinite(sample.BatteryPower)) missing.Add(sample.OnAc && !sample.Charging
                ? "电池未充放电或功率不可用" : "电池功率不可用");
            PerfTelemetryHint.Text = missing.Count == 0
                ? $"更新于 {DateTime.Now:HH:mm:ss} · 每秒刷新"
                : $"更新于 {DateTime.Now:HH:mm:ss} · {missing.Count} 项传感器不可用";
            ToolTipService.SetToolTip(PerfTelemetryHint,
                missing.Count == 0 ? null : string.Join("；", missing));
        }
        catch
        {
            if (_perfOpen && version == _perfTelemetryVersion)
            {
                ClearTelemetryValues();
                PerfTelemetryHint.Text = "设备传感器读取失败，将自动刷新";
                ToolTipService.SetToolTip(PerfTelemetryHint, null);
            }
        }
        finally { _perfTelemetryLoading = false; }
    }

    private void ShowPerformanceHelp(Border row)
    {
        _perfHelpRow = row;
        if (_perfRowHints.TryGetValue(row, out var hint))
            PerfControlHelp.Text = $"{hint.Tag}：{hint.Text}";
        else if (ReferenceEquals(row, PerfEco))
            PerfControlHelp.Text = "最佳能效：省电优先的 Windows 电源模式。";
        else if (ReferenceEquals(row, PerfBalanced))
            PerfControlHelp.Text = "平衡：兼顾性能与功耗的 Windows 电源模式。";
        else if (ReferenceEquals(row, PerfPerformance))
            PerfControlHelp.Text = "最佳性能：性能优先的 Windows 电源模式。";
    }

    /// <summary>面板内可见的状态提示：主页 toast 会被性能浮层遮住，保存结果必须写在面板里</summary>
    private void ShowPerformanceStatus(string text, bool warn)
    {
        PerfControlHelp.Visibility = Visibility.Collapsed;
        PerfStatusText.Text = text;
        PerfStatusText.Foreground = new SolidColorBrush(warn
            ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB9, 0x00)
            : Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
        PerfStatusText.Visibility = Visibility.Visible;
        if (_perfStatusTimer is null)
        {
            _perfStatusTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _perfStatusTimer.Tick += (_, _) =>
            {
                _perfStatusTimer!.Stop();
                PerfStatusText.Visibility = Visibility.Collapsed;
                PerfControlHelp.Visibility = Visibility.Visible;
            };
        }
        _perfStatusTimer.Stop();
        _perfStatusTimer.Start();
    }

    private void ClearPerformanceStatus()
    {
        _perfStatusTimer?.Stop();
        PerfStatusText.Visibility = Visibility.Collapsed;
        PerfControlHelp.Visibility = Visibility.Visible;
    }

    /// <summary>读取主显卡名称：取显存最大的一块，失败时返回"—"</summary>
    private static string ReadGpuName()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, AdapterRAM FROM Win32_VideoController");
            var best = "";
            long bestRam = -1;
            foreach (var o in searcher.Get())
            {
                using var mo = (System.Management.ManagementObject)o;
                var name = mo["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                long ram = 0;
                try { if (mo["AdapterRAM"] is { } r) ram = Convert.ToInt64(r); } catch { }
                if (ram > bestRam) { bestRam = ram; best = name; }
            }
            return string.IsNullOrWhiteSpace(best) ? "—" : ShortGpuName(best);
        }
        catch { return "—"; }
    }

    /// <summary>显卡名称简称：去掉 NVIDIA/AMD/Intel 等冗余前缀与 (R)/(TM) 标记，保留型号核心信息</summary>
    private static string ShortGpuName(string name)
    {
        var s = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        // 前缀按长到短匹配，避免 "NVIDIA GeForce" 只剥掉 "NVIDIA"
        string[] prefixes =
        [
            "NVIDIA GeForce ", "NVIDIA ", "GeForce ",
            "AMD Radeon ", "AMD ", "Radeon ",
            "Intel Arc ", "Intel "
        ];
        foreach (var p in prefixes)
        {
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { s = s[p.Length..]; break; }
        }
        // 尾部 "Graphics" 属于冗余描述（如 Arc A770 Graphics），去掉后为空则保留
        var trimmed = s.EndsWith(" Graphics", StringComparison.OrdinalIgnoreCase)
            ? s[..^" Graphics".Length].TrimEnd() : s;
        return string.IsNullOrWhiteSpace(trimmed) ? s : trimmed;
    }

    private void BuildPerformanceSettingsUi()
    {
        _perfSettingRows.AddRange(_perfCards);
        foreach (var card in _perfCards)
        {
            var helpRow = card;
            card.PointerEntered += (_, _) => ShowPerformanceHelp(helpRow);
            card.GotFocus += (_, _) => ShowPerformanceHelp(helpRow);
        }

        // 小节标题：左侧橙色短竖条 + 字距略放宽的小标题，增强层级感
        void Section(StackPanel host, string title)
        {
            var accent = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9A, 0x4D));
            host.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(4, 4, 0, 0),
                Children =
                {
                    new Border
                    {
                        Width = 3, Height = 12, CornerRadius = new CornerRadius(1.5),
                        Background = accent, VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = title, FontSize = 11.5, Opacity = 0.6,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 40,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            });
        }

        // 滑块统一橙色主题：细轨、橙色进度与圆形手柄
        void StyleSlider(Slider slider)
        {
            slider.Resources["SliderTrackFill"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            slider.Resources["SliderTrackValueFill"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9A, 0x4D));
            slider.Resources["SliderThumbBackground"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB3, 0x66));
            slider.Resources["SliderTrackFillDisabled"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            slider.Resources["SliderTrackValueFillDisabled"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x59, 0xFF, 0x9A, 0x4D));
        }

        // 保留原有紧凑布局，未接入后端的控件明确说明依赖。
        Border Row(StackPanel host, string title, string hint, FrameworkElement control, bool inline = false, bool supported = false)
        {
            var head = new StackPanel { Spacing = 1 };
            head.Children.Add(new TextBlock
            {
                Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var hintText = new TextBlock
            {
                Text = hint, FontSize = 10, Opacity = 0.4, TextWrapping = TextWrapping.Wrap
            };
            hintText.Tag = title;
            if (control is Control ctl) ctl.IsEnabled = supported;
            control.Opacity = supported ? 1 : 0.5;

            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(head);
            if (inline)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                line.Children.Add(control);
            }

            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(line);
            if (!inline) content.Children.Add(control);

            var row = new Border
            {
                Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1.5), Child = content
            };
            row.Tapped += (_, e) => { e.Handled = true; FocusPerformanceRow(row); };
            row.PointerEntered += (_, _) => ShowPerformanceHelp(row);
            row.GotFocus += (_, _) => ShowPerformanceHelp(row);
            _perfSettingRows.Add(row);
            if (ReferenceEquals(host, PerfPowerSettings)) _perfPowerRows.Add(row);
            else if (ReferenceEquals(host, PerfCpuSettings)) _perfCpuRows.Add(row);
            host.Children.Add(row);
            _perfRowHints[row] = hintText;
            if (!supported) _perfActions[row] = () => ShowToast(title + "：" + hint, ToastKind.Warning);
            return row;
        }

        // 统一样式的下拉框：磨砂胶囊 + 细描边，与开关、卡片保持一致的视觉语言
        ComboBox Options(params string[] values)
        {
            var combo = new ComboBox
            {
                Width = 158, MinHeight = 30,
                Padding = new Thickness(10, 3, 6, 3),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF), Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), Offset = 1 }
                    }
                },
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                FontSize = 12,
                IsTabStop = false, UseSystemFocusVisuals = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            foreach (var value in values) combo.Items.Add(value);
            combo.SelectedIndex = 0;
            return combo;
        }
        // 紧凑开关：34×18 胶囊 + 12px 圆点，替代默认的大号 ToggleSwitch
        FrameworkElement Toggle()
        {
            var pill = new Border
            {
                Width = 34, Height = 18, CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            pill.Child = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(2, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            return pill;
        }

        _perfFrameSlider = new Slider { Minimum = 29, Maximum = 144, Value = 29, StepFrequency = 1, IsTabStop = false, UseSystemFocusVisuals = false, IsEnabled = false };
        StyleSlider(_perfFrameSlider);
        _perfFrameRead = new TextBlock { Text = "检测中…", FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };
        var frameBody = new StackPanel { Spacing = 2 };
        frameBody.Children.Add(_perfFrameRead);
        frameBody.Children.Add(_perfFrameSlider);
        _perfFrameRow = Row(PerfGlobalSettings, "全局锁帧", "驱动限帧优先，其次 RTSS", frameBody, supported: true);
        // 卡片右上角：显示当前实际使用的限帧后端标识（与磁贴同一套品牌标识）
        _perfFrameBrand = new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = 20,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(_perfFrameBrand, 1);
        var frameHeaderLine = (Grid)((StackPanel)_perfFrameRow.Child).Children[0];
        frameHeaderLine.Children.Add(_perfFrameBrand);
        _perfFrameSlider.ValueChanged += (_, _) =>
        {
            if (_perfFrameSyncing) return;
            _perfFrameRead.Text = (_perfFrameSlider.Value < _perfFrameState.Minimum ? "关闭" : $"{_perfFrameSlider.Value:0} FPS") + " · 待应用";
        };
        _perfAdjusters[_perfFrameRow] = dir =>
        {
            if (_perfFrameSlider.IsEnabled && !_perfBusy && !_perfFrameLoading)
                _perfFrameSlider.Value = Math.Clamp(_perfFrameSlider.Value + dir * _perfFrameState.Step, _perfFrameSlider.Minimum, _perfFrameSlider.Maximum);
        };

        Section(PerfPowerSettings, "功耗控制");
        _perfPowerPill = (Border)Toggle();
        _perfPowerDot = (Border)_perfPowerPill.Child;
        _perfPowerRow = Row(PerfPowerSettings, "设备功耗", "启用后授权管理员读取，不自动改写", _perfPowerPill, inline: true, supported: true);
        _perfActions[_perfPowerRow] = ToggleCpuPower;
        _perfPowerPill.Tapped += (_, e) => { e.Handled = true; FocusPerformanceRow(_perfPowerRow); ToggleCpuPower(); };
        foreach (var title in new[] { "持续功耗 TDP / PL1", "短时功耗 SPPT / PL2", "瞬时功耗 FPPT" })
        {
            int index = _perfPowerControls.Count;
            var slider = new Slider { Minimum = 1, Maximum = 100, Value = 15, StepFrequency = 1, IsEnabled = false, IsTabStop = false, UseSystemFocusVisuals = false };
            StyleSlider(slider);
            var read = new TextBlock { Text = "未启用", FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };
            var body = new StackPanel { Spacing = 2 };
            body.Children.Add(read);
            body.Children.Add(slider);
            var row = Row(PerfPowerSettings, title, "CPU 原生限制，不代表整机功耗", body, supported: true);
            _perfPowerControls.Add((row, slider, read));
            slider.ValueChanged += (_, _) => { if (!_perfPowerSyncing) read.Text = $"{slider.Value:0} W · 待应用"; };
            _perfAdjusters[row] = dir => { if (slider.IsEnabled && !_perfBusy && !_perfPowerLoading) slider.Value = Math.Clamp(slider.Value + dir, slider.Minimum, slider.Maximum); };
        }

        Section(PerfCpuSettings, "CPU");
        _perfBoostPill = (Border)Toggle();
        _perfBoostDot = (Border)_perfBoostPill.Child;
        _perfBoostRow = Row(PerfCpuSettings, "CPU 睿频", "正在读取系统设置…", _perfBoostPill, inline: true, supported: true);
        _perfActions[_perfBoostRow] = ToggleBoost;
        _perfBoostPill.Tapped += (_, e) =>
        {
            e.Handled = true;
            FocusPerformanceRow(_perfBoostRow);
            ToggleBoost();
        };
        _perfFrequencySlider = new Slider
        {
            Minimum = 0.9, Maximum = 5, Value = 0.9, StepFrequency = 0.1,
            IsTabStop = false, UseSystemFocusVisuals = false, IsEnabled = false
        };
        StyleSlider(_perfFrequencySlider);
        _perfFrequencyRead = new TextBlock { Text = "—", FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };
        var frequencyBody = new StackPanel { Spacing = 2 };
        frequencyBody.Children.Add(_perfFrequencyRead);
        frequencyBody.Children.Add(_perfFrequencySlider);
        _perfFrequencyRow = Row(PerfCpuSettings, "CPU 最高频率", "正在读取系统设置…", frequencyBody, supported: true);
        _perfFrequencySlider.ValueChanged += (_, e) =>
        {
            if (_perfCpuSyncing) return;
            _perfFrequencyMhz = e.NewValue < 0.95 ? 0 : (int)Math.Round(e.NewValue * 10) * 100;
            _perfFrequencyRead.Text = _perfFrequencyMhz == 0 ? "不限制 · 待应用" : $"{_perfFrequencyMhz / 1000.0:0.0} GHz · 待应用";
        };
        _perfAdjusters[_perfFrequencyRow] = dir =>
        {
            if (_perfFrequencySlider.IsEnabled)
                _perfFrequencySlider.Value = Math.Clamp(_perfFrequencySlider.Value + dir * 0.1, 0.9, 5);
        };
        var topology = GameCpuService.Topology;
        var settings = SettingsService.Current;
        var coreSelection = Options(Enumerable.Range(0, topology.CoreCount + 1)
            .Select(n => n == 0 ? "全部核心" : $"{n} 个物理核").ToArray());
        coreSelection.SelectedIndex = Math.Clamp(settings.GameCoreCount, 0, topology.CoreCount);
        var coreMode = Options("自动", "性能优先", "效率优先");
        coreMode.SelectedIndex = topology.Hybrid && Enum.IsDefined(settings.GameCoreMode) ? (int)settings.GameCoreMode : 0;
        var coreRow = Row(PerfCpuSettings, "游戏可用核心", topology.Supported ? PerfCoreHint : topology.Message, coreSelection, supported: topology.Supported);
        var modeRow = Row(PerfCpuSettings, "核心调度模式", topology.Hybrid ? PerfModeHint : "未检测到异构核心，仅支持自动模式", coreMode, supported: topology.Supported && topology.Hybrid);
        _perfCoreSelection = coreSelection;
        _perfCoreMode = coreMode;
        _perfCoreRow = coreRow;
        _perfModeRow = modeRow;
        _perfAdjusters[coreRow] = dir =>
        {
            if (coreSelection.IsEnabled && !_perfBusy && !_perfCpuLoading)
                coreSelection.SelectedIndex = Math.Clamp(coreSelection.SelectedIndex + dir, 0, topology.CoreCount);
        };
        _perfAdjusters[modeRow] = dir =>
        {
            if (coreMode.IsEnabled && !_perfBusy && !_perfCpuLoading)
                coreMode.SelectedIndex = Math.Clamp(coreMode.SelectedIndex + dir, 0, 2);
        };
        // A 键打开下拉框（展开后方向键上下选、A/B 收起）
        if (topology.Supported) _perfActions[coreRow] = () => OpenPerformanceCombo(coreSelection);
        if (topology.Supported && topology.Hybrid) _perfActions[modeRow] = () => OpenPerformanceCombo(coreMode);
        coreSelection.SelectionChanged += (_, _) =>
        {
            if (_perfBusy || _perfCoreSyncing) return;
            _perfRowHints[coreRow].Text = $"待应用 {coreSelection.SelectedItem} · {PerfCoreHint}";
            ShowPerformanceHelp(coreRow);
        };
        coreMode.SelectionChanged += (_, _) =>
        {
            if (_perfBusy || _perfCoreSyncing) return;
            _perfRowHints[modeRow].Text = $"待应用 {coreMode.SelectedItem} · {PerfModeHint}";
            ShowPerformanceHelp(modeRow);
        };

        PerfSaveButton.Click += (_, _) => _ = SavePerformanceAsync();
        PerfSaveButton.PointerEntered += (_, _) => PerfControlHelp.Text =
            "保存所有待应用的修改。";
    }

    private void RenderCpuPower()
    {
        _perfPowerSyncing = true;
        try
        {
            bool on = _perfPowerState.Enabled;
            _perfPowerPill!.Opacity = _perfPowerLoading ? 0.5 : 1;
            _perfPowerPill.Background = new SolidColorBrush(on ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9A, 0x4D) : Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            _perfPowerDot!.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _perfPowerDot.Margin = on ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
            var saved = SettingsService.Current;
            _perfRowHints[_perfPowerRow!].Text = !on && saved.CpuPowerPl1 > 0
                ? $"已保存并开机自动恢复：持续 {saved.CpuPowerPl1} W / 短时 {(saved.CpuPowerPl2 > 0 ? saved.CpuPowerPl2 : saved.CpuPowerPl1)} W · 点击可接管调节"
                : _perfPowerState.Message;
            for (int i = 0; i < _perfPowerControls.Count; i++)
            {
                var c = _perfPowerControls[i];
                var limit = i < _perfPowerState.Limits.Length ? _perfPowerState.Limits[i] : null;
                c.Slider.IsEnabled = limit is { Writable: true } && !_perfBusy && !_perfPowerLoading;
                if (limit != null)
                {
                    c.Slider.Minimum = limit.Minimum;
                    c.Slider.Maximum = Math.Max(limit.Minimum, limit.Maximum);
                    c.Slider.Value = Math.Clamp(limit.Watts, c.Slider.Minimum, c.Slider.Maximum);
                    c.Read.Text = $"{limit.Name} · {limit.Watts:0.#} W";
                    _perfRowHints[c.Row].Text = limit.Writable ? $"{limit.Minimum:0}–{limit.Maximum:0} W · 保存后写入并读回" : "固件锁定或接口不支持写入";
                }
                else
                {
                    c.Read.Text = on ? "不支持" : "未启用";
                    _perfRowHints[c.Row].Text = on ? "CPU 或后端不支持此限制" : "请先开启设备功耗并授权";
                }
            }
            if (_perfHelpRow != null && _perfPowerRows.Contains(_perfHelpRow)) ShowPerformanceHelp(_perfHelpRow);
        }
        finally { _perfPowerSyncing = false; }
    }

    private async Task RefreshCpuPowerAsync()
    {
        if (_perfPowerLoading) return;
        _perfPowerLoading = true;
        try { _perfPowerState = await Task.Run(CpuPowerService.Read); }
        finally { _perfPowerLoading = false; RenderCpuPower(); }
    }

    /// <summary>保存当前生效的功耗限制，并注册登录时静默写回硬件的计划任务（重启后自动恢复）</summary>
    private async Task SaveCpuPowerSettingsAsync()
    {
        var state = await Task.Run(CpuPowerService.Read);
        if (!state.Enabled || state.Limits.Length == 0) return;
        CpuPowerAutoApply.RememberOriginals(state);
        var pl1 = (int)Math.Round(state.Limits[0].Watts);
        var pl2 = state.Limits.Length > 1 ? (int)Math.Round(state.Limits[1].Watts) : 0;
        var fppt = state.Limits.Length > 2 ? (int)Math.Round(state.Limits[2].Watts) : 0;
        SettingsService.Update(s =>
        {
            s.CpuPowerPl1 = pl1;
            s.CpuPowerPl2 = pl2;
            s.CpuPowerFppt = fppt;
            s.CpuPowerTakeover = true;
        });
        var task = await Task.Run(CpuPowerService.EnsureAutoApplyTask);
        if (!task.Ok) ShowToast("功耗已保存，但开机自动恢复未注册：" + task.Message, ToastKind.Warning);
    }

    private async void ToggleCpuPower()
    {
        if (!_perfOpen || _perfBusy || _perfSaving || _perfPowerLoading) return;
        _perfPowerLoading = true;
        RenderCpuPower();
        try
        {
            var disabling = _perfPowerState.Enabled;
            var result = await Task.Run(() => disabling ? CpuPowerService.Disable() : CpuPowerService.Enable());
            // 关闭功耗接管：清除保存的目标值，避免下次登录又把限制写回去
            if (disabling && result.Ok)
                SettingsService.Update(s => { s.CpuPowerPl1 = 0; s.CpuPowerPl2 = 0; s.CpuPowerFppt = 0; s.CpuPowerTakeover = false; });
            _perfPowerState = await Task.Run(CpuPowerService.Read);
            ShowToast(result.Message, result.Ok ? ToastKind.Success : ToastKind.Warning);
        }
        finally { _perfPowerLoading = false; RenderCpuPower(); }
    }

    private async void ApplyCpuPower(int index)
    {
        if (!_perfOpen || _perfBusy || _perfPowerLoading) return;
        if (index >= _perfPowerState.Limits.Length || !_perfPowerState.Limits[index].Writable)
        { ShowToast("当前 CPU 功耗接口不可写，请先启用或检查固件限制", ToastKind.Warning); return; }
        double watts = _perfPowerControls[index].Slider.Value;
        _perfBusy = true;
        RenderCpuPower();
        try
        {
            var result = await Task.Run(() => CpuPowerService.Apply(index, watts));
            _perfPowerState = await Task.Run(CpuPowerService.Read);
            bool confirmed = result.Ok && index < _perfPowerState.Limits.Length
                && Math.Abs(_perfPowerState.Limits[index].Watts - watts) <= 0.25;
            ShowToast(confirmed || !result.Ok ? result.Message : "写入后再次读取未能确认目标值，请检查固件覆盖或助手状态", confirmed ? ToastKind.Success : ToastKind.Warning);
            if (confirmed) await SaveCpuPowerSettingsAsync();
        }
        finally { _perfBusy = false; RenderCpuPower(); }
    }

    private async Task RefreshFrameLimitAsync()
    {
        if (_perfFrameLoading) return;
        _perfFrameLoading = true;
        _perfFrameSlider!.IsEnabled = false;
        try
        {
            _perfFrameState = await Task.Run(FrameLimitService.Read);
            _perfFrameSyncing = true;
            int min = _perfFrameState.Minimum, max = _perfFrameState.Maximum, step = _perfFrameState.Step;
            _perfFrameSlider.Minimum = min - step;
            _perfFrameSlider.Maximum = max;
            _perfFrameSlider.StepFrequency = step;
            _perfFrameSlider.Value = _perfFrameState.Fps == 0 ? min - step : Math.Clamp(_perfFrameState.Fps, min, max);
            _perfFrameRead!.Text = !_perfFrameState.Supported ? "不可用" :
                $"{_perfFrameState.Backend} · " + (_perfFrameState.Fps == 0 ? "关闭" : $"{_perfFrameState.Fps} FPS");
            // 卡片右上角同步当前限帧后端标识（与磁贴一致，字号按卡片缩小）
            if (_perfFrameBrand != null)
            {
                var (brandText, brandColor, brandSize) = TileStyle.BackendBrand(_perfFrameState.Backend);
                _perfFrameBrand.Text = brandText;
                _perfFrameBrand.Foreground = new SolidColorBrush(brandColor);
                _perfFrameBrand.FontSize = Math.Round(brandSize * 0.55);
                _perfFrameBrand.Visibility = _perfFrameState.Supported && brandText.Length > 0
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            _perfRowHints[_perfFrameRow!].Text = _perfFrameState.Message + $"；最左端关闭；{min}–{max} FPS · 步长 {step}";
            if (_perfHelpRow == _perfFrameRow) ShowPerformanceHelp(_perfFrameRow!);
        }
        finally
        {
            _perfFrameSyncing = _perfFrameLoading = false;
            _perfFrameSlider.IsEnabled = _perfFrameState.Supported && !_perfBusy;
        }
    }

    private async Task RefreshCpuControlsAsync()
    {
        if (_perfCpuLoading) return;
        _perfCpuLoading = true;
        if (_perfFrequencySlider != null) _perfFrequencySlider.IsEnabled = false;
        try
        {
            _perfCpuState = await Task.Run(PerformanceService.GetCpuSettings);
            _perfFrequencyMhz = _perfCpuState.MaxFrequencyMhz;
            _perfCpuSyncing = true;
            if (_perfFrequencySlider != null)
                _perfFrequencySlider.Value = _perfFrequencyMhz == 0 ? 0.9 : Math.Clamp(_perfFrequencyMhz / 1000.0, 1, 5);
            if (_perfFrequencyRead != null)
                _perfFrequencyRead.Text = !_perfCpuState.FrequencySupported ? "—" : _perfFrequencyMhz == 0 ? "不限制" : $"{_perfFrequencyMhz / 1000.0:0.0} GHz";
            RenderBoostToggle();
            if (_perfBoostRow != null)
                _perfRowHints[_perfBoostRow].Text = _perfCpuState.BoostSupported
                    ? "保存后写入当前电源方案" : "系统不支持睿频设置";
            if (_perfFrequencyRow != null)
                _perfRowHints[_perfFrequencyRow].Text = _perfCpuState.FrequencySupported
                    ? "最左端不限制 · 1–5 GHz" : "系统不支持频率设置";
        }
        finally
        {
            _perfCpuSyncing = false;
            _perfCpuLoading = false;
            if (_perfFrequencySlider != null) _perfFrequencySlider.IsEnabled = _perfCpuState.FrequencySupported && !_perfBusy;
        }
    }

    /// <summary>睿频开关视觉：优先显示待应用值，未改动时显示系统当前值</summary>
    private void RenderBoostToggle()
    {
        if (_perfBoostPill is null || _perfBoostDot is null) return;
        var on = _perfBoostPending ?? (_perfCpuState.BoostSupported && _perfCpuState.BoostMode != 0);
        _perfBoostPill.Opacity = _perfCpuState.BoostSupported ? 1 : 0.5;
        _perfBoostPill.Background = new SolidColorBrush(on
            ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9A, 0x4D)
            : Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        _perfBoostDot.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _perfBoostDot.Margin = on ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
    }

    /// <summary>睿频开关只改待应用状态，按 X 保存时才写入</summary>
    private void ToggleBoost()
    {
        if (!_perfOpen || _perfBusy || _perfSaving || _perfCpuLoading) return;
        if (!_perfCpuState.BoostSupported)
        {
            ShowToast("当前系统未提供睿频设置", ToastKind.Warning);
            return;
        }
        _perfBoostPending = !(_perfBoostPending ?? (_perfCpuState.BoostMode != 0));
        RenderBoostToggle();
        if (_perfBoostRow != null) _perfRowHints[_perfBoostRow].Text = "已切换，待保存";
        if (_perfBoostRow != null) ShowPerformanceHelp(_perfBoostRow);
    }

    /// <summary>X：一次保存所有待应用修改；全部成功后关闭面板，失败项留在面板提示</summary>
    private async Task SavePerformanceAsync()
    {
        if (!_perfOpen || _perfBusy || _perfSaving || _perfCpuLoading || _perfPowerLoading || _perfFrameLoading) return;
        _perfSaving = true;
        PerfSettingsScroll.IsHitTestVisible = false;
        PerfSaveButton.IsEnabled = false;
        var failures = new List<string>();
        var changed = 0;
        var boostFailed = false;
        try
        {
            // 1) Windows 电源模式
            if (_perfSystemState.Available && _perfIndex is >= 0 and <= 2 && (int)_perfSystemState.Level != _perfIndex)
            {
                changed++;
                var (ok, message) = await Task.Run(() => PerformanceService.ApplyLevel((PerformanceLevel)_perfIndex));
                if (!ok) failures.Add(message);
            }

            // 2) 全局锁帧
            if (_perfFrameState.Supported && _perfFrameSlider is not null)
            {
                var fps = _perfFrameSlider.Value < _perfFrameState.Minimum ? 0 : (int)Math.Round(_perfFrameSlider.Value);
                if (fps != _perfFrameState.Fps)
                {
                    changed++;
                    var (ok, message) = await Task.Run(() => FrameLimitService.Apply(_perfFrameState, fps));
                    if (!ok) failures.Add(message);
                }
            }

            // 3) CPU 功耗限制：持续功耗变化时，短时 / 瞬时按当前比例自动跟随，
            //    并始终保证 PL1 ≤ PL2 ≤ FPPT；写入顺序先升后降，避免中间状态被固件拒绝
            if (_perfPowerState.Enabled && _perfPowerState.Limits.Length > 0)
            {
                var limits = _perfPowerState.Limits;
                var values = new double[limits.Length];
                var touched = new bool[limits.Length];
                for (var i = 0; i < limits.Length; i++)
                {
                    var hasSlider = i < _perfPowerControls.Count;
                    values[i] = hasSlider ? _perfPowerControls[i].Slider.Value : limits[i].Watts;
                    touched[i] = hasSlider && limits[i].Writable && Math.Abs(values[i] - limits[i].Watts) > 0.05;
                }
                // 持续功耗改了而短时没手动改：短时按当前比例跟着抬高（至少不低于持续功耗）
                if (touched[0] && limits.Length > 1 && limits[1].Writable && !touched[1])
                {
                    var ratio = limits[0].Watts > 0.5 ? limits[1].Watts / limits[0].Watts : 1.25;
                    values[1] = Math.Clamp(Math.Round(values[0] * Math.Max(1, ratio)),
                        limits[1].Minimum, limits[1].Maximum);
                }
                // 瞬时同理：跟随新的短时功耗
                if (limits.Length > 2 && limits[2].Writable && !touched[2] && (touched[0] || touched[1]))
                {
                    var ratio = limits[1].Watts > 0.5 ? limits[2].Watts / limits[1].Watts : 1.5;
                    values[2] = Math.Clamp(Math.Round(values[1] * Math.Max(1, ratio)),
                        limits[2].Minimum, limits[2].Maximum);
                }
                // 兜底排序：保持 PL1 ≤ PL2 ≤ FPPT
                for (var i = 1; i < limits.Length; i++) values[i] = Math.Max(values[i], values[i - 1]);

                var targets = Enumerable.Range(0, limits.Length)
                    .Where(i => limits[i].Writable && Math.Abs(values[i] - limits[i].Watts) > 0.05)
                    .Select(i => (Index: i, Watts: values[i]))
                    .ToArray();
                changed += targets.Length;
                var (powerOk, powerMessage) = await ApplyPowerLimitsAsync(
                    targets, limits.Select(l => l.Watts).ToArray());
                if (!powerOk) failures.Add(powerMessage);
                else if (targets.Length > 0) await SaveCpuPowerSettingsAsync(); // 记住目标值，重启后自动写回
            }

            // 4) CPU 睿频 / 最高频率
            if (_perfBoostPending is bool boostWanted && (boostWanted ? 2 : 0) != _perfCpuState.BoostMode)
            {
                changed++;
                var (ok, message) = await Task.Run(() => PerformanceService.ApplyCpuSetting(true, boostWanted ? 2 : 0));
                boostFailed = !ok;
                if (!ok) failures.Add(message);
            }
            if (_perfCpuState.FrequencySupported && _perfFrequencyMhz != _perfCpuState.MaxFrequencyMhz)
            {
                changed++;
                var (ok, message) = await Task.Run(() => PerformanceService.ApplyCpuSetting(false, _perfFrequencyMhz));
                if (!ok) failures.Add(message);
            }

            // 5) 游戏可用核心
            var topology = GameCpuService.Topology;
            if (topology.Supported && _perfCoreSelection is { SelectedIndex: >= 0 } core && _perfCoreMode is not null)
            {
                var count = core.SelectedIndex;
                var mode = (GameCoreMode)_perfCoreMode.SelectedIndex;
                var settings = SettingsService.Current;
                if (count != settings.GameCoreCount || (topology.Hybrid && mode != settings.GameCoreMode))
                {
                    changed++;
                    SettingsService.Update(s => { s.GameCoreCount = count; s.GameCoreMode = mode; });
                    var result = await Task.Run(() => GameCpuService.Configure(count, mode));
                    if (!result.Ok) failures.Add(result.Message);
                }
            }
        }
        catch (Exception ex) { failures.Add(ex.Message); }
        finally
        {
            _perfSaving = false;
            if (!boostFailed) _perfBoostPending = null;
            PerfSettingsScroll.IsHitTestVisible = true;
            PerfSaveButton.IsEnabled = true;
        }

        // 读回真实值；失败项保持失败提示，不伪造成功状态
        if (_perfOpen)
        {
            await RefreshPerformanceStateAsync();
            await RefreshFrameLimitAsync();
            await RefreshCpuPowerAsync();
            await RefreshCpuControlsAsync();
        }

        if (changed == 0)
        {
            // 没有改动：什么都不写入，同样按保存成功处理并关闭面板
            ShowToast("已保存并生效", ToastKind.Success);
            ClosePerformanceMenu();
            return;
        }
        if (failures.Count > 0)
        {
            ShowToast("部分设置未生效：" + string.Join("；", failures), ToastKind.Warning);
            ShowPerformanceStatus("部分设置未生效：" + string.Join("；", failures), true);
            return;
        }
        RefreshPerformanceTiles();
        ShowToast("已保存并生效", ToastKind.Success);
        ClosePerformanceMenu();
    }

    /// <summary>
    /// 写入多个功耗限制（持续/短时/瞬时）。先抬高的项按索引从大到小写、再降低的项按索引从小到大写，
    /// 保证任何中间状态都满足 PL1 ≤ PL2 ≤ FPPT，不会被固件/后端以“顺序颠倒”为由拒绝。
    /// </summary>
    private static async Task<(bool Ok, string Message)> ApplyPowerLimitsAsync(
        (int Index, double Watts)[] targets, double[] current)
    {
        if (targets.Length == 0) return (true, "");
        var raises = targets.Where(t => t.Watts > current[t.Index] + 0.05).OrderByDescending(t => t.Index).ToArray();
        var lowers = targets.Where(t => t.Watts < current[t.Index] - 0.05).OrderBy(t => t.Index).ToArray();
        foreach (var (index, watts) in raises.Concat(lowers))
        {
            var result = await Task.Run(() => CpuPowerService.Apply(index, watts));
            if (!result.Ok) return (false, result.Message);
        }
        return (true, "");
    }

    /// <summary>把焦点移到指定行：同步档位索引、高亮并滚动到可见区域</summary>
    private void FocusPerformanceRow(Border row)
    {
        _perfFocus = _perfSettingRows.IndexOf(row);
        _perfLastColumn = _perfPowerRows.Contains(row) ? 0 : _perfCpuRows.Contains(row) ? 1 : _perfLastColumn;
        ShowPerformanceHelp(row);
        if (_perfFocus is >= 0 and < 3) _perfIndex = _perfFocus;
        _perfTouched = true;
        UpdatePerformanceCursor();
        row.StartBringIntoView();
        // 回到模式卡片时滚到最上，确保上方的设备状态完整显示
        if (_perfFocus is >= 0 and < 3) PerfSettingsScroll.ChangeView(null, 0, null, true);
    }

    /// <summary>按视觉行上下移动：卡片 → 锁帧 → 两列内逐行升降，不再跨列跳到另一列底部</summary>
    private void MovePerformanceFocus(int delta)
    {
        if (_perfFocus < 3)
        {
            if (delta > 0) FocusPerformanceRow(_perfSettingRows[3]);
            else PerfSettingsScroll.ChangeView(null, 0, null, true);   // 已在顶部：滚到最上，露出设备状态
            return;
        }
        var current = _perfSettingRows[_perfFocus];
        var inPower = _perfPowerRows.Contains(current);
        var inCpu = _perfCpuRows.Contains(current);
        if (!inPower && !inCpu)
        {
            // 全局锁帧行
            if (delta > 0) FocusPerformanceRow((_perfLastColumn == 0 ? _perfPowerRows : _perfCpuRows)[0]);
            else FocusPerformanceRow(_perfCards[_perfIndex]);
            return;
        }
        var list = inPower ? _perfPowerRows : _perfCpuRows;
        var index = list.IndexOf(current) + delta;
        if (index < 0) FocusPerformanceRow(_perfSettingRows[3]);   // 列顶再上：回到锁帧行
        else if (index < list.Count) FocusPerformanceRow(list[index]);
        // 列底再下：保持不动，避免跳到另一列造成“上下乱跳”
    }

    /// <summary>右列两列之间左右切换（功耗列 ⇄ CPU/GPU 列）；焦点不在右列时返回 false</summary>
    private bool MovePerformanceColumn(int dir)
    {
        if (_perfFocus < 3) return false;
        var current = _perfSettingRows[_perfFocus];
        var inPower = _perfPowerRows.Contains(current);
        var inCpu = _perfCpuRows.Contains(current);
        if (!inPower && !inCpu) return false;   // 不在两列内（如全局锁帧）：返回 false，交给调值
        if (dir > 0 && inPower)
            FocusPerformanceRow(_perfCpuRows[Math.Min(_perfPowerRows.IndexOf(current), _perfCpuRows.Count - 1)]);
        else if (dir < 0 && inCpu)
            FocusPerformanceRow(_perfPowerRows[Math.Min(_perfCpuRows.IndexOf(current), _perfPowerRows.Count - 1)]);
        return true;   // 已在最左/最右列：不跨列，也不调值
    }

    /// <summary>打开性能调节：默认停在当前 Windows 电源模式对应的档位</summary>
    private void OpenPerformanceMenu()
    {
        if (_perfOpen || _perfBusy) return;
        _perfOpen = true;
        ++_perfSessionVersion;
        ++_perfTelemetryVersion;
        ClearTelemetryValues();
        PerfTelemetryHint.Text = "正在读取设备传感器…";
        _ = RefreshDeviceStatusAsync();
        _perfTouched = false;
        _perfBoostPending = null;   // 关闭时未保存的改动全部丢弃
        ClearPerformanceStatus();
        var state = PerformanceService.Cached;
        _perfSystemState = state;
        _perfIndex = Math.Clamp((int)state.Level, 0, 2);
        _perfFocus = _perfIndex;
        _perfHelpFocus = -1;
        // 核心选项回到已保存值，避免上次未保存的选择残留
        var saved = SettingsService.Current;
        _perfCoreSyncing = true;
        try
        {
            if (_perfCoreSelection is not null && _perfCoreSelection.Items.Count > 0)
                _perfCoreSelection.SelectedIndex = Math.Clamp(saved.GameCoreCount, 0, _perfCoreSelection.Items.Count - 1);
            if (_perfCoreMode is not null && _perfCoreMode.Items.Count > 0)
                _perfCoreMode.SelectedIndex = GameCpuService.Topology.Hybrid && Enum.IsDefined(saved.GameCoreMode)
                    ? Math.Clamp((int)saved.GameCoreMode, 0, _perfCoreMode.Items.Count - 1) : 0;
        }
        finally { _perfCoreSyncing = false; }
        PerfSettingsScroll.MaxHeight = Math.Max(200, Math.Min(780, ActualHeight - 230));
        PerformancePanel.Width = Math.Min(640, Math.Max(320, ActualWidth - 48));
        PerfSettingsScroll.ChangeView(null, 0, null, true);
        PerformanceOverlay.Visibility = Visibility.Visible;
        UpdatePerformanceCursor();
        PlayPerformanceIn();
        if (_gpuName is null) _ = LoadGpuNameAsync();
        _ = RefreshPerformanceStateAsync(); // 弹窗打开时后台刷新一次真实状态
        _ = RefreshCpuControlsAsync();
        _ = RefreshFrameLimitAsync();
        _ = RefreshCpuPowerAsync();
    }

    /// <summary>显卡名称 WMI 查询较慢，放到后台，查完再回填“当前设备”</summary>
    private async Task LoadGpuNameAsync()
    {
        var name = await Task.Run(ReadGpuName);
        _gpuName = name;
        PerfDeviceText.Text = name;
    }

    private void ClosePerformanceMenu()
    {
        ClosePerformanceCombo();
        ClearPerformanceStatus();
        _perfIn?.Stop();
        PerformancePanel.Opacity = 1;
        PerformanceOverlay.Visibility = Visibility.Collapsed;
        _perfOpen = false;
        ++_perfSessionVersion;
        ++_perfTelemetryVersion;
    }

    /// <summary>入场：遮罩淡入 + 面板淡入、轻微放大并自下而上归位（与电源菜单一致）</summary>
    private void PlayPerformanceIn()
    {
        var maskFade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(maskFade, PerformanceMask);
        Storyboard.SetTargetProperty(maskFade, "Opacity");

        var panelFade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180) };
        Storyboard.SetTarget(panelFade, PerformancePanel);
        Storyboard.SetTargetProperty(panelFade, "Opacity");

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation { From = 0.92, To = 1, Duration = TimeSpan.FromMilliseconds(230), EasingFunction = ease };
        Storyboard.SetTarget(scaleX, PerformancePanelTransform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        var scaleY = new DoubleAnimation { From = 0.92, To = 1, Duration = TimeSpan.FromMilliseconds(230), EasingFunction = ease };
        Storyboard.SetTarget(scaleY, PerformancePanelTransform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        var rise = new DoubleAnimation { From = 26, To = 0, Duration = TimeSpan.FromMilliseconds(260), EasingFunction = ease };
        Storyboard.SetTarget(rise, PerformancePanelTransform);
        Storyboard.SetTargetProperty(rise, "TranslateY");

        var sb = new Storyboard();
        sb.Children.Add(maskFade);
        sb.Children.Add(panelFade);
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Children.Add(rise);
        _perfIn?.Stop();
        _perfIn = sb;
        sb.Begin();
    }

    /// <summary>手柄 A 展开下拉框：展开后方向键上下切选项、A/B 收起</summary>
    private void OpenPerformanceCombo(ComboBox combo)
    {
        if (!combo.IsEnabled) return;
        _perfOpenCombo = combo;
        combo.DropDownClosed -= OnPerformanceComboClosed;
        combo.DropDownClosed += OnPerformanceComboClosed;
        combo.IsDropDownOpen = true;
    }

    private void OnPerformanceComboClosed(object? sender, object e)
    {
        if (ReferenceEquals(_perfOpenCombo, sender)) _perfOpenCombo = null;
    }

    private void ClosePerformanceCombo()
    {
        var combo = _perfOpenCombo;
        _perfOpenCombo = null;
        if (combo is not null) combo.IsDropDownOpen = false;
    }

    /// <summary>性能弹窗内手柄按键：三档选模式、设置行调值并标记待应用，X 保存，B/View/Y 关闭</summary>
    private bool HandlePerformance(GamepadButtons b)
    {
        if (_perfBusy || _perfSaving) return true;
        // 下拉框已展开：上下选择，A/B 收起（其余按键吞掉，避免误改其它行）
        if (_perfOpenCombo is not null)
        {
            if (b.HasFlag(GamepadButtons.DPadUp) || b.HasFlag(GamepadButtons.DPadDown))
            {
                var step = b.HasFlag(GamepadButtons.DPadDown) ? 1 : -1;
                var combo = _perfOpenCombo;
                combo.SelectedIndex = Math.Clamp(combo.SelectedIndex + step, 0, combo.Items.Count - 1);
                return true;
            }
            if (b.HasFlag(GamepadButtons.A) || b.HasFlag(GamepadButtons.B))
            {
                ClosePerformanceCombo();
                return true;
            }
            return true;
        }
        // 右摇杆左右 = 调值（滑杆 / 下拉框 / 档位），不参与列切换
        if (b.HasFlag(GamepadButtons.RightStickLeft) || b.HasFlag(GamepadButtons.RightStickRight))
        {
            var dir = b.HasFlag(GamepadButtons.RightStickRight) ? 1 : -1;
            if (_perfFocus < 3) FocusPerformanceRow(_perfCards[(_perfFocus + (dir > 0 ? 1 : 2)) % 3]);
            else if (_perfAdjusters.TryGetValue(_perfSettingRows[_perfFocus], out var adjust)) adjust(dir);
            return true;
        }
        if (b.HasFlag(GamepadButtons.DPadUp) || b.HasFlag(GamepadButtons.DPadDown))
        {
            MovePerformanceFocus(b.HasFlag(GamepadButtons.DPadDown) ? 1 : -1);
            return true;
        }
        // 模式卡片左右循环选档；设置行左右在“功耗 ⇄ CPU”两列间切换
        if (b.HasFlag(GamepadButtons.DPadLeft) || b.HasFlag(GamepadButtons.DPadRight))
        {
            var dir = b.HasFlag(GamepadButtons.DPadRight) ? 1 : -1;
            if (_perfFocus < 3) FocusPerformanceRow(_perfCards[(_perfFocus + (dir > 0 ? 1 : 2)) % 3]);
            else if (!MovePerformanceColumn(dir) && _perfAdjusters.TryGetValue(_perfSettingRows[_perfFocus], out var adjust))
                adjust(dir);   // 仅两列之外的行（如全局锁帧）用左右调值
            return true;
        }
        if (b.HasFlag(GamepadButtons.A))
        {
            // 模式卡片：A = 立即切换并应用该档电源模式；开关类行照旧切换；其余行不做写入
            if (_perfFocus is >= 0 and < 3) _ = ApplyPerformanceLevelAtAsync(_perfFocus);
            else if (_perfActions.TryGetValue(_perfSettingRows[_perfFocus], out var action)) action();
            return true;
        }
        if (b.HasFlag(GamepadButtons.X))
        {
            _ = SavePerformanceAsync();
            return true;
        }
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View) || b.HasFlag(GamepadButtons.Y))
        { ClosePerformanceMenu(); return true; }
        return true; // 弹窗打开时吞掉其余按键，避免误触发主页操作
    }

    private void UpdatePerformanceCursor()
    {
        var accentBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xB3, 0x66));
        var idleBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        // 焦点用橙色斜向渐变，未选中用极淡白光渐变，比纯色更有层次
        var accentBg = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x3D, 0xFF, 0x9A, 0x4D), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x12, 0xFF, 0x9A, 0x4D), Offset = 1 }
            }
        };
        var idleBg = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), Offset = 1 }
            }
        };
        for (var i = 0; i < _perfSettingRows.Count; i++)
        {
            var on = i == _perfFocus;
            _perfSettingRows[i].BorderBrush = on ? accentBr : idleBr;
            _perfSettingRows[i].BorderThickness = new Thickness(on ? 2 : 1.5);
            _perfSettingRows[i].Background = on ? accentBg : idleBg;
        }
        var state = _perfSystemState;
        var available = state.Available;
        PerfSystemMode.Text = available
            ? $"当前系统模式：{state.Display}"
            : "当前系统模式：不可用（系统不支持、返回未知模式或读取失败）；无法应用电源模式";

        // 圆点=系统当前生效；待应用=已选中但尚未应用；不可用时三张卡禁用
        var active = available ? (int)state.Level : -1;
        PerfEcoActive.Visibility = active == 0 ? Visibility.Visible : Visibility.Collapsed;
        PerfBalancedActive.Visibility = active == 1 ? Visibility.Visible : Visibility.Collapsed;
        PerfPerformanceActive.Visibility = active == 2 ? Visibility.Visible : Visibility.Collapsed;
        PerfEcoPending.Visibility = available && _perfIndex == 0 && active != 0 ? Visibility.Visible : Visibility.Collapsed;
        PerfBalancedPending.Visibility = available && _perfIndex == 1 && active != 1 ? Visibility.Visible : Visibility.Collapsed;
        PerfPerformancePending.Visibility = available && _perfIndex == 2 && active != 2 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _perfCards.Length; i++)
        {
            _perfCards[i].IsHitTestVisible = available;
            _perfCards[i].Opacity = available ? 1 : 0.5;
        }
        PerfDeviceText.Text = _gpuName ?? "…";
        if (_perfHelpFocus != _perfFocus)
        {
            _perfHelpFocus = _perfFocus;
            ShowPerformanceHelp(_perfSettingRows[_perfFocus]);
        }
    }

    /// <summary>鼠标/手柄点卡片：只切换待应用档位，统一由 X 保存</summary>
    private void ChoosePerformanceCard(int index)
    {
        if (!_perfOpen || _perfBusy || _perfSaving) return;
        if (!_perfSystemState.Available)
        {
            ShowToast("当前系统不支持电源模式，未修改设置", ToastKind.Warning);
            return;
        }
        FocusPerformanceRow(_perfCards[index]);
    }

    /// <summary>A 键在模式卡片上直接切换并应用该档 Windows 电源模式（立即生效）</summary>
    private async Task ApplyPerformanceLevelAtAsync(int index)
    {
        if (_perfBusy || _perfSaving) return;
        if (_perfSystemState.Available && (int)_perfSystemState.Level == index)
        {
            ShowToast("已是当前电源模式");
            return;
        }
        _perfTouched = true; // 期间不被后台同步改写选择
        var (ok, message) = await Task.Run(() => PerformanceService.ApplyLevel((PerformanceLevel)index));
        ShowToast(message, ok ? ToastKind.Success : ToastKind.Warning);
        if (!ok) return;
        await PerformanceService.RefreshCacheAsync();
        _perfTouched = false;
        await RefreshPerformanceStateAsync();
    }

    /// <summary>后台同步系统模式；保留待应用选择，忽略关闭或重开之前的读取结果</summary>
    private async Task RefreshPerformanceStateAsync()
    {
        if (!_perfOpen || _perfBusy || _perfStateRefreshBusy) return;
        var version = _perfSessionVersion;
        _perfStateRefreshBusy = true;
        try
        {
            var state = await PerformanceService.RefreshCacheAsync();
            if (!_perfOpen || version != _perfSessionVersion || _perfBusy) return;
            _perfSystemState = state;
            if (!_perfTouched && state.Available)
            {
                _perfIndex = Math.Clamp((int)state.Level, 0, 2);
                _perfFocus = _perfIndex;
            }
            UpdatePerformanceCursor();
            RefreshPerformanceTiles();
        }
        finally { _perfStateRefreshBusy = false; }
    }

    // ---- 壁纸浮层（壁纸磁贴 A/点击打开：设为桌面壁纸 / 设为锁屏壁纸） ----

    private bool _wallpaperOpen;
    private bool _wallpaperBusy;   // 已确认某项、正在设置（防止重复触发）
    private bool _wallpaperWired;  // 点按事件只挂一次
    private int _wallpaperIndex;
    private Storyboard? _wallpaperIn;
    private Border[] _wallpaperCards = [];
    private LaunchItem? _wallpaperItem;

    /// <summary>打开壁纸浮层：默认停在"桌面壁纸"，A 才执行</summary>
    private void OpenWallpaperMenu(LaunchItem item)
    {
        if (_wallpaperOpen) return;
        _wallpaperOpen = true;
        _wallpaperBusy = false;
        _wallpaperIndex = 0;
        _wallpaperItem = item;
        _wallpaperCards = [WallpaperDesktop, WallpaperLock];
        if (!_wallpaperWired)
        {
            _wallpaperWired = true;
            WallpaperMask.Tapped += (_, _) => CloseWallpaperMenu();
            WallpaperDesktop.Tapped += (_, e) => { e.Handled = true; ChooseWallpaper(0); };
            WallpaperLock.Tapped += (_, e) => { e.Handled = true; ChooseWallpaper(1); };
        }
        WallpaperOverlay.Visibility = Visibility.Visible;
        UpdateWallpaperCursor();
        PlayWallpaperIn();
    }

    private void CloseWallpaperMenu()
    {
        _wallpaperIn?.Stop();
        WallpaperPanel.Opacity = 1;
        WallpaperOverlay.Visibility = Visibility.Collapsed;
        _wallpaperOpen = false;
        _wallpaperItem = null;
    }

    /// <summary>入场：遮罩淡入 + 面板淡入、轻微放大并自下而上归位（与电源菜单一致的手感）</summary>
    private void PlayWallpaperIn()
    {
        var maskFade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(maskFade, WallpaperMask);
        Storyboard.SetTargetProperty(maskFade, "Opacity");

        var panelFade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180) };
        Storyboard.SetTarget(panelFade, WallpaperPanel);
        Storyboard.SetTargetProperty(panelFade, "Opacity");

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation { From = 0.92, To = 1, Duration = TimeSpan.FromMilliseconds(230), EasingFunction = ease };
        Storyboard.SetTarget(scaleX, WallpaperPanelTransform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        var scaleY = new DoubleAnimation { From = 0.92, To = 1, Duration = TimeSpan.FromMilliseconds(230), EasingFunction = ease };
        Storyboard.SetTarget(scaleY, WallpaperPanelTransform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        var rise = new DoubleAnimation { From = 26, To = 0, Duration = TimeSpan.FromMilliseconds(260), EasingFunction = ease };
        Storyboard.SetTarget(rise, WallpaperPanelTransform);
        Storyboard.SetTargetProperty(rise, "TranslateY");

        var sb = new Storyboard();
        sb.Children.Add(maskFade);
        sb.Children.Add(panelFade);
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Children.Add(rise);
        _wallpaperIn?.Stop();
        _wallpaperIn = sb;
        sb.Begin();
    }

    private bool HandleWallpaper(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.DPadUp) || b.HasFlag(GamepadButtons.DPadLeft))
        { _wallpaperIndex = (_wallpaperIndex + _wallpaperCards.Length - 1) % _wallpaperCards.Length; UpdateWallpaperCursor(); return true; }
        if (b.HasFlag(GamepadButtons.DPadDown) || b.HasFlag(GamepadButtons.DPadRight))
        { _wallpaperIndex = (_wallpaperIndex + 1) % _wallpaperCards.Length; UpdateWallpaperCursor(); return true; }
        if (b.HasFlag(GamepadButtons.A)) { ChooseWallpaper(_wallpaperIndex); return true; }
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View) || b.HasFlag(GamepadButtons.X))
        { CloseWallpaperMenu(); return true; }
        return true; // 遮罩打开时吞掉其余按键，避免误触发主页操作
    }

    private void UpdateWallpaperCursor()
    {
        var accentBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
        var idleBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        var accentBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0x4C, 0xC2, 0xFF));
        var idleBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        for (var i = 0; i < _wallpaperCards.Length; i++)
        {
            var on = i == _wallpaperIndex;
            _wallpaperCards[i].BorderBrush = on ? accentBr : idleBr;
            _wallpaperCards[i].BorderThickness = new Thickness(on ? 2 : 1.5);
            _wallpaperCards[i].Background = on ? accentBg : idleBg;
        }
    }

    /// <summary>选中某项：先关遮罩，再把当前壁纸设为桌面（index=0）或锁屏（index=1）</summary>
    private void ChooseWallpaper(int index)
    {
        if (!_wallpaperOpen || _wallpaperBusy) return;
        _wallpaperBusy = true;
        var item = _wallpaperItem;
        CloseWallpaperMenu();
        var file = item is null ? null : WallpaperService.CurrentFile(item);
        if (file is null)
        {
            ShowToast("壁纸还没下载好，请稍后再试", ToastKind.Warning);
            return;
        }
        var (ok, message) = WallpaperService.Apply(file, index == 1);
        ShowToast(message, ok ? ToastKind.Success : ToastKind.Warning);
    }

    // ==================== 文件传输浮层（WidgetKind.FileTransfer）====================

    private bool _fileTransferOpen;
    private int _ftIndex;
    /// <summary>拖入浮层的待发送文件（绝对路径，按拖入顺序）</summary>
    private readonly List<string> _ftDropped = [];

    /// <summary>打开文件传输浮层：启动 UDP 发现 + TCP 监听，并订阅状态变化</summary>
    private void OpenFileTransfer(LaunchItem item)
    {
        if (_fileTransferOpen) return;
        _fileTransferOpen = true;
        _ftIndex = 0;
        _ftDropped.Clear();
        HighlightFileTransferDrop(false);
        FileTransferOverlay.Visibility = Visibility.Visible;
        PlayFileTransferIn();
        FileTransferService.StateChanged += OnFileTransferChanged;
        FileTransferService.Start(item.FileTransferSendFolder, item.FileTransferRecvFolder);
        RenderFileTransfer();
    }

    private void CloseFileTransfer()
    {
        if (!_fileTransferOpen) return;
        _fileTransferOpen = false;
        FileTransferService.StateChanged -= OnFileTransferChanged;
        FileTransferService.Stop();
        _ftDropped.Clear();
        FileTransferOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnFileTransferChanged() =>
        DispatcherQueue.TryEnqueue(() => { if (_fileTransferOpen) RenderFileTransfer(); });

    /// <summary>入场：遮罩淡入 + 面板轻微放大并自下而上归位（与电源菜单一致的手感）</summary>
    private Storyboard? _ftIn;

    private void PlayFileTransferIn()
    {
        var maskFade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(maskFade, FileTransferMask);
        Storyboard.SetTargetProperty(maskFade, "Opacity");

        var panelFade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180) };
        Storyboard.SetTarget(panelFade, FileTransferPanel);
        Storyboard.SetTargetProperty(panelFade, "Opacity");

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation { From = 0.94, To = 1, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = ease };
        Storyboard.SetTarget(scaleX, FileTransferPanelTransform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        var scaleY = new DoubleAnimation { From = 0.94, To = 1, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = ease };
        Storyboard.SetTarget(scaleY, FileTransferPanelTransform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        var rise = new DoubleAnimation { From = 24, To = 0, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = ease };
        Storyboard.SetTarget(rise, FileTransferPanelTransform);
        Storyboard.SetTargetProperty(rise, "TranslateY");

        var sb = new Storyboard();
        sb.Children.Add(maskFade);
        sb.Children.Add(panelFade);
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Children.Add(rise);
        _ftIn?.Stop();
        _ftIn = sb;
        sb.Begin();
    }

    /// <summary>重建设备/文件列表并刷新状态、进度（状态变化时调用，频率已被服务端节流）</summary>
    private void RenderFileTransfer()
    {
        if (!_fileTransferOpen) return;

        var peers = FileTransferService.DiscoveredPeers;
        var offer = FileTransferService.PendingOffer;
        if (_ftIndex >= peers.Count) _ftIndex = 0;

        var status = FileTransferService.Status;
        if (string.IsNullOrWhiteSpace(status))
            status = peers.Count > 0 ? $"发现 {peers.Count} 台设备" : "正在搜索局域网设备…";
        FileTransferStatusText.Text = status;

        FileTransferOfferBar.Visibility = offer is null ? Visibility.Collapsed : Visibility.Visible;
        // 有接收请求时提示条只保留"接受/拒绝"，触屏点放大的图标即可响应
        var hintSpec = offer is null
            ? "DPAD:选择|A:连接|X:发送|Y:清空|B:关闭"
            : "A:接受|B:拒绝";
        if (FileTransferHintBar.Hints != hintSpec) FileTransferHintBar.Hints = hintSpec;
        if (offer is not null)
            FileTransferOfferText.Text = $"{offer.PeerName} 想发送 {offer.Count} 个文件"
                + $"（{FileTransferService.SizeText(offer.TotalBytes)}）· A 接受 · B 拒绝";

        // 设备行
        FileTransferPeerList.Children.Clear();
        if (peers.Count == 0)
        {
            FileTransferPeerList.Children.Add(new TextBlock
            {
                Text = "未发现设备 · 请确认对方也打开了「文件传输」且在同一局域网",
                FontSize = 12, Opacity = 0.45, Margin = new Thickness(4, 2, 0, 2)
            });
        }
        else
        {
            for (var i = 0; i < peers.Count; i++)
            {
                var index = i;
                var row = MakeFileTransferRow($"📶 {peers[i].Name}", peers[i].Address,
                    selected: false, highlight: index == _ftIndex);
                row.Tapped += (_, _) =>
                {
                    _ftIndex = index;
                    _ = FileTransferService.ConnectAsync(peers[index]);
                    RenderFileTransfer();
                };
                FileTransferPeerList.Children.Add(row);
            }
        }

        // 待发送文件行（全部来自拖入，无需勾选；X 直接发送）
        FileTransferFileList.Children.Clear();
        FileTransferFilesHeader.Text = _ftDropped.Count > 0
            ? $"待发送文件（{_ftDropped.Count} 个 · X 发送）"
            : "待发送文件（把文件拖进窗口）";
        FileTransferDropText.Text = _ftDropped.Count == 0 ? "将文件拖到这里" : "继续拖入文件";
        foreach (var path in _ftDropped)
        {
            string name;
            string size;
            try
            {
                var fi = new FileInfo(path);
                name = fi.Name;
                size = FileTransferService.SizeText(fi.Length);
            }
            catch
            {
                name = Path.GetFileName(path);
                size = "—";
            }
            FileTransferFileList.Children.Add(MakeFileTransferRow(name, size, selected: false, highlight: false));
        }

        // 进度
        var busy = FileTransferService.Busy;
        FileTransferProgressBox.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            var p = FileTransferService.Progress;
            FileTransferProgressBar.Value = Math.Clamp(p, 0, 1);
            FileTransferProgressText.Text = p > 0 ? $"传输中 {p * 100:0}%" : "等待确认…";
        }
    }

    private static Border MakeFileTransferRow(string title, string subtitle, bool selected, bool highlight)
    {
        var accent = Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF);
        var border = new Border
        {
            Height = 46,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 0, 10, 0),
            Background = new SolidColorBrush(highlight
                ? Windows.UI.Color.FromArgb(0x26, 0x4C, 0xC2, 0xFF)
                : Windows.UI.Color.FromArgb((byte)(selected ? 0x20 : 0x10), 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(highlight ? accent : Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(highlight ? 2 : 1)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, MaxWidth = 380, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = new TextBlock { Text = subtitle, FontSize = 12, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sub, 1);
        grid.Children.Add(text);
        grid.Children.Add(sub);
        border.Child = grid;
        return border;
    }

    /// <summary>把拖入的文件加入待发送列表（去重；文件夹与不存在的路径忽略）</summary>
    private void AddDroppedFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            if (_ftDropped.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) continue;
            _ftDropped.Add(path);
            added++;
        }
        if (added > 0)
        {
            ShowToast($"已加入 {added} 个文件，按 X 发送", ToastKind.Success);
            RenderFileTransfer();
        }
    }

    /// <summary>浮层内手柄按键：DPad 选设备，A 连接，X 发送拖入的文件，Y 清空，B 关闭（有接收请求时 A 接受、B 拒绝）</summary>
    private bool HandleFileTransfer(GamepadButtons b)
    {
        if (FileTransferService.PendingOffer is not null)
        {
            if (b.HasFlag(GamepadButtons.A)) FileTransferService.AcceptOffer();
            else if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View)) FileTransferService.RejectOffer();
            return true; // 等待确认期间吞掉其它按键，避免误发
        }

        var peers = FileTransferService.DiscoveredPeers;

        if (b.HasFlag(GamepadButtons.DPadUp) || b.HasFlag(GamepadButtons.DPadLeft))
        {
            if (peers.Count > 0) _ftIndex = (_ftIndex + peers.Count - 1) % Math.Max(peers.Count, 1);
            RenderFileTransfer();
            return true;
        }
        if (b.HasFlag(GamepadButtons.DPadDown) || b.HasFlag(GamepadButtons.DPadRight))
        {
            if (peers.Count > 0) _ftIndex = (_ftIndex + 1) % peers.Count;
            RenderFileTransfer();
            return true;
        }
        if (b.HasFlag(GamepadButtons.A))
        {
            if (_ftIndex < peers.Count) _ = FileTransferService.ConnectAsync(peers[_ftIndex]);
            RenderFileTransfer();
            return true;
        }
        if (b.HasFlag(GamepadButtons.X))
        {
            if (_ftDropped.Count == 0) ShowToast("先把文件拖进这个窗口", ToastKind.Warning);
            else _ = FileTransferService.SendAsync(_ftDropped.ToList());
            return true;
        }
        if (b.HasFlag(GamepadButtons.Y))
        {
            if (_ftDropped.Count > 0) { _ftDropped.Clear(); RenderFileTransfer(); }
            return true;
        }
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View)) { CloseFileTransfer(); return true; }
        return true; // 吞掉其余按键，避免误触发主页操作
    }

    /// <summary>电源菜单内手柄按键：↑/← 上一项，↓/→ 下一项，A 执行，B/View/X 取消</summary>
    private bool HandlePower(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.DPadUp) || b.HasFlag(GamepadButtons.DPadLeft))
        { _powerIndex = (_powerIndex + _powerCards.Length - 1) % _powerCards.Length; UpdatePowerCursor(); return true; }
        if (b.HasFlag(GamepadButtons.DPadDown) || b.HasFlag(GamepadButtons.DPadRight))
        { _powerIndex = (_powerIndex + 1) % _powerCards.Length; UpdatePowerCursor(); return true; }
        if (b.HasFlag(GamepadButtons.A)) { ChoosePower(_powerIndex); return true; }
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View) || b.HasFlag(GamepadButtons.X))
        { ClosePowerMenu(); return true; }
        return true; // 遮罩打开时吞掉其余按键，避免误触发主页操作
    }

    private void UpdatePowerCursor()
    {
        var accentBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
        var idleBr = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        var accentBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0x4C, 0xC2, 0xFF));
        var idleBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        for (var i = 0; i < _powerCards.Length; i++)
        {
            var on = i == _powerIndex;
            _powerCards[i].BorderBrush = on ? accentBr : idleBr;
            _powerCards[i].BorderThickness = new Thickness(on ? 2 : 1.5);
            _powerCards[i].Background = on ? accentBg : idleBg;
        }
    }

    /// <summary>选中某项：先关遮罩（让画面刷新），再执行电源操作；失败时用提示条说明原因</summary>
    private void ChoosePower(int index)
    {
        if (!_powerOpen || _powerBusy) return;
        if (index < 0 || index >= _powerCards.Length) return;
        _powerBusy = true;
        ClosePowerMenu();
        _ = RunPowerActionAsync((PowerAction)index);
    }

    private async Task RunPowerActionAsync(PowerAction action)
    {
        // 关机/重启后窗口不会再有刷新机会，先等遮罩收起这一帧
        await Task.Delay(280);
        var (ok, message) = await Task.Run(() => PowerService.Execute(action));
        _powerBusy = false;
        if (!ok) ShowToast(message, ToastKind.Warning);
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

    /// <summary>壁纸轮播/下载状态变化（在 UI 线程触发）：刷新壁纸磁贴</summary>
    private void OnWallpaperChanged() => QueuePageRefresh(RefreshWallpaperTiles);

    /// <summary>刷新所有壁纸小组件磁贴的当前壁纸（换图时只换 Source，不重建磁贴）</summary>
    private void RefreshWallpaperTiles()
    {
        foreach (var (refs, index) in _wallpaperRefs)
        {
            if (index < 0 || index >= _display.Count) continue;
            var item = _display[index].item;
            var img = WallpaperService.CurrentImage(item);
            refs.InfoText.Text = WallpaperService.InfoOf(item);
            if (img != null && !ReferenceEquals(refs.Img.Source, img))
            {
                refs.Img.Source = img;
                refs.Img.Visibility = Visibility.Visible;
                refs.Placeholder.Visibility = Visibility.Collapsed;
                FadeIn(refs.Img); // 轮播动画：淡入
            }
            else if (img == null)
            {
                refs.Img.Visibility = Visibility.Collapsed;
                refs.Placeholder.Visibility = Visibility.Visible;
                refs.Placeholder.Text = WallpaperService.Placeholder(item);
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
    private void OnForegroundChanged() => QueuePageRefresh(RefreshForegroundTiles);

    /// <summary>刷新所有系统小组件磁贴的读数（右摇杆切换面板项、音量/亮度/射频变化后调用）</summary>
    private void RefreshSystemTiles()
    {
        foreach (var (refs, item) in _sysRefs) TileStyle.FillSystemTile(refs, item);
    }

    /// <summary>刷新所有性能控制磁贴的查看项与读数（切换查看项、功耗/温度采样、电源状态刷新后调用）</summary>
    private void RefreshPerformanceTiles()
    {
        foreach (var (refs, item) in _perfRefs)
        {
            refs.PendingPower = PerfPowerPending(item);
            refs.PendingFrame = PerfFramePending(item);
            refs.PendingPowerLimit = PerfPowerLimitPending(item);
            refs.PendingCpuMhz = PerfCpuMhzPending(item);
            refs.PendingBoost = PerfBoostTilePending(item);
            TileStyle.FillPerformanceTile(refs, item);
        }
    }

    /// <summary>后台读一次当前电源方案/锁帧状态并写进缓存，完成后回 UI 线程刷新磁贴</summary>
    private void RefreshPerformanceCacheAsync()
    {
        if (_perfRefreshBusy) { _perfRefreshAgain = true; return; } // 本轮结束后自动补一次（首次布局刚建好磁贴时会遇到）
        _perfRefreshBusy = true;
        var needFrame = _perfRefs.Values.Any(i => i.PerformanceFrameLimit);
        var needPowerLimit = _perfRefs.Values.Any(i => i.PerformancePowerLimit);
        var needCpu = _perfRefs.Values.Any(i => i.PerformanceCpuFrequency || i.PerformanceCpuBoost);
        _ = Task.Run(async () =>
        {
            // 每项独立兜底：某一项刷新失败不能阻断后面的缓存（尤其是 CPU 睿频/频率）
            try { await PerformanceService.RefreshCacheAsync(); } catch { }
            if (needFrame) { try { await FrameLimitService.RefreshCacheAsync(); } catch { } }
            if (needPowerLimit) { try { await CpuPowerService.RefreshCacheAsync(); } catch { } }
            if (needCpu) { try { await PerformanceService.RefreshCpuCacheAsync(); } catch { } }
        }).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _perfRefreshBusy = false;
                if (_pageWorkActive) RefreshPerformanceTiles();
                if (_perfRefreshAgain)
                {
                    _perfRefreshAgain = false;
                    RefreshPerformanceCacheAsync();
                }
            });
        });
    }

    /// <summary>刷新所有性能监控磁贴的开关状态与读数摘要</summary>
    private void RefreshMonitorTiles()
    {
        foreach (var (refs, item) in _monRefs) TileStyle.FillMonitorState(refs, item);
    }

    // ---- 下载模式 ----

    private bool _dlOpen;              // 遮罩已打开（设置页或运行页）
    private bool _dlRunning;           // 遮罩当前显示"运行中"
    private bool _dlBusy;              // 正在进入/退出（熄屏、静音在后台线程）
    private int _dlFocus;              // 当前焦点行
    private int _dlMinutes = 60;       // 设置页的下载时长（分钟）
    private PowerAction _dlAfter = PowerAction.Sleep; // 完成后动作
    private double _dlSpeed;           // 最近一次采样到的网速（字节/秒）
    private DispatcherTimer? _dlPeekBack; // "查看下载进度"3 秒后自动回到遮罩

    private static readonly SolidColorBrush DownloadAccentBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB3, 0x66));
    private static readonly SolidColorBrush DownloadIdleBrush =
        new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    /// <summary>下载模式磁贴 A/点击：打开进入前设置（此时不熄屏）</summary>
    private void OpenDownloadSetup()
    {
        if (_dlOpen || DownloadModeService.IsActive) return;
        _dlOpen = true;
        _dlRunning = false;
        _dlBusy = false;
        _dlFocus = 0;
        DownloadOverlay.Visibility = Visibility.Visible;
        DownloadSetupPanel.Visibility = Visibility.Visible;
        DownloadRunningPanel.Visibility = Visibility.Collapsed;
        UpdateDownloadSetupVisuals();
    }

    /// <summary>关闭设置页（未进入下载模式，屏幕保持正常）</summary>
    private void CloseDownloadSetup()
    {
        if (!_dlOpen || _dlRunning) return;
        _dlOpen = false;
        DownloadOverlay.Visibility = Visibility.Collapsed;
        DownloadSetupPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>设置页的行高亮与数值文字</summary>
    private void UpdateDownloadSetupVisuals()
    {
        Border[] rows = [DlDurationRow, DlActionRow, DlStartRow, DlCancelRow];
        for (var i = 0; i < rows.Length; i++)
            rows[i].BorderBrush = i == _dlFocus ? DownloadAccentBrush : DownloadIdleBrush;
        DlDurationText.Text = _dlMinutes + " 分钟";
        DlActionText.Text = DownloadActionLabel(_dlAfter);
    }

    private void UpdateDownloadRunningVisuals()
    {
        DlPeekRow.BorderBrush = _dlFocus == 0 ? DownloadAccentBrush : DownloadIdleBrush;
        DlCloseRow.BorderBrush = _dlFocus == 1 ? DownloadAccentBrush : DownloadIdleBrush;
    }

    private static string DownloadActionLabel(PowerAction action) => action switch
    {
        PowerAction.Sleep => "睡眠",
        PowerAction.Hibernate => "休眠",
        PowerAction.Shutdown => "关机",
        _ => "不操作"
    };

    /// <summary>开始：记录亮度/音量并熄屏静音，窗口全屏置顶，切到运行页</summary>
    private async Task StartDownloadModeAsync()
    {
        if (_dlBusy) return;
        _dlBusy = true;
        try
        {
            App.Main?.BeginDownloadOverlay();
            var minutes = _dlMinutes;
            var after = _dlAfter;
            await Task.Run(() => DownloadModeService.Enter(minutes, after));

            _dlRunning = true;
            _dlFocus = 0;
            DownloadSetupPanel.Visibility = Visibility.Collapsed;
            DownloadRunningPanel.Visibility = Visibility.Visible;
            UpdateDownloadRunningVisuals();
            _dlSpeed = 0;
            UpdateDownloadRunningTexts();
            RefreshDownloadTiles();
        }
        finally
        {
            _dlBusy = false;
        }
    }

    /// <summary>运行页读数：当前时间、剩余时间、网速、完成后动作</summary>
    private void UpdateDownloadRunningTexts()
    {
        if (!_dlRunning) return;
        if (!DownloadModeService.IsActive)
        {
            // 已在别处退出（例如到点执行了电源操作）
            _dlRunning = false;
            return;
        }

        var now = DateTime.Now;
        DlClockText.Text = now.ToString("HH:mm");
        DlClockDate.Text = now.ToString("M月d日 dddd");

        var remain = DownloadModeService.Remaining;
        if (remain <= TimeSpan.Zero)
        {
            _ = FinishDownloadModeAsync();
            return;
        }
        DlRemainText.Text = remain.TotalHours >= 1
            ? $"{(int)remain.TotalHours:00}:{remain.Minutes:00}:{remain.Seconds:00}"
            : $"{remain.Minutes:00}:{remain.Seconds:00}";

        _dlSpeed = DownloadModeService.SampleBytesPerSecond();
        DlSpeedText.Text = DownloadModeService.FormatSpeed(_dlSpeed);
        DlAfterText.Text = DownloadActionLabel(DownloadModeService.AfterAction);
    }

    /// <summary>倒计时结束：恢复屏幕与声音、关掉遮罩，再执行用户选择的睡眠/休眠/关机</summary>
    private async Task FinishDownloadModeAsync()
    {
        if (_dlBusy) return;
        var after = DownloadModeService.AfterAction;
        await ExitDownloadModeAsync(showToast: false);
        if (after is PowerAction.Sleep or PowerAction.Hibernate or PowerAction.Shutdown)
            _ = Task.Run(() => PowerService.Execute(after));
    }

    /// <summary>退出下载模式：恢复亮度与声音、还原窗口状态、关闭遮罩</summary>
    private async Task ExitDownloadModeAsync(bool showToast = true)
    {
        if (_dlBusy) return;
        _dlBusy = true;
        try
        {
            _dlPeekBack?.Stop();
            _dlPeekBack = null;
            await Task.Run(DownloadModeService.Exit);
            App.Main?.ShowWindowAfterPeek();  // 若正处在"查看进度"的最小化状态，先还原窗口
            App.Main?.EndDownloadOverlay();
            _dlOpen = false;
            _dlRunning = false;
            DownloadOverlay.Visibility = Visibility.Collapsed;
            DownloadSetupPanel.Visibility = Visibility.Collapsed;
            DownloadRunningPanel.Visibility = Visibility.Collapsed;
            RefreshDownloadTiles();
            if (showToast) ShowToast("已关闭下载模式", ToastKind.Success);
        }
        finally
        {
            _dlBusy = false;
        }
    }

    /// <summary>查看下载进度：先还原屏幕、最小化本窗口并切回下载页，5 秒后自动回来重新熄屏</summary>
    private void PeekDownloadPage()
    {
        if (!DownloadModeService.IsActive || _dlPeekBack is not null) return;
        App.Main?.HideWindowForPeek();
        _ = Task.Run(DownloadModeService.PeekBegin);

        _dlPeekBack = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _dlPeekBack.Tick += (_, _) =>
        {
            _dlPeekBack?.Stop();
            _dlPeekBack = null;
            _ = Task.Run(() =>
            {
                DownloadModeService.PeekEnd();
                DispatcherQueue.TryEnqueue(() => App.Main?.ShowWindowAfterPeek());
            });
        };
        _dlPeekBack.Start();
    }

    /// <summary>下载模式遮罩的手柄输入：设置页 UD 选行 / LB·RB 调值 / A 确认；运行页 LR 选按钮 / A 执行</summary>
    private bool HandleDownloadMode(GamepadButtons b)
    {
        if (!_dlOpen || _dlBusy) return true;

        if (!_dlRunning)
        {
            if (b.HasFlag(GamepadButtons.DPadUp))
            {
                _dlFocus = (_dlFocus + 3) % 4;
                UpdateDownloadSetupVisuals();
                PlayDownloadCue(UiSoundKind.Move);
            }
            else if (b.HasFlag(GamepadButtons.DPadDown))
            {
                _dlFocus = (_dlFocus + 1) % 4;
                UpdateDownloadSetupVisuals();
                PlayDownloadCue(UiSoundKind.Move);
            }
            else if (b.HasFlag(GamepadButtons.LeftShoulder) || b.HasFlag(GamepadButtons.RightShoulder))
            {
                var delta = b.HasFlag(GamepadButtons.RightShoulder) ? 1 : -1;
                if (_dlFocus == 0)
                {
                    _dlMinutes = Math.Clamp(_dlMinutes + delta * 5, 5, 24 * 60);
                    UpdateDownloadSetupVisuals();
                    PlayDownloadCue(UiSoundKind.Select);
                }
                else if (_dlFocus == 1)
                {
                    var actions = new[] { PowerAction.Sleep, PowerAction.Hibernate, PowerAction.Shutdown };
                    var at = Array.IndexOf(actions, _dlAfter);
                    _dlAfter = actions[(at + delta + actions.Length) % actions.Length];
                    UpdateDownloadSetupVisuals();
                    PlayDownloadCue(UiSoundKind.Select);
                }
            }
            else if (b.HasFlag(GamepadButtons.A))
            {
                if (_dlFocus == 2)
                {
                    PlayDownloadCue(UiSoundKind.Confirm);
                    _ = StartDownloadModeAsync();
                }
                else if (_dlFocus == 3)
                {
                    PlayDownloadCue(UiSoundKind.Back);
                    CloseDownloadSetup();
                }
            }
            else if (b.HasFlag(GamepadButtons.B))
            {
                PlayDownloadCue(UiSoundKind.Back);
                CloseDownloadSetup();
            }
            return true;
        }

        // 运行页：只看两个按钮
        if (b.HasFlag(GamepadButtons.DPadLeft) || b.HasFlag(GamepadButtons.DPadRight))
        {
            _dlFocus = 1 - _dlFocus;
            UpdateDownloadRunningVisuals();
            PlayDownloadCue(UiSoundKind.Move);
        }
        else if (b.HasFlag(GamepadButtons.A))
        {
            if (_dlFocus == 0)
            {
                PlayDownloadCue(UiSoundKind.Confirm);
                PeekDownloadPage();
            }
            else
            {
                PlayDownloadCue(UiSoundKind.Back);
                _ = ExitDownloadModeAsync();
            }
        }
        else if (b.HasFlag(GamepadButtons.B))
        {
            PlayDownloadCue(UiSoundKind.Back);
            _ = ExitDownloadModeAsync();
        }
        return true;
    }

    /// <summary>下载模式沿用主页音效：移动/切换/确认/返回各有不同音色，受"界面音效"开关控制</summary>
    private static void PlayDownloadCue(UiSoundKind kind)
    {
        if (SettingsService.Current.UiSoundEnabled) UiSound.Play(kind);
    }

    /// <summary>刷新下载模式磁贴（剩余时间秒级变化）</summary>
    private void RefreshDownloadTiles()
    {
        foreach (var (refs, item) in _dlRefs) TileStyle.FillDownloadState(refs, item);
    }

    // 下载模式遮罩的鼠标/触摸入口（手柄走 HandleDownloadMode）
    private void OnDownloadStartTapped(object sender, TappedRoutedEventArgs e)
    {
        PlayDownloadCue(UiSoundKind.Confirm);
        _ = StartDownloadModeAsync();
    }

    private void OnDownloadCancelTapped(object sender, TappedRoutedEventArgs e)
    {
        PlayDownloadCue(UiSoundKind.Back);
        CloseDownloadSetup();
    }

    private void OnDownloadPeekTapped(object sender, TappedRoutedEventArgs e)
    {
        PlayDownloadCue(UiSoundKind.Confirm);
        PeekDownloadPage();
    }

    private void OnDownloadCloseTapped(object sender, TappedRoutedEventArgs e)
    {
        PlayDownloadCue(UiSoundKind.Back);
        _ = ExitDownloadModeAsync();
    }

    /// <summary>刷新小黄鸭磁贴的安装/运行状态（每秒轮询或操作后调用）</summary>
    private void RefreshLosslessTiles()
    {
        foreach (var (refs, item) in _lossRefs) TileStyle.FillLosslessState(refs, item);
    }

    /// <summary>刷新小猫咪磁贴的连接状态与当前服务器</summary>
    private void RefreshProxyTiles()
    {
        foreach (var (refs, item) in _proxyRefs) TileStyle.FillProxyTile(refs, item);
    }

    /// <summary>刷新屏幕翻译磁贴的开关状态</summary>
    private void RefreshTranslateTiles()
    {
        foreach (var (refs, item) in _translateRefs) TileStyle.FillScreenTranslateState(refs, item);
    }

    /// <summary>小猫咪 A 键：开关连接，并按结果显示提示</summary>
    private async Task ToggleProxyAsync(LaunchItem item)
    {
        var hint = ProxyService.FirstRunHint();
        if (hint is not null) ShowToast(hint, ToastKind.Success);
        var result = await ProxyService.ToggleAsync(item);
        RefreshProxyTiles();
        if (result.Ok) PersistLayout(); // 记住实际连接的节点
        ShowToast(result.Message, result.Ok
            ? (result.On ? ToastKind.Success : ToastKind.Warning)
            : ToastKind.Warning, sound: result.Ok ? UiSoundKind.Select : UiSoundKind.Error);
    }

    /// <summary>小猫咪 Y 键：更新订阅并选最快节点，按结果显示提示</summary>
    private async Task RefreshFastestProxyAsync(LaunchItem item)
    {
        var hint = ProxyService.FirstRunHint();
        ShowToast(hint ?? "正在更新订阅并测速…", ToastKind.Success);
        var result = await ProxyService.RefreshFastestAsync(item);
        RefreshProxyTiles();
        if (result.Ok) PersistLayout(); // 记住最快的节点，下次启动继续用它
        ShowToast(result.Message, result.Ok ? ToastKind.Success : ToastKind.Warning);
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
    private void AnimateSystemSwitch(int delta) =>
        AnimateTileSwitch(_sysRefs.Select(kv =>
            (kv.Key.SlideRow, (Action)(() => TileStyle.FillSystemTile(kv.Key, kv.Value)))), delta);

    /// <summary>磁贴切换动画：旧内容向切换方向反向滑出淡出，换内容后从另一侧滑入淡入</summary>
    private static void AnimateTileSwitch(IEnumerable<(Grid Slide, Action Refresh)> tiles, int delta)
    {
        var dir = delta >= 0 ? 1 : -1;
        foreach (var (slide, refresh) in tiles)
        {
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
                refresh(); // 换成新选项的内容
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

    /// <summary>当前焦点所在的性能控制磁贴项（磁贴区选中 或 抽屉内层焦点）；不是性能控制返回 null</summary>
    private LaunchItem? PerformanceItemInFocus()
    {
        if (_display.Count == 0) return null;
        if (_inDrawer)
        {
            if (FocusedDrawerItem() is not { Kind: LaunchKind.Widget, WidgetKind: WidgetKind.Performance } inner) return null;
            return inner;
        }
        if (_selected < 0 || _selected >= _display.Count) return null;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Performance ? item : null;
    }

    /// <summary>电源模式待应用档位（按磁贴项记录；-1=未选择，LB/RB 选择后写入）</summary>
    private readonly Dictionary<LaunchItem, int> _perfPowerPending = new();

    /// <summary>该磁贴的待应用电源档位；未选择时取当前生效档位（不可用返回 -1）</summary>
    private int PerfPowerPending(LaunchItem item)
    {
        if (_perfPowerPending.TryGetValue(item, out var pending) && pending >= 0) return pending;
        var state = PerformanceService.Cached;
        return state.Available ? (int)state.Level : -1;
    }

    /// <summary>全局锁帧待应用值（按磁贴项记录，FPS；-1=未选择，LB/RB 调整后写入）</summary>
    private readonly Dictionary<LaunchItem, int> _perfFramePending = new();

    /// <summary>持续功耗待应用值（按磁贴项记录，W；-1=未选择，LB/RB 按 1W 调整后写入）</summary>
    private readonly Dictionary<LaunchItem, int> _perfPowerLimitPending = new();

    /// <summary>该磁贴的待应用持续功耗；未选择返回 -1（磁贴显示当前生效值）</summary>
    private int PerfPowerLimitPending(LaunchItem item)
        => _perfPowerLimitPending.TryGetValue(item, out var pending) && pending >= 0 ? pending : -1;

    /// <summary>CPU 最高频率待应用值（MHz；-1=未选择，0=不限制）</summary>
    private readonly Dictionary<LaunchItem, int> _perfCpuMhzPending = new();

    /// <summary>该磁贴的待应用 CPU 频率；未选择返回 -1（磁贴显示当前生效值）</summary>
    private int PerfCpuMhzPending(LaunchItem item)
        => _perfCpuMhzPending.TryGetValue(item, out var pending) && pending >= 0 ? pending : -1;

    /// <summary>CPU 睿频待应用状态（-1=未选择，0=关闭，2=开启）</summary>
    private readonly Dictionary<LaunchItem, int> _perfBoostTilePending = new();

    /// <summary>该磁贴的待应用睿频状态；未选择返回 -1（磁贴显示当前生效值）</summary>
    private int PerfBoostTilePending(LaunchItem item)
        => _perfBoostTilePending.TryGetValue(item, out var pending) && pending >= 0 ? pending : -1;

    /// <summary>该磁贴的待应用锁帧值；未选择返回 -1（磁贴显示当前生效值）</summary>
    private int PerfFramePending(LaunchItem item)
        => _perfFramePending.TryGetValue(item, out var pending) && pending >= 0 ? pending : -1;

    /// <summary>LB/RB：按磁贴当前显示的快捷项调节（电源模式循环选档 / 全局锁帧按 5 帧步进）。返回是否消费按键。</summary>
    private bool TryAdjustPerformancePower(int dir)
    {
        if (PerformanceItemInFocus() is not { } item) return false;
        var enabled = TileStyle.EnabledQuickItems(item);
        if (enabled.Length == 0) return false;
        var quick = TileStyle.CurrentQuickItem(item);
        if (quick == TileStyle.PerformanceQuickItem.FrameLimit)
            return AdjustPerformanceFrame(item, dir);
        if (quick == TileStyle.PerformanceQuickItem.PowerLimit)
            return AdjustPerformancePowerLimit(item, dir);
        if (quick == TileStyle.PerformanceQuickItem.CpuFrequency)
            return AdjustPerformanceCpuFrequency(item, dir);
        if (quick == TileStyle.PerformanceQuickItem.CpuBoost)
            return true; // 睿频是开关型：不走 LB/RB 调整，直接按 A 开关并保存
        if (!item.PerformancePowerMode) return false;

        var state = PerformanceService.GetState();
        if (!state.Available) { ShowToast("当前系统不支持电源模式", ToastKind.Warning); return true; }
        var start = PerfPowerPending(item);
        if (start < 0) start = (int)state.Level;
        _perfPowerPending[item] = ((start + dir) % 3 + 3) % 3;
        AnimatePerformanceSwitch(dir);
        return true;
    }

    /// <summary>全局锁帧按 5 帧步进调整（限制在后端支持范围内）；结果只记为待应用，A 才写入</summary>
    private bool AdjustPerformanceFrame(LaunchItem item, int dir)
    {
        const int step = 5;
        var state = FrameLimitService.Cached;
        if (!state.Supported) { ShowToast("没有可用的限帧后端", ToastKind.Warning); return true; }

        var max = state.Maximum - (state.Maximum - state.Minimum) % step;
        var slots = Math.Max(0, (max - state.Minimum) / step);
        var start = _perfFramePending.TryGetValue(item, out var pending) && pending >= 0 ? pending : state.Fps;
        if (start <= 0) start = state.Minimum;
        var slot = (int)Math.Round((start - state.Minimum) / (double)step) + dir;
        _perfFramePending[item] = state.Minimum + Math.Clamp(slot, 0, slots) * step;
        RefreshPerformanceTile(item); // 只改数值，不做滑动动画
        return true;
    }

    /// <summary>功耗控制按 1W 调整持续功耗；短时/瞬时功耗在应用时按比例自动跟随</summary>
    private bool AdjustPerformancePowerLimit(LaunchItem item, int dir)
    {
        var state = CpuPowerService.Cached;
        if (!state.Enabled) { ShowToast("按 A 启用功耗控制（需要管理员授权）", ToastKind.Warning); return true; }
        if (!state.Supported || state.Limits.Length == 0 || !state.Limits[0].Writable)
        { ShowToast("当前 CPU 功耗接口不可写", ToastKind.Warning); return true; }

        var limit = state.Limits[0];
        var start = _perfPowerLimitPending.TryGetValue(item, out var pending) && pending >= 0
            ? pending : (int)Math.Round(limit.Watts);
        var min = (int)Math.Round(limit.Minimum);
        var max = (int)Math.Round(limit.Maximum);
        _perfPowerLimitPending[item] = Math.Clamp(start + dir, min, max);
        RefreshPerformanceTile(item); // 只改数值，不做滑动动画
        return true;
    }

    /// <summary>CPU 频率按 100MHz 调整最高频率（0=不限制）；右/上为加，左/下为减</summary>
    private bool AdjustPerformanceCpuFrequency(LaunchItem item, int dir)
    {
        var cpu = PerformanceService.CachedCpu;
        // 缓存尚未读到（首次进入/读取失败）时也允许先调整，同时后台补读真实值
        if (!cpu.FrequencySupported) _ = Task.Run(PerformanceService.RefreshCpuCacheAsync);
        var start = _perfCpuMhzPending.TryGetValue(item, out var pending) && pending >= 0
            ? pending : cpu.MaxFrequencyMhz;
        var next = dir > 0
            ? (start < 1000 ? 1000 : Math.Min(start + 100, 5000))
            : (start <= 0 ? 5000 : start <= 1000 ? 0 : start - 100);
        _perfCpuMhzPending[item] = next;
        RefreshPerformanceTile(item); // 只改数值，不做滑动动画
        return true;
    }

    /// <summary>只刷新指定性能磁贴的内容（不重建、不做切换动画）</summary>
    private void RefreshPerformanceTile(LaunchItem item)
    {
        foreach (var (refs, it) in _perfRefs)
        {
            if (!ReferenceEquals(it, item)) continue;
            refs.PendingPower = PerfPowerPending(it);
            refs.PendingFrame = PerfFramePending(it);
            refs.PendingPowerLimit = PerfPowerLimitPending(it);
            refs.PendingCpuMhz = PerfCpuMhzPending(it);
            refs.PendingBoost = PerfBoostTilePending(it);
            TileStyle.FillPerformanceTile(refs, it);
        }
    }

    /// <summary>右摇杆←/→：在已开启的快捷项之间切换（电源模式 → 全局锁帧 → 功耗控制 → CPU 频率 → CPU 睿频）。返回是否消费按键。</summary>
    private bool TryCyclePerformanceQuick(int dir)
    {
        if (PerformanceItemInFocus() is not { } item) return false;
        var enabled = TileStyle.EnabledQuickItems(item);
        if (enabled.Length < 2) return false;

        var index = Array.IndexOf(enabled, TileStyle.CurrentQuickItem(item));
        item.PerformanceOption = (int)enabled[((index + dir) % enabled.Length + enabled.Length) % enabled.Length];
        ConfigService.Save(_items.ToList());
        AnimatePerformanceSwitch(dir);
        UpdateHintForSelection();
        return true;
    }

    /// <summary>上一次光标所在的性能控制磁贴（光标移开时用来还原待应用档位）</summary>
    private LaunchItem? _perfFocusPrev;

    /// <summary>光标移开性能控制磁贴后，丢弃待应用档位并恢复为当前已生效的图标和选项</summary>
    private void SyncPerformanceFocus()
    {
        var current = PerformanceItemInFocus();
        var prev = _perfFocusPrev;
        _perfFocusPrev = current;
        if (prev is null || ReferenceEquals(prev, current)) return;
        var hadPower = _perfPowerPending.Remove(prev);
        var hadFrame = _perfFramePending.Remove(prev);
        var hadLimit = _perfPowerLimitPending.Remove(prev);
        var hadCpuMhz = _perfCpuMhzPending.Remove(prev);
        var hadBoost = _perfBoostTilePending.Remove(prev);
        if (!hadPower && !hadFrame && !hadLimit && !hadCpuMhz && !hadBoost) return;
        // 回到当前实际生效值（待应用置空后由 FillPerformanceTile 读服务缓存）
        foreach (var (refs, item) in _perfRefs)
        {
            if (!ReferenceEquals(item, prev)) continue;
            refs.PendingPower = -1;
            refs.PendingFrame = -1;
            refs.PendingPowerLimit = -1;
            refs.PendingCpuMhz = -1;
            refs.PendingBoost = -1;
            TileStyle.FillPerformanceTile(refs, item);
        }
    }

    /// <summary>应用 CPU 最高频率（0=不限制）；写入当前电源方案的交流/直流值</summary>
    private async Task ApplyPerformanceCpuFrequencyAsync(LaunchItem item)
    {
        var cpu = PerformanceService.CachedCpu;
        // 缓存未就绪时先补读一次，避免误判“不可用”
        if (!cpu.FrequencySupported) cpu = await PerformanceService.RefreshCpuCacheAsync();
        if (!cpu.FrequencySupported) { ShowToast("当前系统未提供 CPU 频率设置", ToastKind.Warning); return; }
        var mhz = _perfCpuMhzPending.TryGetValue(item, out var pending) && pending >= 0
            ? pending : cpu.MaxFrequencyMhz;
        var (ok, message) = await Task.Run(() => PerformanceService.ApplyCpuSetting(false, mhz));
        ShowToast(message, ok ? ToastKind.Success : ToastKind.Warning);
        if (ok)
        {
            await PerformanceService.RefreshCpuCacheAsync();
            RefreshPerformanceTiles();
        }
    }

    /// <summary>应用 CPU 睿频：A 键直接取反并保存（当前开启→关闭，当前关闭→开启）</summary>
    private async Task ApplyPerformanceCpuBoostAsync(LaunchItem item)
    {
        var cpu = PerformanceService.CachedCpu;
        if (!cpu.BoostSupported) cpu = await PerformanceService.RefreshCpuCacheAsync();
        if (!cpu.BoostSupported) { ShowToast("当前系统未提供睿频设置", ToastKind.Warning); return; }
        var mode = cpu.BoostMode == 0 ? 2 : 0; // 取反：0=关闭，2=开启
        _perfBoostTilePending[item] = mode;
        RefreshPerformanceTile(item);          // 先给出即时反馈，再写入系统
        var (ok, message) = await Task.Run(() => PerformanceService.ApplyCpuSetting(true, mode));
        ShowToast(message, ok ? ToastKind.Success : ToastKind.Warning);
        if (ok)
        {
            await PerformanceService.RefreshCpuCacheAsync();
            RefreshPerformanceTiles();
        }
        else
        {
            _perfBoostTilePending.Remove(item);
            RefreshPerformanceTile(item);
        }
    }

    /// <summary>应用持续功耗；短时（SPPT/PL2）与瞬时（FPPT）按 1.25 / 1.5 倍自动跟随，
    /// 并各自限制在硬件可写范围内（与面板里只调持续功耗的语义一致）</summary>
    private async Task ApplyPerformancePowerLimitAsync(LaunchItem item)
    {
        var state = CpuPowerService.Cached;
        if (!state.Enabled)
        {
            // 首次使用：拉起管理员助手授权（写入 MSR 需要）
            var enable = await Task.Run(CpuPowerService.Enable);
            ShowToast(enable.Message, enable.Ok ? ToastKind.Success : ToastKind.Warning);
            if (!enable.Ok) return;
            await CpuPowerService.RefreshCacheAsync();
            RefreshPerformanceTiles();
            return;
        }
        if (!state.Supported || state.Limits.Length == 0) { ShowToast("当前 CPU 功耗接口不可用", ToastKind.Warning); return; }

        var burst = state.Limits.Length > 1 ? state.Limits[1] : null;
        var peak = state.Limits.Length > 2 ? state.Limits[2] : null;
        var pl1 = _perfPowerLimitPending.TryGetValue(item, out var pending) && pending >= 0
            ? pending : (int)Math.Round(state.Limits[0].Watts);
        int Follow(CpuPowerService.Limit? limit, double ratio)
            => limit is null ? 0 : (int)Math.Clamp(Math.Round(pl1 * ratio), limit.Minimum, limit.Maximum);

        var pl2 = Follow(burst, 1.25);
        var fppt = Follow(peak, 1.5);

        var targets = new List<(int Index, double Watts)> { (0, pl1) };
        if (burst is not null) targets.Add((1, Math.Max(pl2, pl1)));
        if (peak is not null) targets.Add((2, Math.Max(fppt, burst is null ? pl1 : Math.Max(pl2, pl1))));
        var current = state.Limits.Select(l => l.Watts).ToArray();
        var (ok, message) = await ApplyPowerLimitsAsync(targets.ToArray(), current);
        if (ok)
        {
            await CpuPowerService.RefreshCacheAsync();
            RefreshPerformanceTiles();
            ShowToast($"持续功耗 {pl1} W（短时 {pl2} W / 瞬时 {fppt} W）", ToastKind.Success);
            await SaveCpuPowerSettingsAsync(); // 记住目标值，重启后自动写回
        }
        else
        {
            ShowToast(message, ToastKind.Warning);
        }
    }

    /// <summary>A：应用磁贴上选好的快捷项（电源模式写入 Windows / 全局锁帧写入限帧后端 / 功耗控制写入持续功耗 / CPU 频率与睿频写入当前方案）</summary>
    private async void ApplyPerformancePowerMode(LaunchItem item)
    {
        if (TileStyle.CurrentQuickItem(item) == TileStyle.PerformanceQuickItem.CpuBoost && item.PerformanceCpuBoost)
        {
            await ApplyPerformanceCpuBoostAsync(item);
            return;
        }
        if (TileStyle.CurrentQuickItem(item) == TileStyle.PerformanceQuickItem.CpuFrequency && item.PerformanceCpuFrequency)
        {
            await ApplyPerformanceCpuFrequencyAsync(item);
            return;
        }
        if (TileStyle.CurrentQuickItem(item) == TileStyle.PerformanceQuickItem.PowerLimit && item.PerformancePowerLimit)
        {
            await ApplyPerformancePowerLimitAsync(item);
            return;
        }
        if (TileStyle.CurrentQuickItem(item) == TileStyle.PerformanceQuickItem.FrameLimit && item.PerformanceFrameLimit)
        {
            var frameState = FrameLimitService.Cached;
            if (!frameState.Supported) { ShowToast("没有可用的限帧后端", ToastKind.Warning); return; }
            var fps = _perfFramePending.TryGetValue(item, out var pendingFrame) && pendingFrame >= 0
                ? pendingFrame : frameState.Fps;
            var (frameOk, frameMessage) = await Task.Run(() => FrameLimitService.Apply(frameState, fps));
            ShowToast(frameMessage, frameOk ? ToastKind.Success : ToastKind.Warning);
            if (frameOk) await FrameLimitService.RefreshCacheAsync();
            RefreshPerformanceTiles();
            return;
        }

        var state = PerformanceService.GetState();
        if (!state.Available) { ShowToast("当前系统不支持电源模式", ToastKind.Warning); return; }
        var pending = PerfPowerPending(item);
        if (pending < 0) { ShowToast("先用 LB/RB 选择电源模式", ToastKind.Warning); return; }

        var (ok, message) = await Task.Run(() => PerformanceService.ApplyLevel((PerformanceLevel)pending));
        ShowToast(message, ok ? ToastKind.Success : ToastKind.Warning);
        RefreshPerformanceTiles();
    }

    /// <summary>电源模式切换动画：旧档位反向滑出淡出，换内容后从另一侧滑入淡入（与系统小组件同手感）</summary>
    private void AnimatePerformanceSwitch(int delta)
    {
        var dir = delta >= 0 ? 1 : -1;
        foreach (var (refs, item) in _perfRefs.ToList())
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
                refs.PendingPower = PerfPowerPending(item);
                refs.PendingFrame = PerfFramePending(item);
                refs.PendingPowerLimit = PerfPowerLimitPending(item);
                refs.PendingCpuMhz = PerfCpuMhzPending(item);
                refs.PendingBoost = PerfBoostTilePending(item);
                TileStyle.FillPerformanceTile(refs, item);
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
        UpdateWallpaperInfo();   // 壁纸磁贴：选中时显示标题与壁纸信息，光标移开即隐藏
        UpdateHintForSelection();
        SyncPerformanceFocus();  // 性能磁贴：光标移开后恢复当前已生效的电源模式
    }

    /// <summary>壁纸磁贴信息条：只有当前选中的壁纸磁贴显示，其它一律隐藏</summary>
    private void UpdateWallpaperInfo()
    {
        foreach (var (refs, index) in _wallpaperRefs)
        {
            var show = !_inDrawer && index == _selected && index >= 0 && index < _display.Count;
            refs.InfoBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) continue;
            var item = _display[index].item;
            refs.InfoTitle.Text = "壁纸";
            refs.InfoText.Text = WallpaperService.InfoOf(item);
        }
    }

    /// <summary>当前选中的是不是壁纸小组件磁贴</summary>
    private bool IsWallpaperSelected() =>
        _selected >= 0 && _selected < _display.Count
        && _display[_selected].item is { Kind: LaunchKind.Widget, WidgetKind: WidgetKind.Wallpaper };

    /// <summary>Y：收藏当前壁纸（收藏的壁纸在轮播中出现得更频繁，并持久化到配置）</summary>
    private void LikeSelectedWallpaper()
    {
        if (!IsWallpaperSelected()) return;
        LikeWallpaper(_display[_selected].item);
    }

    private void LikeWallpaper(LaunchItem item)
    {
        var file = WallpaperService.CurrentFile(item);
        if (file is null)
        {
            ShowToast("壁纸还没下载好，请稍后再试", ToastKind.Warning);
            return;
        }
        var name = Path.GetFileName(file);
        if (item.WallpaperLiked.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("这张壁纸已经收藏过了", ToastKind.Warning);
            return;
        }
        item.WallpaperBlocked.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        item.WallpaperLiked.Add(name);
        ConfigService.Save(_items.ToList());
        WallpaperService.Register(item);
        UpdateWallpaperInfo();
        ShowToast("已收藏当前壁纸", ToastKind.Success);
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
                    : FocusedDrawerItem() is { Kind: LaunchKind.Widget, WidgetKind: WidgetKind.Performance } perfInner
                        ? PerformanceHintText(perfInner, "DPAD:选择内层|", "B:返回抽屉") // 内层性能控制（按已开启的快捷项生成）
                        : "DPAD:选择内层|A:启动|X:添加|Y:最小化|B:返回抽屉"); // 内层：按键作用于内层磁贴
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
            HintBar.SetHints("DPAD:切换选择|A:备份存档|Y:恢复存档|R3:编辑|L3:排列|B:隐藏");
        else if (IsRecycleBinSelected())
            HintBar.SetHints("DPAD:切换选择|A:打开回收站|Y:清空回收站|R3:编辑|L3:排列|B:隐藏");
        else if (IsForegroundSelected())
            HintBar.SetHints("DPAD:切换选择|A:冻结/解冻|Y:结束进程|R3:编辑|L3:排列|B:隐藏");
        else if (IsSystemSelected())
            HintBar.SetHints("DPAD:切换选择|←→:切换项|A:开关/打开|LB/RB:调整|R3:编辑|L3:排列|B:隐藏");
        else if (IsPerformanceSelected())
            HintBar.SetHints(PerformanceHintText(_display[_selected].item, "DPAD:切换选择|", "B:隐藏"));
        else if (IsGamepadMapSelected())
            HintBar.SetHints("DPAD:切换选择|A:开启/关闭映射|R3:编辑|L3:排列|B:隐藏");
        else if (IsOptiSelected())
            HintBar.SetHints("DPAD:切换选择|A:呼出OptiScaler菜单|Y:注入|R3:编辑|L3:排列|B:隐藏");
        else if (IsMonitorSelected())
            HintBar.SetHints("DPAD:切换选择|A:开启/关闭监控|R3:编辑|L3:排列|B:隐藏");
        else if (IsLosslessSelected())
            HintBar.SetHints("DPAD:切换选择|A:开关插帧|Y:打开小黄鸭|R3:编辑|L3:排列|B:隐藏");
        else if (IsWallpaperSelected())
            HintBar.SetHints("DPAD:切换选择|A:应用壁纸|Y:收藏壁纸|R3:编辑|L3:排列|B:隐藏");
        else if (IsFixedSelected())
            HintBar.SetHints("DPAD:选择|A:打开|L3:排列|VIEW:设置|B:隐藏"); // 设置/电源常驻磁贴不可编辑
        else if (IsClosableSelected())
            HintBar.SetHints("DPAD:选择|A:打开|Y:最小化|X:添加|R3:编辑|L3:排列|VIEW:设置|B:隐藏");
        else
            HintBar.SetHints(DrawerService.FindDrawer(_items.ToList()) is null
                ? "DPAD:选择|A:启动|X:添加|R3:编辑|L3:排列|VIEW:设置|B:隐藏"
                : "DPAD:选择(↓:抽屉)|A:启动|X:添加|R3:编辑|L3:排列|VIEW:设置|B:隐藏");
    }

    /// <summary>选中磁贴是否为设置/电源常驻磁贴（固定小尺寸+固定配色，不可编辑）</summary>
    private bool IsFixedSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && TileStyle.IsFixedTile(item);
    }

    /// <summary>选中磁贴是否可 Y 关闭（程序/网站/文件夹=可；组/小组件=不可）</summary>
    private bool IsClosableSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind is LaunchKind.App or LaunchKind.Url or LaunchKind.Folder;
    }

    /// <summary>方向选择：按磁贴矩形找指定方向最近邻。返回 true=发生了移动（供"最底行 ↓ 进入抽屉"判断）</summary>
    private bool MoveSelection(int dx, int dy)
    {
        if (_tiles.Count == 0) return false;
        var cur = Math.Clamp(_selected, 0, _tiles.Count - 1);
        var best = FindNearestTile(cur, dx, dy);
        if (best < 0) return false;
        _selected = best;
        UpdateSelection();
        return true;
    }

    private static Windows.Foundation.Rect TileRect(Border b) =>
        new(Canvas.GetLeft(b), Canvas.GetTop(b), b.Width, b.Height);

    /// <summary>
    /// 方向最近邻（磁贴尺寸不一也只会走到紧邻的磁贴）：
    /// 用矩形边缘的推进距离 + 侧向重叠量评分，而不是中心点距离——
    /// 大磁贴中心偏得远，中心距离会跳过紧挨着的小磁贴。返回 -1 表示该方向没有候选。
    /// </summary>
    private int FindNearestTile(int from, int dx, int dy, Func<int, bool>? exclude = null)
    {
        if (from < 0 || from >= _tiles.Count) return -1;
        var cr = TileRect(_tiles[from]);
        var curX = cr.X + cr.Width / 2;
        var curY = cr.Y + cr.Height / 2;
        var horizontal = dx != 0;
        var sign = horizontal ? dx : dy;

        var best = -1;
        var bestScore = double.MaxValue;
        var bestPrimary = double.MaxValue;
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (i == from) continue;
            if (exclude is not null && exclude(i)) continue;
            var r = TileRect(_tiles[i]);
            var cx = r.X + r.Width / 2;
            var cy = r.Y + r.Height / 2;

            // 中心必须在移动方向前方
            var centerDelta = horizontal ? (cx - curX) * sign : (cy - curY) * sign;
            if (centerDelta < 1) continue;

            // 边缘推进距离（<=0 = 紧贴或轻微重叠）
            var edge = horizontal
                ? (sign > 0 ? r.X - (cr.X + cr.Width) : cr.X - (r.X + r.Width))
                : (sign > 0 ? r.Y - (cr.Y + cr.Height) : cr.Y - (r.Y + r.Height));

            // 侧向重叠：>0 正对；<=0 错开，按间隙惩罚
            var crossOverlap = horizontal
                ? Math.Min(cr.Y + cr.Height, r.Y + r.Height) - Math.Max(cr.Y, r.Y)
                : Math.Min(cr.X + cr.Width, r.X + r.Width) - Math.Max(cr.X, r.X);
            var crossGap = crossOverlap > 0 ? 0 : -crossOverlap;

            var score = Math.Max(0, edge) + crossGap * 3;
            if (score < bestScore - 0.001 ||
                (Math.Abs(score - bestScore) <= 0.001 && centerDelta < bestPrimary))
            {
                bestScore = score;
                bestPrimary = centerDelta;
                best = i;
            }
        }
        return best;
    }

    private static (double X, double Y) Center(Border b) =>
        (Canvas.GetLeft(b) + b.Width / 2, Canvas.GetTop(b) + b.Height / 2);

    /// <summary>移动模式（已抓取）：把抓取中的磁贴与指定方向最近邻磁贴交换位置</summary>
    private void SwapInDirection(int dx, int dy)
    {
        if (_grabbed is null || _tiles.Count == 0) return;
        var gi = _display.IndexOf((_grabbed, null));
        if (gi < 0) return;

        // 组成员不参与交换
        var best = FindNearestTile(gi, dx, dy,
            i => i >= _display.Count || _display[i].group is not null);
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
        if (item.Kind == LaunchKind.Widget && item.WidgetKind is WidgetKind.Settings or WidgetKind.Power) return; // 设置/电源磁贴不参与组合
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
        if (item.Kind == LaunchKind.Widget && item.WidgetKind is WidgetKind.Settings or WidgetKind.Power) return; // 设置/电源磁贴不可删除
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

    /// <summary>启动其他程序后是否自动收起：开始菜单模式下保持常驻，只能手动隐藏</summary>
    private static bool AutoHideAfterLaunch =>
        SettingsService.Current.HideAfterLaunch && !SettingsService.Current.StartMenuMode;

    private bool _launchInProgress;
    private CancellationTokenSource? _pendingLaunch;
    public void CancelPendingLaunch() => _pendingLaunch?.Cancel();

    private async void LaunchItem(LaunchItem item)
    {
        // 同一时间只允许一次前台交接，防止连按或不同磁贴争抢焦点。
        if (_launchInProgress) return;
        _launchInProgress = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _pendingLaunch = timeout;
        var token = timeout.Token;
        try
        {
            if (App.Main is { } main) await main.HideForLaunchAsync(timeout.Token);
            var work = Task.Run(() => LaunchService.LaunchOrActivateAsync(item, token));
            var result = await work.WaitAsync(timeout.Token);
            if (!result.Success)
            {
                App.Main?.ShowFromTray();
                ShowToast($"{item.Name}：{result.Message}", ToastKind.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            timeout.Cancel();
            App.Main?.ShowFromTray();
            ShowToast("切换已取消或超时；不会重复启动程序", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            timeout.Cancel();
            LaunchService.Diag($"界面启动失败: {ex.GetType().Name} {ex.Message}");
            App.Main?.ShowFromTray();
            ShowToast($"打开失败：{ex.Message}", ToastKind.Warning);
        }
        finally { _pendingLaunch = null; _launchInProgress = false; }
    }

    /// <summary>Y=收起：把选中磁贴对应的窗口最小化（不结束进程）；未运行则提示（小组件/组磁贴不响应；修改器小组件=关闭修改器）</summary>
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

    /// <summary>收起磁贴对应的窗口（Y 键与长按共用）：程序/网站/文件夹一律最小化；修改器小组件=关闭修改器</summary>
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

        var minimized = LaunchService.Minimize(item);
        ShowToast(minimized > 0 ? $"已最小化 {item.Name}" : $"{item.Name} 未在运行",
            minimized > 0 ? ToastKind.Success : ToastKind.Warning);
        if (minimized > 0) App.Main?.KeepForeground(); // 目标最小化后启动器保持前台不最小化
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

    private static bool IsToggleItem(LaunchItem? item)
    {
        if (item is not { Kind: LaunchKind.Widget }) return false;
        if (item.WidgetKind == WidgetKind.System)
            return !SystemPanelService.IsLauncher(SystemPanelService.FromOption(
                SystemPanelService.NormalizeOption(item.SystemOptionMask, item.SystemOption)));
        return item.WidgetKind is WidgetKind.GamepadMap or WidgetKind.Foreground or WidgetKind.Monitor
            or WidgetKind.LosslessScaling or WidgetKind.Keyboard or WidgetKind.Clash or WidgetKind.ScreenTranslate;
    }

    public bool IsToggleSelected => !_moving && !_choiceOpen && !_renaming && !_powerOpen && !_perfOpen && !_dlOpen &&
        !_wallpaperOpen && !_fileTransferOpen && !_searchOpen &&
        IsToggleItem(_inDrawer ? FocusedDrawerItem() :
            (_selected >= 0 && _selected < _display.Count ? _display[_selected].item : null));

    /// <summary>性能面板是否展开：面板内的操作音效与主页磁贴开关保持一致（用 success 音而非 A 键确认音）</summary>
    public bool IsPerformancePanelOpen => _perfOpen;

    public bool IsToggleTileSource(object? source)
    {
        if (_moving) return false;
        for (var el = source as DependencyObject; el is not null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is not Border border) continue;
            if (_drawerInnerTiles.Contains(border)) return IsToggleItem(border.Tag as LaunchItem);
            if (_tiles.Contains(border) && border.Tag is int index && index >= 0 && index < _display.Count)
                return IsToggleItem(_display[index].item);
        }
        return false;
    }

    /// <summary>点击源是否落在主页磁贴上：磁贴是自绘 Border（不是标准控件），供全局点击音效判定用</summary>
    public bool IsTileSource(object? source)
    {
        for (var el = source as DependencyObject; el is not null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is Border border && _tiles.Contains(border)) return true;
        }
        return false;
    }

    public void FocusGrid()
    {
        // 呼出主页时兜底复位：切页/组展开动画若在启动器隐藏期间被打断，状态会卡在"动画中"，
        // 造成后续切页无响应、磁贴停在半滑出位置；重新呼出时按静止状态恢复。
        _pageAnimating = false;
        _groupAnimating = false;
        PageSlide.X = 0;
        TileScroll.Opacity = 1;
        // 首次启动时窗口尺寸可能还没定下来，磁贴按早期宽度排过一次会整块偏右；
        // 每次呼出都按当前实际尺寸复核一次列数/偏移（列数不变只重设位置，不重建磁贴）。
        QueueResizeLayout();
        if (_tiles.Count > 0) UpdateSelection();
    }
}
