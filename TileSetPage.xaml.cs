using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 磁贴集副本页：打开某个磁贴集小组件时，展示其添加时复制的主页快照。
/// 磁贴样式、流式排布、选中/导航、组展开、音乐/图片小组件刷新均与主页一致（共用 TileStyle 构建）。
/// 手柄：方向=选择，A=启动/展开组，R3=编辑选中磁贴（与主页同理），LB/RB=翻页（选中音乐/图片磁贴时为切歌/切图），B/View=返回主页。
/// 快照内不含磁贴集磁贴本身（避免递归）。
/// </summary>
public sealed partial class TileSetPage : Page
{
    private readonly List<LaunchItem> _items = [];
    /// <summary>显示序列 = _items 展开组后的扁平视图（item, 所属组或 null）</summary>
    private readonly List<(LaunchItem item, LaunchItem? group)> _display = [];
    private readonly List<Border> _tiles = [];
    private readonly Dictionary<TileStyle.MusicTileRefs, int> _musicRefs = [];
    private readonly Dictionary<TileStyle.PhotoTileRefs, (int, string, LaunchItem)> _photoRefs = [];
    private readonly Dictionary<TileStyle.WeatherTileRefs, int> _weatherRefs = [];
    /// <summary>当前显示的手柄映射磁贴引用（重排时重建；value=(显示序列索引, 磁贴项)）</summary>
    private readonly Dictionary<TileStyle.GamepadMapTileRefs, (int index, LaunchItem item)> _remapRefs = [];
    private LaunchItem? _expanded;
    private bool _groupAnimating;
    private int _selected;
    /// <summary>已加载的磁贴集 Target：变化=打开的是另一个磁贴集（重置选中），不变=返回本页（保持选中）</summary>
    private string? _loadedTarget;

    // ---- 排列（L3 移动）模式状态，与主页同款 ----
    private bool _moving;
    private readonly List<LaunchItem> _marked = [];
    private LaunchItem? _grabbed;

    // ---- 长按设置磁贴进入编辑模式 + 指针拖拽磁贴（与主页 MainPage 同款手势层，改动需两页同步） ----
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
    private LaunchItem? _lpRemapItem;  // 长按目标=手柄映射磁贴时记录（长按=关闭映射）
    private LaunchItem? _lpOptiItem;   // 长按目标=OptiScaler 磁贴时记录（长按=注入）
    private Windows.Foundation.Point _lpStart;
    private uint _lpPointerId;

    // 设置磁贴徽章：拖拽悬停=垃圾桶；标记≥2=加号（点击组合）（与主页 MainPage 同款）
    private Border? _settingsTile;      // 当前设置磁贴元素（每次 LayoutTiles 重建时更新）
    private enum SettingsBadge { None, Trash, Plus }
    private SettingsBadge _badge;
    private FontIcon? _badgeIcon;
    private string? _badgeOrigGlyph;
    private bool _dragOverTrash;        // 拖拽中指针悬停在设置磁贴上

    /// <summary>本副本页展示的磁贴集（按 Target 从配置定位，编辑保存返回后重新加载）</summary>
    private LaunchItem? _set;

    /// <summary>磁贴集 Target（供 MainWindow 转发 R3 编辑时定位快照归属）</summary>
    public string SetTarget => _set?.Target ?? "";

    /// <summary>是否处于排列（L3 移动）模式：此期间 R3 不触发编辑</summary>
    public bool IsMoving => _moving;

    /// <summary>当前选中且可 R3 编辑的快照磁贴（组成员返回 null，与主页同规则）</summary>
    public LaunchItem? SelectedItem
    {
        get
        {
            if (_selected < 0 || _selected >= _display.Count) return null;
            var (item, group) = _display[_selected];
            if (group is not null) return null;
            return item;
        }
    }

    /// <summary>磁贴间距/单位与主页同源（TileStyle）</summary>
    private static double Gap => TileStyle.Gap;
    private const double Unit = TileStyle.Unit;

    public TileSetPage()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            TileHost.SizeChanged -= TileHost_SizeChanged;
            LayoutTiles();
            TileHost.SizeChanged += TileHost_SizeChanged;
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 载入磁贴集快照：按 Target 从配置重新定位（编辑保存返回后拿到最新数据），再深拷贝与主页数据隔离
        _items.Clear();
        if (e.Parameter is LaunchItem navSet)
        {
            _set = ConfigService.Load().FirstOrDefault(it =>
                it.Target == navSet.Target && it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.TileSet)
                ?? navSet;
            SetTitle.Text = _set.Name;
            foreach (var it in _set.Snapshot) _items.Add(it.Clone());
        }
        _expanded = null;
        // 打开不同磁贴集才重置选中；返回本页（同一磁贴集，如从设置/编辑页返回）保持进入前的选中
        if (_loadedTarget != _set?.Target) { _selected = 0; _loadedTarget = _set?.Target; }
        BuildDisplay();
        LayoutTiles();
        UpdateSelection();

