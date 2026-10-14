using System.Collections.ObjectModel;
using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 主页：Win10 磁贴风格画布（小/长方形/大三种尺寸，自动流式排布）。
/// 手柄：方向=选择，A=启动（组磁贴=展开/收起），Y=关闭程序（编辑模式内 Y=删除，组=拆分组，成员=移出组），
/// LB/RB=翻页；L3=移动模式：A 抓取磁贴后方向=移动、再 A 放下，X=多选标记、标记≥2 按 A 组合为组，B/L3=退出。
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

    // ---- 长按设置磁贴进入编辑模式 + 指针拖拽磁贴 ----
    private LaunchItem? _dragItem;      // 正在拖拽的顶层磁贴
    private Border? _dragTile;
    private Windows.Foundation.Point _dragStart;  // 按下点（相对 TileHost）
    private uint _dragPointerId;
    private bool _dragMoved;            // 超过阈值=进入拖拽（否则视为轻点）
    private bool _suppressTap;          // 长按/拖拽完成后吞掉随后的 Tapped
    private Microsoft.UI.Xaml.DispatcherTimer? _lpTimer;  // 长按计时器
    private Border? _lpTile;
    private LaunchItem? _lpCloseItem;  // 长按目标=可关闭磁贴时记录（null=设置磁贴进编辑）
    private Windows.Foundation.Point _lpStart;
    private uint _lpPointerId;

    // 设置磁贴徽章：拖拽悬停=垃圾桶；标记≥2=加号（点击组合）
    private Border? _settingsTile;      // 当前设置磁贴元素（每次 LayoutTiles 重建时更新）
    private enum SettingsBadge { None, Trash, Plus }
    private SettingsBadge _badge;
    private FontIcon? _badgeIcon;
    private string? _badgeOrigGlyph;
    private bool _dragOverTrash;        // 拖拽中指针悬停在设置磁贴上

    /// <summary>当前显示的音乐小组件磁贴引用（重排时重建；key=磁贴元素引用，value=显示序列索引）</summary>
    private readonly Dictionary<TileStyle.MusicTileRefs, int> _musicRefs = [];

    /// <summary>当前显示的图片组磁贴引用（key=磁贴元素引用，value=(显示序列索引, 定向文件夹, 设置项)）</summary>
    private readonly Dictionary<TileStyle.PhotoTileRefs, (int index, string folder, LaunchItem item)> _photoRefs = [];

    /// <summary>磁贴间距（像素，可在外观设置调节）；磁贴基本单位固定</summary>
    private static double Gap => TileStyle.Gap;
    private const double Unit = TileStyle.Unit;

    public MainPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            LayoutTiles();
            UpdateSelection();
        };

        // 顶栏时钟：每秒刷新（页面离开时停止，避免后台空转）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();

        // 页面尺寸变化（窗口/全屏切换）时重算列数重排；先取消订阅再重排，避免 Height/MinHeight 修改引发自激重排循环
        SizeChanged += (_, _) =>
        {
            TileHost.SizeChanged -= TileHost_SizeChanged;
            LayoutTiles();
            TileHost.SizeChanged += TileHost_SizeChanged;
        };

        // 添加方式遮罩：鼠标点击选项/背景
        ChoiceCustom.Tapped += (_, _) => { CloseChoice(); App.Main?.RootNavigateAddItem(); };
        ChoiceWidget.Tapped += (_, _) => { CloseChoice(); App.Main?.RootNavigateWidget(); };
        ChoiceOverlay.Tapped += (_, _) => CloseChoice();
    }

    private readonly DispatcherTimer _clockTimer;

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
            if (_selected < 0 || _selected >= _display.Count) return null;
            var (item, group) = _display[_selected];
            // 组成员不支持编辑页编辑（展开的组按 R3 编辑组本体：名称/大小/颜色/图标/图片）；
            // 音乐/图片/磁贴集小组件支持 R3 编辑（表单按 _isWidget 裁剪）
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
        Reload(); // 内部 UpdateSelection 会按当前选中项设置底部提示条
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
    }

    private void OnMusicStateChanged() =>
        DispatcherQueue.TryEnqueue(RefreshMusicTiles);

    private void OnSlideshowChanged() =>
        DispatcherQueue.TryEnqueue(RefreshPhotoTiles);

    /// <summary>按设置显示/隐藏顶栏时钟、网络/电池与手柄图标</summary>
    public void ApplyTopbarVisibility()
    {
        var s = SettingsService.Current;
        ClockPanel.Visibility = s.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        GamepadPanel.Visibility = s.ShowGamepadIcon ? Visibility.Visible : Visibility.Collapsed;
        UpdateBattery();
        UpdateNetwork();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _clockTimer.Stop();
        SystemMediaService.StateChanged -= OnMusicStateChanged;
        SlideshowService.CurrentChanged -= OnSlideshowChanged;
        SlideshowService.Stop();
    }

    public void Reload()
    {
        _items.Clear();
        foreach (var it in ConfigService.Load()) _items.Add(it);
        _expanded = null;
        _marked.Clear();
        if (_selected >= _items.Count) _selected = Math.Max(0, _items.Count - 1);
        BuildDisplay();
        LayoutTiles();
        UpdateSelection();
    }

    /// <summary>把 _items（组保持嵌套）按展开状态摊平为显示序列</summary>
    private void BuildDisplay()
    {
        _display.Clear();
        foreach (var it in _items)
        {
            _display.Add((it, null));
            if (it.Kind == LaunchKind.Group && ReferenceEquals(it, _expanded))
                foreach (var m in it.Members)
                    _display.Add((m, it));
        }
        if (_selected >= _display.Count) _selected = Math.Max(0, _display.Count - 1);
    }

    // ---- 桌面拖入：快捷方式/程序/网站/文件夹 → 自动识别生成磁贴 ----

    private void OnDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        // 仅接受文件拖放（从桌面/资源管理器拖入）
        e.AcceptedOperation = e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        if (e.DragUIOverride != null) e.DragUIOverride.Caption = "添加到 Game Locker";
    }

    private async void OnDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;

        var added = 0;
        var skipped = 0;
        var list = ConfigService.Load();
        foreach (var storage in items)
        {
            var path = storage.Path;
            if (string.IsNullOrEmpty(path)) continue;
            var it = DropImportService.FromDroppedPath(path);
            if (it == null) { skipped++; continue; }
            // 去重：Target 已存在（顶层或组内成员）则跳过
            if (list.Any(x => x.Target == it.Target) ||
                list.Any(x => x.Kind == LaunchKind.Group && x.Members.Any(m => m.Target == it.Target)))
            { skipped++; continue; }
            if (it.Kind != LaunchKind.Url)
                it.IconFile = await IconExtractService.ExtractAsync(it.Target);
            list.Add(it);
            added++;
        }
        if (added > 0) ConfigService.Save(list);

        Reload();
        ShowDropToast(added, skipped);
    }

    /// <summary>临时提示条：2 秒后自动隐藏（拖入结果、关闭反馈、删除反馈等复用）</summary>
    private Microsoft.UI.Xaml.DispatcherTimer? _dropToastTimer;

    private void ShowToast(string text)
    {
        MoveHintText.Text = text;
        MoveHintBar.Visibility = Visibility.Visible;
        if (_dropToastTimer == null)
        {
            _dropToastTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _dropToastTimer.Tick += (_, _) =>
            {
                _dropToastTimer!.Stop();
                // 顶栏 toast 到时直接隐藏；底部操作提示条（HintBar）由 UpdateHintBar 独立管理，互不影响
                MoveHintBar.Visibility = Visibility.Collapsed;
            };
        }
        _dropToastTimer.Stop();
        _dropToastTimer.Start();
    }

    private void ShowDropToast(int added, int skipped)
    {
        ShowToast(added switch
        {
            0 => skipped > 0 ? "拖入项已存在或不支持，未添加" : "未识别到可添加项",
            _ => skipped > 0 ? $"已添加 {added} 个磁贴（跳过 {skipped} 个重复/不支持）" : $"已添加 {added} 个磁贴"
        });
    }

    /// <summary>
    /// 处理手柄按键。返回 true = 已消费（MainWindow 跳过默认路由）。
    /// </summary>
    public bool HandleGamepad(GamepadButtons buttons)
    {
        // 添加方式选择遮罩打开：按键优先交给遮罩
        if (_choiceOpen) return HandleChoice(buttons);

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
        if (buttons.HasFlag(GamepadButtons.DPadDown)) { MoveSelection(0, 1); return true; }
        if (buttons.HasFlag(GamepadButtons.DPadLeft)) { MoveSelection(-1, 0); return true; }
        if (buttons.HasFlag(GamepadButtons.DPadRight)) { MoveSelection(1, 0); return true; }
        if (buttons.HasFlag(GamepadButtons.A)) { ActivateSelected(); return true; }
        // 常规模式 Y=关闭：结束选中磁贴对应的运行中程序（编辑模式内 Y 仍为删除）
        if (buttons.HasFlag(GamepadButtons.Y)) { CloseSelected(); return true; }
        // 普通模式不响应其他键（防误删）：仅 L3 编辑/移动模式内 Y=删除
        if (buttons.HasFlag(GamepadButtons.LeftShoulder))
        {
            // 选中音乐小组件：LB=上一曲；选中图片组：LB=上一张；否则翻页
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

    /// <summary>进入编辑（移动）模式：时钟+日期换成"编辑模式"大字，提示条换成编辑操作提示。
    /// L3 与长按设置磁贴共用；重排一次以给磁贴挂上拖拽手势。</summary>
    private void EnterEditMode()
    {
        if (_moving) return;
        _moving = true;
        ClockText.Visibility = Visibility.Collapsed;
        DateText.Visibility = Visibility.Collapsed;
        EditModeText.Visibility = Visibility.Visible;
        UpdateHintBar();
        LayoutTiles();
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
        RefreshSettingsBadge();
        // 恢复时钟+日期模块与底部常规提示条
        EditModeText.Visibility = Visibility.Collapsed;
        ClockText.Visibility = Visibility.Visible;
        DateText.Visibility = Visibility.Visible;
        UpdateClock();
        LayoutTiles();
        UpdateSelection(); // 末尾按当前选中项恢复底部提示条（音乐磁贴=音乐提示）
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

    private static (int Cols, int Rows) CellsOf(TileSize size) => size switch
    {
        TileSize.Small => (1, 1),
        TileSize.Large => (2, 2),
        _ => (2, 1)
    };

    /// <summary>自动流式排布：每个磁贴放入第一个能容纳它的空位（按 _display 显示序列）</summary>
    private void LayoutTiles()
    {
        TileHost.Children.Clear();
        _tiles.Clear();
        _musicRefs.Clear();
        _photoRefs.Clear();
        // 磁贴重建：旧的徽章引用失效
        _settingsTile = null;
        _badge = SettingsBadge.None;
        _badgeIcon = null;
        _badgeOrigGlyph = null;

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
            // 组成员保持自身原有尺寸/样式；音乐小组件固定 2×2，图片组可编辑尺寸
            var (w, h) = item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Music
                ? (2, 2)
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
    {
        for (var dy = 0; dy < h; dy++)
            for (var dx = 0; dx < w; dx++)
                if (occ.Contains((c + dx, r + dy))) return false;
        return true;
    }

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

        // 标准磁贴（背景图片铺满 / 强调色底+图标+名称）：与磁贴集副本页共用同一构建
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

    // ---- 磁贴轻点/双击：单击延迟"系统双击时长"（默认 500ms）执行（启动/切图），
    // 窗口内再点同一磁贴或事件 TapCount≥2 = 双击进编辑（等同 R3）----
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

    /// <summary>双击磁贴处理：取消待执行的单击动作并进编辑页；Tapped 与 DoubleTapped 双通道触发，100ms 内去重</summary>
    private void HandleDoubleTap(int index)
    {
        if (_moving) return;
        if ((DateTime.Now - _lastDoubleAt).TotalMilliseconds < 100) return;
        _lastDoubleAt = DateTime.Now;
        _tapTimer?.Stop();
        _pendingTapIndex = -1;
        _pendingAction = null;
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

            // 普通模式长按：设置磁贴=进入编辑模式；程序/网站/文件夹磁贴=关闭其前台窗口（等同 Y 键）
            if (!_moving && group is null &&
                (isSettings || item.Kind is LaunchKind.App or LaunchKind.Url or LaunchKind.Folder))
            {
                _lpTimer ??= new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _lpTimer.Stop();
                _lpTimer.Tick -= OnLongPressTick;
                _lpTimer.Tick += OnLongPressTick;
                _lpTile = tile;
                _lpCloseItem = isSettings ? null : item;
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
                if (Dist(p, _lpStart) > 12) { _lpTimer?.Stop(); _lpTile = null; _lpCloseItem = null; } // 移走了=取消长按
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
            }
        }), true);
    }

    private void OnLongPressTick(object sender, object args)
    {
        ((Microsoft.UI.Xaml.DispatcherTimer)sender).Stop();
        _lpTile = null;
        _suppressTap = true;
        var closeItem = _lpCloseItem;
        _lpCloseItem = null;
        if (closeItem is not null) CloseItem(closeItem); // 长按可关闭磁贴=关程序/网站/文件夹
        else EnterEditMode();                             // 长按设置磁贴=进入编辑模式
    }

    private static double Dist(Windows.Foundation.Point a, Windows.Foundation.Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>拖拽放下：命中设置磁贴（垃圾桶）=删除；命中其他磁贴或空位=交换位置移动（不再拖拽组合，组合用轻点标记+加号）</summary>
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
            if (item.WidgetKind == WidgetKind.Photos)
            {
                // 图片组：A = 手动切到下一张
                SlideshowService.Skip(item.WidgetFolder ?? "", 1);
            }
            else if (item.WidgetKind == WidgetKind.TileSet)
            {
                // 磁贴集：A = 打开其添加时复制的主页快照
                App.Main?.OpenTileSet(item);
            }
            else if (item.WidgetKind == WidgetKind.Settings)
            {
                // 设置磁贴：A = 进入软件设置页
                App.Main?.NavigateSettings(typeof(SettingsPage));
            }
            else
            {
                // 音乐小组件：A = 播放/暂停系统当前媒体
                SystemMediaService.TogglePlay();
            }
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

    /// <summary>当前选中的是否为顶层图片组磁贴（决定 LB/RB 是切图还是翻页）</summary>
    private bool IsPhotoSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Photos;
    }

    /// <summary>当前选中磁贴定向的图片文件夹（未选中/无文件夹返回空串）</summary>
    private string SelectedFolder()
    {
        if (_selected < 0 || _selected >= _display.Count) return "";
        return _display[_selected].item.WidgetFolder ?? "";
    }

    // ---- 添加方式选择遮罩（X 键弹出：自定义 / 小组件） ----

    private bool _choiceOpen;
    private int _choiceIndex; // 0=自定义 1=小组件

    /// <summary>打开添加方式选择遮罩（MainWindow 的 X 键调用）</summary>
    public void OpenAddChoice()
    {
        if (_moving) return; // 移动模式下不弹
        _choiceOpen = true;
        _choiceIndex = 0;
        ChoiceOverlay.Visibility = Visibility.Visible;
        UpdateChoiceCursor();
    }

    private void CloseChoice()
    {
        _choiceOpen = false;
        ChoiceOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>遮罩内手柄按键：方向=切换，A=确认，B/View/X=取消</summary>
    private bool HandleChoice(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.DPadUp) || b.HasFlag(GamepadButtons.DPadLeft))
        { _choiceIndex = 0; UpdateChoiceCursor(); return true; }
        if (b.HasFlag(GamepadButtons.DPadDown) || b.HasFlag(GamepadButtons.DPadRight))
        { _choiceIndex = 1; UpdateChoiceCursor(); return true; }
        if (b.HasFlag(GamepadButtons.A))
        {
            var target = _choiceIndex;
            CloseChoice();
            NavigateAddChoice(target);
            return true;
        }
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View) || b.HasFlag(GamepadButtons.X))
        { CloseChoice(); return true; }
        return true; // 遮罩打开时吞掉其余按键，避免误触发主页操作
    }

    private void UpdateChoiceCursor()
    {
        var white = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ChoiceCustom.BorderBrush = _choiceIndex == 0 ? white : clear;
        ChoiceWidget.BorderBrush = _choiceIndex == 1 ? white : clear;
    }

    private void NavigateAddChoice(int index)
    {
        if (index == 1) App.Main?.RootNavigateWidget();
        else App.Main?.RootNavigateAddItem();
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
            if (i == _selected)
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
        if (_selected >= 0 && _selected < _tiles.Count)
            TileScroll.ChangeView(null,
                Math.Max(0, Canvas.GetTop(_tiles[_selected]) - 40), null, false);
        UpdateHintForSelection();
    }

    /// <summary>按当前选中项替换底部提示条：音乐/图片组磁贴=只显示对应操作提示（主页提示暂时隐藏），其他=恢复主页提示（编辑模式不处理，由 UpdateHintBar 负责）</summary>
    private void UpdateHintForSelection()
    {
        if (_moving) return;
        if (IsMusicSelected())
            HintBar.SetHints("DPAD:切换选择|A:播放/暂停|LB:上一曲|RB:下一曲");
        else if (IsPhotoSelected())
            HintBar.SetHints("DPAD:切换选择|A:切换|LB:上一张|RB:下一张");
        else if (IsClosableSelected())
            HintBar.SetHints("DPAD:选择|A:启动|Y:关闭|X:添加|R3:编辑|L3:排列|VIEW:设置|B:隐藏");
        else
            HintBar.SetHints("DPAD:选择|A:启动|X:添加|R3:编辑|L3:排列|VIEW:设置|B:隐藏");
    }

    /// <summary>选中磁贴是否可 Y 关闭（程序/网站/文件夹=可；组/小组件=不可）</summary>
    private bool IsClosableSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return false;
        var (item, group) = _display[_selected];
        return group is null && item.Kind is LaunchKind.App or LaunchKind.Url or LaunchKind.Folder;
    }

    /// <summary>方向选择：按磁贴中心坐标找指定方向最近邻</summary>
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

    /// <summary>移动模式（已抓取）：把抓取中的磁贴与指定方向最近邻磁贴交换位置</summary>
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

    /// <summary>Y=关闭：结束选中磁贴对应的运行中程序；未运行则提示（小组件/组磁贴不响应）</summary>
    private void CloseSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        if (group is not null || item.Kind is LaunchKind.Widget or LaunchKind.Group) return;
        CloseItem(item);
    }

    /// <summary>关闭磁贴对应的前台窗口（Y 键与长按共用）：程序/网站/文件夹</summary>
    private void CloseItem(LaunchItem item)
    {
        var killed = LaunchService.Close(item);
        ShowToast(killed > 0 ? $"已关闭 {item.Name}" : $"{item.Name} 未在运行");
        if (killed > 0) App.Main?.KeepForeground(); // 目标关闭后启动器保持前台不最小化
    }

    private void PersistLayout()
    {
        ConfigService.Save(_items.ToList());
    }

    /// <summary>LB/RB 翻页</summary>
    public void PageScroll(bool forward)
    {
        TileScroll.ChangeView(null,
            TileScroll.VerticalOffset + (forward ? 1 : -1) * (TileScroll.ViewportHeight - 40), null);
    }

    public void FocusGrid()
    {
        if (_tiles.Count > 0) UpdateSelection();
    }
}
