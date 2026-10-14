using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    private LaunchItem? _expanded;
    private bool _groupAnimating;
    private int _selected;
    /// <summary>已加载的磁贴集 Target：变化=打开的是另一个磁贴集（重置选中），不变=返回本页（保持选中）</summary>
    private string? _loadedTarget;

    // ---- 排列（L3 移动）模式状态，与主页同款 ----
    private bool _moving;
    private readonly List<LaunchItem> _marked = [];
    private LaunchItem? _grabbed;

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
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SystemMediaService.StateChanged -= OnMusicStateChanged;
        SlideshowService.CurrentChanged -= OnSlideshowChanged;
    }

    private void OnMusicStateChanged() => DispatcherQueue.TryEnqueue(RefreshMusicTiles);
    private void OnSlideshowChanged() => DispatcherQueue.TryEnqueue(RefreshPhotoTiles);

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
            musicTile.Tapped += (_, _) => { _selected = index; UpdateSelection(); };
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
            photoTile.Tapped += (_, _) =>
            {
                _selected = index;
                UpdateSelection();
                SlideshowService.Skip(folder, 1);
            };
            return photoTile;
        }

        // 标准磁贴（与主页共用构建）
        var tile = TileStyle.BuildStandardTile(item, group, width, height, accent, noColor);
        tile.Tag = index;
        tile.Tapped += (_, _) => { _selected = index; UpdateSelection(); ActivateAt(index); };
        return tile;
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

    /// <summary>Y=关闭：结束选中磁贴对应的运行中程序；未运行则提示（小组件/组磁贴不响应）</summary>
    private void CloseSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        if (group is not null || item.Kind is LaunchKind.Widget or LaunchKind.Group) return;
        var killed = LaunchService.Close(item);
        ShowToast(killed > 0 ? $"已关闭 {item.Name}" : $"{item.Name} 未在运行");
        if (killed > 0) App.Main?.KeepForeground(); // 目标关闭后启动器保持前台不最小化
    }

    /// <summary>临时提示条：2.5 秒后自动隐藏（关闭反馈等复用）</summary>
    private Microsoft.UI.Xaml.DispatcherTimer? _toastTimer;

    private void ShowToast(string text)
    {
        MoveHintText.Text = text;
        MoveHintBar.Visibility = Visibility.Visible;
        if (_toastTimer == null)
        {
            _toastTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                MoveHintBar.Visibility = Visibility.Collapsed;
            };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
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

    /// <summary>按当前选中项替换底部提示条（与主页同款）：音乐/图片组磁贴=只显示对应操作提示，其他=恢复副本页常规提示（排列模式不处理，由 UpdateHintBar 负责）</summary>
    private void UpdateHintForSelection()
    {
        if (_moving) return;
        if (IsMusicSelected())
            HintBar.SetHints("DPAD:切换选择|A:播放/暂停|LB:上一曲|RB:下一曲");
        else if (IsPhotoSelected())
            HintBar.SetHints("DPAD:切换选择|A:切换|LB:上一张|RB:下一张");
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
        // 常规模式 Y=关闭：结束选中磁贴对应的运行中程序（排列模式内 Y 仍为删除）
        if (buttons.HasFlag(GamepadButtons.Y)) { CloseSelected(); return true; }
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
            // 进入排列模式：标题替换为"编辑模式"，底部提示条换成排列操作提示
            _moving = true;
            SetTitle.Visibility = Visibility.Collapsed;
            EditModeText.Visibility = Visibility.Visible;
            UpdateHintBar();
            LayoutTiles();
            UpdateSelection();
            return true;
        }
        return false;
    }

    private void EndMove()
    {
        _moving = false;
        _marked.Clear();
        _grabbed = null;
        // 恢复标题与常规提示条
        EditModeText.Visibility = Visibility.Collapsed;
        SetTitle.Visibility = Visibility.Visible;
        HintBar.SetHints(NormalHints);
        PersistSnapshot();
        LayoutTiles();
        UpdateSelection();
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

    /// <summary>排列模式 X 键：标记/取消标记当前顶层磁贴（已标记青色描边）</summary>
    private void ToggleMark()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        if (group is not null || item.Kind == LaunchKind.Group) return;
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

    /// <summary>删除当前选中磁贴：组=拆分组；成员=移出组；普通=删除</summary>
    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _display.Count) return;
        var (item, group) = _display[_selected];
        if (group is not null)
        {
            group.Members.Remove(item);
            if (group.Members.Count < 2)
            {
                var gi = _items.IndexOf(group);
                _items.Remove(group);
                if (ReferenceEquals(_expanded, group)) _expanded = null;
                foreach (var m in group.Members) _items.Insert(Math.Min(gi + 1, _items.Count), m);
                group.Members.Clear();
            }
        }
        else if (item.Kind == LaunchKind.Group)
        {
            var gi = _items.IndexOf(item);
            _items.Remove(item);
            if (ReferenceEquals(_expanded, item)) _expanded = null;
            for (var k = 0; k < item.Members.Count; k++)
                _items.Insert(Math.Min(gi + k, _items.Count), item.Members[k]);
            item.Members.Clear();
        }
        else
        {
            _marked.Remove(item);
            if (ReferenceEquals(_grabbed, item)) _grabbed = null;
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