        // 音乐/图片小组件在快照中照常工作：订阅状态刷新
        SystemMediaService.StateChanged += OnMusicStateChanged;
        _ = SystemMediaService.InitAsync();
        RefreshMusicTiles();
        SlideshowService.CurrentChanged += OnSlideshowChanged;
        SlideshowService.Start();
        RefreshPhotoTiles();
        // 天气小组件在快照中同样刷新
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
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 双保险：任何导航离开都取消未决长按，避免计时器在后台触发关闭程序/进入编辑模式（与主页同款）
        _lpTimer?.Stop();
        _lpTile = null;
        _lpCloseItem = null;
        _lpSyncItem = null;
        _lpRemapItem = null;
        _lpOptiItem = null;
        SystemMediaService.StateChanged -= OnMusicStateChanged;
        SlideshowService.CurrentChanged -= OnSlideshowChanged;
        WeatherService.StateChanged -= OnWeatherChanged;
        TrainerService.StatusChanged -= OnTrainerStatus;
        SaveBackupService.StatusChanged -= OnBackupStatus;
        OptiScalerService.StatusChanged -= OnOptiScalerStatus;
        GamepadRemapService.StateChanged -= OnRemapStateChanged;
    }

    private void OnMusicStateChanged() => DispatcherQueue.TryEnqueue(RefreshMusicTiles);
    private void OnSlideshowChanged() => DispatcherQueue.TryEnqueue(RefreshPhotoTiles);
    private void OnWeatherChanged() => DispatcherQueue.TryEnqueue(RefreshWeatherTiles);

    /// <summary>修改器取回进度：事件来自后台线程，marshal 回 UI 线程后用提示条反馈（失败用警告样式）</summary>
    private void OnTrainerStatus(TrainerMessage m) =>
        DispatcherQueue.TryEnqueue(() => ShowToast(m.Text, m.Error ? ToastKind.Warning : ToastKind.Success, m.Progress));

    /// <summary>游戏存档进度：事件来自后台线程，marshal 回 UI 线程后用提示条反馈（失败用警告样式）</summary>
    private void OnBackupStatus(BackupMessage m) =>
        DispatcherQueue.TryEnqueue(() => ShowToast(m.Text, m.Error ? ToastKind.Warning : ToastKind.Success));

    /// <summary>OptiScaler 进度：事件来自后台线程，marshal 回 UI 线程后用提示条反馈（失败用警告样式）</summary>
    private void OnOptiScalerStatus(OptiScalerMessage m) =>
        DispatcherQueue.TryEnqueue(() => ShowToast(m.Text, m.Error ? ToastKind.Warning : ToastKind.Success, m.Progress));

    private void TileHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_groupAnimating) return;
        LayoutTiles();
    }

    /// <summary>把 _items（组保持嵌套）按展开状态摊平为显示序列</summary>
    private void BuildDisplay()
    {
        _display.Clear();
        foreach (var it in _items)
        {
            // 快照里不应有磁贴集磁贴本身；防御性跳过，避免递归打开
            if (it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.TileSet) continue;
            _display.Add((it, null));
            if (it.Kind == LaunchKind.Group && ReferenceEquals(it, _expanded))
                foreach (var m in it.Members)
                    _display.Add((m, it));
        }
        if (_selected >= _display.Count) _selected = Math.Max(0, _display.Count - 1);
    }

    // ---- 布局（与主页同款流式排布） ----

    private static (int Cols, int Rows) CellsOf(TileSize size) => size switch
    {
        TileSize.Small => (1, 1),
        TileSize.Large => (2, 2),
        _ => (2, 1)
    };

    private void LayoutTiles()
    {
        TileHost.Children.Clear();
        _tiles.Clear();
        _musicRefs.Clear();
        _photoRefs.Clear();
        _weatherRefs.Clear();
        _remapRefs.Clear();
        // 磁贴重建：旧的徽章引用失效（与主页同款）
        _settingsTile = null;
        _badge = SettingsBadge.None;
        _badgeIcon = null;
        _badgeOrigGlyph = null;

        var availW = TileScroll.ViewportWidth > 0 ? TileScroll.ViewportWidth : TileHost.ActualWidth;
        if (availW <= 0) return;

        var cols = Math.Max(2, (int)((availW + Gap) / (Unit + Gap)));
        var occupied = new HashSet<(int, int)>();
        int maxRow = -1, maxCol = -1;
        var placed = new List<(Border tile, int col, int row)>();

        for (var i = 0; i < _display.Count; i++)
        {
            var (item, group) = _display[i];
            var (w, h) = item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Music
                ? (2, 2)
                : CellsOf(item.Size);

            (int c, int r) spot = (0, 0);
            bool found = false;
            for (var r = 0; !found && r < 1000; r++)
                for (var c = 0; c + w <= cols && !found; c++)
                    if (Fits(occupied, c, r, w, h)) { spot = (c, r); found = true; }
            if (!found) break;

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

        var usedW = maxCol < 0 ? 0 : (maxCol + 1) * (Unit + Gap) - Gap;
        var offsetX = Math.Max(0, (availW - usedW) / 2);
        foreach (var (tile, c, r) in placed)
        {
            Canvas.SetLeft(tile, offsetX + c * (Unit + Gap));
            Canvas.SetTop(tile, r * (Unit + Gap));
        }
        TileHost.Height = Math.Max(200, (maxRow + 1) * (Unit + Gap));

        UpdateSelection();
        RefreshSettingsBadge();
    }

    private static bool Fits(HashSet<(int, int)> occ, int c, int r, int w, int h)
    {
        for (var dy = 0; dy < h; dy++)
            for (var dx = 0; dx < w; dx++)
                if (occ.Contains((c + dx, r + dy))) return false;
        return true;
    }

    private Border CreateTile(LaunchItem item, LaunchItem? group, int index, int cellsW, int cellsH)
    {
        double width = cellsW * Unit + (cellsW - 1) * Gap;
        double height = cellsH * Unit + (cellsH - 1) * Gap;
        var noColor = TileStyle.IsNoColor(item);
        var accent = TileStyle.AccentFor(item);

        // 音乐小组件磁贴
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Music)
        {
            var (musicTile, refs) = TileStyle.BuildMusicTile(
                item, width, height, accent, noColor,
                SystemMediaService.Prev, SystemMediaService.TogglePlay, SystemMediaService.Next);
            musicTile.Tag = index;
            _musicRefs[refs] = index;
            // 单击=延迟到系统双击时长后选中（保留副页原有语义：音乐磁贴不切歌，切歌走 LB/RB）
            musicTile.Tapped += (_, _) => OnTileTapped(index, null);
            musicTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(musicTile, item, group, index, false);
            return musicTile;
        }

        // 图片组磁贴
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Photos)
        {
            var folder = item.WidgetFolder ?? "";
            SlideshowService.Register(folder, SlideshowService.IntervalOf(item.SlideshowSpeed));
            var img = SlideshowService.CurrentImage(folder);
            var placeholder = string.IsNullOrEmpty(folder) ? "图片组\n未定向文件夹"
                : !Directory.Exists(folder) ? "图片文件夹不存在"
                : !SlideshowService.IsScanned(folder) ? "正在扫描图片…"
                : "文件夹中没有图片";
            var (photoTile, refs) = TileStyle.BuildPhotoTile(item, width, height, img, placeholder);
            photoTile.Tag = index;
            _photoRefs[refs] = (index, folder, item);
            // 单击=延迟到系统双击时长后选中并切到下一张（保留副页原有语义）
            photoTile.Tapped += (_, _) => OnTileTapped(index, () => SlideshowService.Skip(folder, 1));
            photoTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(photoTile, item, group, index, false);
            return photoTile;
        }

        // 天气小组件磁贴（快照内照常工作）：A 键/点击 = 刷新
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Weather)
        {
            var (weatherTile, weatherRefs) = TileStyle.BuildWeatherTile(item, width, height, accent, noColor);
            weatherTile.Tag = index;
            _weatherRefs[weatherRefs] = index;
            weatherTile.Tapped += (_, _) => OnTileTapped(index, WeatherService.Refresh);
            weatherTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(weatherTile, item, group, index, false);
            TileStyle.FillWeatherTile(weatherRefs);
            return weatherTile;
        }

        // 映射小组件磁贴（快照内照常工作）：A 键/点击 = 触发映射
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Mapping)
        {
            var mappingTile = TileStyle.BuildMappingTile(item, width, height, accent, noColor);
            mappingTile.Tag = index;
            mappingTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            mappingTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(mappingTile, item, group, index, false);
            return mappingTile;
        }

        // 修改器小组件磁贴（快照内照常工作）：A 键/点击 = 按当前游戏进程自动下载并打开最新修改器
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Trainer)
        {
            var trainerTile = TileStyle.BuildTrainerTile(item, width, height, accent, noColor);
            trainerTile.Tag = index;
            trainerTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            trainerTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(trainerTile, item, group, index, false);
            return trainerTile;
        }

        // 游戏存档小组件磁贴（快照内照常工作）：A 键/点击 = 备份当前正在玩的游戏的存档
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Backup)
        {
            var backupTile = TileStyle.BuildBackupTile(item, width, height, accent, noColor);
            backupTile.Tag = index;
            backupTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            backupTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(backupTile, item, group, index, false);
            return backupTile;
        }

        // 手柄映射小组件磁贴（快照内照常工作）：显示开关状态，A 键/点击 = 开/关映射，长按 = 关闭映射
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.GamepadMap)
        {
            var (remapTile, remapRefs) = TileStyle.BuildGamepadMapTile(item, width, height, accent, noColor);
            remapTile.Tag = index;
            _remapRefs[remapRefs] = (index, item);
            remapTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            remapTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(remapTile, item, group, index, false);
            TileStyle.FillGamepadMapState(remapRefs, item);
            return remapTile;
        }

        // OptiScaler 小组件磁贴（快照内照常工作）：A 键/单点 = 发 Insert 键呼出游戏内 OptiScaler 菜单（注入走 Y/长按）
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.OptiScaler)
        {
            var optiTile = TileStyle.BuildOptiScalerTile(item, width, height, accent, noColor);
            optiTile.Tag = index;
            optiTile.Tapped += (_, _) => OnTileTapped(index, () => ActivateAt(index));
            optiTile.DoubleTapped += (_, _) => HandleDoubleTap(index);
            AttachEditGestures(optiTile, item, group, index, false);
            return optiTile;
        }

        // 标准磁贴（与主页共用构建）
        var tile = TileStyle.BuildStandardTile(item, group, width, height, accent, noColor);
        tile.Tag = index;

        // 移动模式已标记：青色描边提示；已抓取：黄色描边提示（与主页一致）
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

    /// <summary>按当前状态刷新设置磁贴徽章：拖拽悬停=垃圾桶；选中≥2=加号（点击组合）；否则恢复原图标（与主页 RefreshSettingsBadge 同款，改动需两页同步）</summary>
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
            _badgeIcon.Glyph = want == SettingsBadge.Trash ? "\uE74D" : "\uE710"; // 删除 / 加号
        }
    }

    // ---- 磁贴轻点/双击：单击延迟"系统双击时长"（默认 500ms）执行（启动/切图/仅选中），
    // 窗口内再点同一磁贴或事件 TapCount≥2 = 双击进编辑（等同 R3）----（与主页同款，改动需两页同步）
    private Microsoft.UI.Xaml.DispatcherTimer? _tapTimer;
    private int _pendingTapIndex = -1;
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

        // 双击的伴随事件（Tapped/DoubleTapped 先后顺序不定）：已处理则不再排程单击动作
        if ((DateTime.Now - _lastDoubleAt).TotalMilliseconds < 100) return;

        // 窗口内再点同一磁贴 = 双击：取消待执行的单击动作，进入编辑页
        if (_tapTimer is { IsEnabled: true } && _pendingTapIndex == index)
        {
            HandleDoubleTap(index);
            return;
        }

        _pendingTapIndex = index;
        _pendingAction = singleAction;
        if (_tapTimer == null)
        {
            // 与系统"双击速度"一致（用户可在鼠标设置里调整），兜底不低于 400ms
            var ms = Math.Max(400, (int)GetDoubleClickTime());
            _tapTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            _tapTimer.Tick += (_, _) =>
            {
                _tapTimer!.Stop();
                _pendingTapIndex = -1;
                var act = _pendingAction;
                _pendingAction = null;
                act?.Invoke();
            };
        }
        _tapTimer.Stop();
        _tapTimer.Start();
    }

    /// <summary>双击磁贴处理：取消待执行的单击动作并进编辑页；Tapped 与 DoubleTapped 双通道触发，100ms 内去重（与主页同款）</summary>
    private void HandleDoubleTap(int index)
    {
        if (_moving) return;
        if ((DateTime.Now - _lastDoubleAt).TotalMilliseconds < 100) return;
        _lastDoubleAt = DateTime.Now;
        _tapTimer?.Stop();
        _pendingTapIndex = -1;
        _pendingAction = null;
        // 双击的第二次"按下"已启动 600ms 长按计时并 CapturePointer；切页后磁贴脱离可视树，
        // 第二次 PointerReleased 不再送达 → 计时器不会被取消，会在后台误触发。导航前先取消。
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

    /// <summary>给磁贴挂编辑手势（与主页 AttachEditGestures 同款，改动需两页同步）：普通模式下长按设置磁贴=进入编辑模式、
    /// 长按程序/网站/文件夹=关闭；编辑模式内按住拖动=移动磁贴（放到别的磁贴/空位=交换位置，放到设置磁贴（垃圾桶）上=删除）；
    /// 编辑模式内轻点磁贴=标记选中，轻点设置磁贴：标记≥2=组合成磁贴组（加号），否则=退出编辑模式。</summary>
    private void AttachEditGestures(Border tile, LaunchItem item, LaunchItem? group, int index, bool isSettings)
    {
        tile.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            _suppressTap = false;
            var p = e.GetCurrentPoint(TileHost).Position;

            // 普通模式长按：设置磁贴=进入编辑模式；程序/网站/文件夹磁贴=关闭其前台窗口（等同 Y 键）；
            // 修改器小组件磁贴=关闭正在运行的修改器（等同 Y 键）；游戏存档小组件磁贴=同步存档（等同 Y 键）；
            // 手柄映射小组件磁贴=关闭映射（等同长按关闭）
            if (!_moving && group is null &&
                (isSettings || item.Kind is LaunchKind.App or LaunchKind.Url or LaunchKind.Folder
                 || (item.Kind == LaunchKind.Widget && item.WidgetKind is WidgetKind.Trainer or WidgetKind.Backup or WidgetKind.GamepadMap or WidgetKind.OptiScaler)))
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
            RefreshSettingsBadge();
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
        else if (closeItem is not null) CloseItem(closeItem);                      // 长按可关闭磁贴=关程序/网站/文件夹
        else EnterEditMode();                                                     // 长按设置磁贴=进入编辑模式
    }

    private static double Dist(Windows.Foundation.Point a, Windows.Foundation.Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>拖拽放下：命中设置磁贴（垃圾桶）=删除；命中其他磁贴或空位=交换位置移动（组合用轻点标记+加号）（与主页同款）</summary>
    private void DropDragged(Windows.Foundation.Point p, LaunchItem dragged, Border? dragTile)
    {
        _dragOverTrash = false;
        RefreshSettingsBadge();
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
                ShowToast($"已删除 {dragged.Name}");
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

    /// <summary>放到空位：与中心距落点最近的顶层磁贴交换位置（与主页同款）</summary>
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

    /// <summary>两个顶层磁贴交换位置（拖拽移动），带 200ms 滑动换位动画；持久化走副本页的 PersistSnapshot（与主页同款动画，不改副页手柄重建式 SwapInDirection）</summary>
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

    // 换位动画状态：磁贴先偏移到旧位置，再逐帧滑回新位置（与主页同款）
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

    /// <summary>换位后重排并把选中跟随到 follow；持久化走副本页的 PersistSnapshot（对齐主页 CommitLayout，仅持久化方式不同）</summary>
    private void CommitLayout(LaunchItem follow)
    {
        PersistSnapshot();
        BuildDisplay();
        LayoutTiles();
        var ni = _display.IndexOf((follow, null));
        if (ni >= 0) _selected = ni;
        else _selected = Math.Clamp(_selected, 0, Math.Max(0, _display.Count - 1));
        UpdateSelection();
    }

    // ---- 动作 ----

    private void ActivateAt(int index)
    {
        if (index < 0 || index >= _display.Count) return;
        var (item, group) = _display[index];
        if (item.Kind == LaunchKind.Widget && group is null)
        {
            if (item.WidgetKind == WidgetKind.Photos) SlideshowService.Skip(item.WidgetFolder ?? "", 1);
            else if (item.WidgetKind == WidgetKind.Settings) App.Main?.NavigateSettings(typeof(SettingsPage));
            else if (item.WidgetKind == WidgetKind.Weather) WeatherService.Refresh();
            else if (item.WidgetKind == WidgetKind.Mapping) TriggerMapping(item);
            else if (item.WidgetKind == WidgetKind.Trainer) TrainerService.Activate();
            else if (item.WidgetKind == WidgetKind.Backup) SaveBackupService.Activate(item);
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
            else SystemMediaService.TogglePlay();
            return;
        }
        if (group is not null || item.Kind != LaunchKind.Group)
        {
            LaunchService.Launch(item);
            if (SettingsService.Current.HideAfterLaunch) App.Main?.HideToTray();
            return;
        }
        if (_groupAnimating) return;
        ToggleGroup(item);
    }

    /// <summary>映射触发中（防止"手柄键=A"这类映射递归触发自身）</summary>
    private bool _mappingInjecting;

    /// <summary>
    /// 触发映射小组件（与主页同款）：键盘键 → 收起窗口后 SendInput 发给此前的前台程序；
    /// 手柄键 → 等价于在本页按下该手柄键（注入本页处理）。
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
    /// （OptiScaler 默认用 Insert 开关它的设置菜单）。注入走 Y 键 / 触屏长按（与主页同款）。
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

    /// <summary>关闭磁贴对应的前台窗口（Y 键与长按共用）：程序/网站/文件夹；修改器小组件=关闭修改器
    /// （与主页 CloseItem 同款，改动需两页同步）</summary>
    private void CloseItem(LaunchItem item)
    {
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

    /// <summary>提示条语义：决定图标徽章与强调色</summary>
    private enum ToastKind { Success, Warning }

    /// <summary>临时提示条：玻璃质感药丸，2 秒后自动隐藏（与主页同款样式）。
    /// 成功=青色对勾，警告=琥珀感叹号；位置在磁贴画布底部（手柄提示条上方），首次出现时淡入 + 自下方归位。
    /// progress 非空表示这是一条联网下载进度：文案下方显示细进度条，且只要还有进度上报就不自动收起。</summary>
    private Microsoft.UI.Xaml.DispatcherTimer? _toastTimer;
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
            MoveHintBar.Visibility = Visibility.Visible;
            PlayToastIn();
        }
        if (_toastTimer == null)
        {
            _toastTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                MoveHintBar.Visibility = Visibility.Collapsed;
            };
        }
        // 下载中每条进度都会重置计时器（正常下载不会中途消失），卡住超过 6 秒才收起
        _toastTimer.Interval = TimeSpan.FromMilliseconds(progress is null ? 2000 : 6000);
        _toastTimer.Stop();
        _toastTimer.Start();
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

    private void ToggleGroup(LaunchItem group)
    {
        _expanded = ReferenceEquals(_expanded, group) ? null : group;
        BuildDisplay();
        LayoutTiles();
        UpdateSelection();
    }

    // ---- 选择 / 导航（与主页同款） ----

    private void UpdateSelection()
    {
        for (var i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            if (i == _selected)
            {
                t.BorderThickness = new Thickness(4);
                t.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
                Canvas.SetZIndex(t, 1);
            }
            else
            {
                // 排列模式标记/抓取描边优先于默认描边
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
                Canvas.SetZIndex(t, 0);
            }
        }
        if (_selected >= 0 && _selected < _tiles.Count)
            TileScroll.ChangeView(null, Math.Max(0, Canvas.GetTop(_tiles[_selected]) - 40), null, false);
        UpdateHintForSelection();
    }

    /// <summary>副本页常规提示条（非音乐/图片磁贴选中时）；程序/网站/文件夹磁贴含 Y:关闭，组/小组件不含</summary>
    private string NormalHints => IsClosableSelected()
        ? "DPAD:选择|A:启动|Y:关闭|R3:编辑|L3:排列|LB/RB:翻页|B:返回"
        : "DPAD:选择|A:启动|R3:编辑|L3:排列|LB/RB:翻页|B:返回";

    /// <summary>选中磁贴是否可 Y 关闭（程序/网站/文件夹=可；组/小组件=不可）</summary>
    private bool IsClosableSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind is LaunchKind.App or LaunchKind.Url or LaunchKind.Folder;
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

    /// <summary>按当前选中项替换底部提示条（与主页同款）：音乐/图片组磁贴=只显示对应操作提示，其他=恢复副本页常规提示（排列模式不处理，由 UpdateHintBar 负责）</summary>
    private void UpdateHintForSelection()
    {
        if (_moving) return;
        if (IsMusicSelected())
            HintBar.SetHints("DPAD:切换选择|A:播放/暂停|LB:上一曲|RB:下一曲");
        else if (IsPhotoSelected())
            HintBar.SetHints("DPAD:切换选择|A:切换|LB:上一张|RB:下一张");
        else if (IsWeatherSelected())
            HintBar.SetHints("DPAD:切换选择|A:刷新天气|R3:编辑|L3:排列|B:返回");
        else if (IsMappingSelected())
            HintBar.SetHints("DPAD:切换选择|A:触发映射|R3:编辑|L3:排列|B:返回");
        else if (IsTrainerSelected())
            HintBar.SetHints("DPAD:切换选择|A:打开修改器|Y:关闭修改器|R3:编辑|L3:排列|B:返回");
        else if (IsBackupSelected())
            HintBar.SetHints("DPAD:切换选择|A:备份存档|Y:同步存档|R3:编辑|L3:排列|B:返回");
        else if (IsGamepadMapSelected())
            HintBar.SetHints("DPAD:切换选择|A:开启/关闭映射|长按:关闭映射|R3:编辑|L3:排列|B:返回");
        else if (IsOptiSelected())
            HintBar.SetHints("DPAD:切换选择|A:呼出OptiScaler菜单|Y:注入|长按:注入|R3:编辑|L3:排列|B:返回");
        else
            HintBar.SetHints(NormalHints);
    }

    private void MoveSelection(int dx, int dy)
    {
        if (_tiles.Count == 0) return;
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
        }
    }

    private static (double X, double Y) Center(Border b) =>
        (Canvas.GetLeft(b) + b.Width / 2, Canvas.GetTop(b) + b.Height / 2);

    private bool IsMusicSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Music;
    }

    private bool IsPhotoSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Photos;
    }

    private string SelectedFolder()
    {
        if (_selected < 0 || _selected >= _display.Count) return "";
        return _display[_selected].item.WidgetFolder ?? "";
    }

    private void PageScroll(bool forward)
    {
        TileScroll.ChangeView(null,
            TileScroll.VerticalOffset + (forward ? 1 : -1) * (TileScroll.ViewportHeight - 40), null);
    }

    // ---- 刷新音乐/图片磁贴内容（与主页同款） ----

    private void RefreshMusicTiles()
    {
        if (_musicRefs.Count == 0) return;
        var playing = SystemMediaService.IsPlaying;
        var has = SystemMediaService.HasSession;
        var title = has ? SystemMediaService.Title : "";
        if (has && title.Length == 0) title = "未知曲目";
        var glyph = playing ? "\uE769" : "\uE768";
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

    /// <summary>刷新所有天气小组件磁贴的显示（只改文字不重建磁贴）</summary>
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
                if (item.PhotoAnimation) FadeIn(refs.Img);
            }
            else if (img == null)
            {
                refs.Img.Visibility = Visibility.Collapsed;
                refs.Placeholder.Visibility = Visibility.Visible;
            }
        }
    }

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

    /// <summary>
    /// 处理手柄按键。返回 true = 已消费。B/View 由 MainWindow 处理为返回主页。
    /// L3 进入排列模式后：方向=移动/选择、X=多选标记、A=抓取/组合、Y=删除、B/L3=退出。
    /// </summary>
    public bool HandleGamepad(GamepadButtons buttons)
    {
        if (_moving)
        {
            if (buttons.HasFlag(GamepadButtons.DPadUp)) { if (_grabbed is not null) SwapInDirection(0, -1); else MoveSelection(0, -1); return true; }
            if (buttons.HasFlag(GamepadButtons.DPadDown)) { if (_grabbed is not null) SwapInDirection(0, 1); else MoveSelection(0, 1); return true; }
            if (buttons.HasFlag(GamepadButtons.DPadLeft)) { if (_grabbed is not null) SwapInDirection(-1, 0); else MoveSelection(-1, 0); return true; }
            if (buttons.HasFlag(GamepadButtons.DPadRight)) { if (_grabbed is not null) SwapInDirection(1, 0); else MoveSelection(1, 0); return true; }
            if (buttons.HasFlag(GamepadButtons.X)) { ToggleMark(); return true; }
            if (buttons.HasFlag(GamepadButtons.Y)) { DeleteSelected(); return true; }
            if (buttons.HasFlag(GamepadButtons.A))
            {
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
        if (buttons.HasFlag(GamepadButtons.DPadDown)) { MoveSelection(0, 1); return true; }
        if (buttons.HasFlag(GamepadButtons.DPadLeft)) { MoveSelection(-1, 0); return true; }
        if (buttons.HasFlag(GamepadButtons.DPadRight)) { MoveSelection(1, 0); return true; }
        if (buttons.HasFlag(GamepadButtons.A)) { ActivateAt(_selected); return true; }
        // 常规模式 Y：OptiScaler 磁贴=注入；游戏存档磁贴=同步存档（用最近一份备份覆盖回本地）；否则关闭选中磁贴对应的运行中程序
        if (buttons.HasFlag(GamepadButtons.Y))
        {
            if (IsOptiSelected()) OptiScalerService.Activate(_display[_selected].item);
            else if (IsBackupSelected()) SaveBackupService.Sync(_display[_selected].item);
            else CloseSelected();
            return true;
        }
        if (buttons.HasFlag(GamepadButtons.LeftShoulder))
        {
            if (IsMusicSelected()) { SystemMediaService.Prev(); return true; }
            if (IsPhotoSelected()) { SlideshowService.Skip(SelectedFolder(), -1); return true; }
            PageScroll(false); return true;
        }
        if (buttons.HasFlag(GamepadButtons.RightShoulder))
        {
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

    /// <summary>进入编辑（排列）模式：标题换成"编辑模式"大字，提示条换成编辑操作提示。
    /// L3 与长按设置磁贴共用（结构对齐主页 EnterEditMode）；重排一次给磁贴挂上拖拽手势，并抖一下作为过渡反馈。</summary>
    private void EnterEditMode()
    {
        if (_moving) return;
        _moving = true;
        SetTitle.Visibility = Visibility.Collapsed;
        EditModeText.Visibility = Visibility.Visible;
        UpdateHintBar();
        LayoutTiles();
        ShakeTiles();
    }

    private void EndMove()
    {
        _moving = false;
        _marked.Clear();
        _grabbed = null;
        // 复位拖拽状态并还原设置磁贴徽章（与主页一致）
        _dragItem = null;
        _dragTile = null;
        _dragMoved = false;
        _dragOverTrash = false;
        RefreshSettingsBadge();
        // 恢复标题与常规提示条
        EditModeText.Visibility = Visibility.Collapsed;
        SetTitle.Visibility = Visibility.Visible;
        HintBar.SetHints(NormalHints);
        PersistSnapshot();
        LayoutTiles();
        UpdateSelection();
        ShakeTiles(); // 与主页一致：退出编辑模式同样抖一下，让"回到普通模式"也有明确反馈
    }

    /// <summary>进入/退出编辑模式的磁贴抖动：每个磁贴快速左右各摆几下后回正，按序号错开起播时间形成"波纹"。
    /// 走 Composition 层的 RotationAngleInDegrees（不改 RenderTransform），因此与拖拽的 TranslateTransform、
    /// 换位/展开动画互不干扰；动画收在 0°，结束后不留偏移。与主页 ShakeTiles 同款。</summary>
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

    /// <summary>排列模式 A 键：抓取当前磁贴（此后方向键=移动它）/ 再按放下</summary>
    private void ToggleGrab()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        if (_grabbed is not null) _grabbed = null;
        else
        {
            if (group is not null || item.Kind == LaunchKind.Group) return; // 组成员/组本体不可抓取
            _grabbed = item;
        }
        UpdateHintBar();
        LayoutTiles();
        UpdateSelection();
    }

    /// <summary>排列模式 X 键：标记/取消标记当前顶层磁贴（已标记青色描边）（与主页 ToggleMark 同款）</summary>
    private void ToggleMark()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        if (group is not null || item.Kind == LaunchKind.Group) return; // 组成员/组本体不参与
        if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Settings) return; // 设置磁贴不参与组合
        if (_marked.Contains(item)) _marked.Remove(item);
        else _marked.Add(item);
        UpdateHintBar();
        LayoutTiles();
        UpdateSelection();
    }

    /// <summary>排列模式 A 键：把已标记（≥2）的磁贴组合为一个组磁贴（放在第一个标记项位置）</summary>
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
        PersistSnapshot();
        BuildDisplay();
        LayoutTiles();
        UpdateSelection();
        UpdateHintBar();
    }

    /// <summary>排列模式（已抓取）：把抓取中的磁贴与指定方向最近邻磁贴交换位置</summary>
    private void SwapInDirection(int dx, int dy)
    {
        if (_grabbed is null || _tiles.Count == 0) return;
        var gi = _display.IndexOf((_grabbed, null));
        if (gi < 0) return;
        var (gx, gy) = Center(_tiles[gi]);

        int best = -1;
        double bestScore = double.MaxValue;
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (i == gi) continue;
            var (_, groupB) = _display[i];
            if (groupB is not null) continue; // 组成员不参与交换
            var (x, y) = Center(_tiles[i]);
            var along = dx != 0 ? (x - gx) * dx : (y - gy) * dy;
            if (along <= 1) continue;
            var cross = dx != 0 ? Math.Abs(y - gy) : Math.Abs(x - gx);
            var score = cross * 10 + along;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return;

        var itemA = _grabbed;
        var (itemT, _) = _display[best];
        var ia = _items.IndexOf(itemA);
        var ib = _items.IndexOf(itemT);
        if (ia < 0 || ib < 0) return;
        (_items[ia], _items[ib]) = (_items[ib], _items[ia]);
        PersistSnapshot();
        BuildDisplay();
        LayoutTiles();
        var newIdx = _display.IndexOf((itemA, null));
        if (newIdx >= 0) _selected = newIdx;
        UpdateSelection();
    }

    /// <summary>删除当前选中磁贴：展开中的组=仅删组本体（成员回到顶层原位）；未展开的组=整组删除（含成员）；成员=移出组；普通=删除</summary>
    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        DeleteItem(item, group);
    }

    /// <summary>删除指定磁贴（Y 键/拖到设置磁贴上共用）：组=拆分组；成员=移出组；普通=删除（与主页 DeleteItem 同款，仅持久化走 PersistSnapshot）</summary>
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
        PersistSnapshot();
        BuildDisplay();
        LayoutTiles();
        UpdateSelection();
        UpdateHintBar();
    }

    private void UpdateHintBar()
    {
        HintBar.SetHints(_marked.Count >= 2
            ? $"DPAD:移动选择|X:取消标记|A:组合({_marked.Count})|Y:删除|B:退出"
            : _grabbed is not null
                ? $"DPAD:移动「{_grabbed.Name}」|A:放下|X:标记|Y:删除|B:退出"
                : "DPAD:选择|X:多选标记|A:抓取移动|Y:删除|B:退出");
    }

    /// <summary>把当前排列结果写回磁贴集快照并落盘（副本页与主页数据隔离，仅更新该集的 Snapshot）</summary>
    private void PersistSnapshot()
    {
        if (_set is null) return;
        var items = ConfigService.Load();
        var set = items.FirstOrDefault(it =>
            it.Target == _set.Target && it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.TileSet);
        if (set is null) return;
        set.Snapshot = _items.Select(it => it.Clone()).ToList();
        _set = set;
        ConfigService.Save(items);
    }
}
