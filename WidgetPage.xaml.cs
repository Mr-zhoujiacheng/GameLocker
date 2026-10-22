using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 小组件页：列出可添加的小组件（音乐控制器 / 图片组 / 天气 / 映射 / 修改器 / 游戏存档 / 手柄映射 / OptiScaler / 回收站 / 前台应用），磁贴预览完全复用主页磁贴样式。
/// 手柄：方向=移动光标，A/X=添加到主页，B/View=返回主页。
/// </summary>
public sealed partial class WidgetPage : Page
{
    private Control[] _formControls = [];
    private int _formIndex;

    /// <summary>预览用固定音乐组件项（Target 稳定哈希配色，与主页添加后的磁贴一致；固定 2×2 大磁贴）</summary>
    private static readonly LaunchItem PreviewItem = new()
    {
        Name = "音乐控制器",
        Target = "widget:music",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Music,
        Size = TileSize.Large
    };

    /// <summary>预览用固定图片组项（未定向文件夹，磁贴显示占位提示）</summary>
    private static readonly LaunchItem PhotoPreviewItem = new()
    {
        Name = "图片组",
        Target = "widget:photos",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Photos,
        Size = TileSize.Large
    };

    /// <summary>预览用固定天气项（2×2 大磁贴展示全部字段；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem WeatherPreviewItem = new()
    {
        Name = "天气",
        Target = "widget:weather",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Weather,
        Size = TileSize.Large
    };

    /// <summary>预览用固定映射项（无自定义标题 → 显示映射按键名占位；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem MappingPreviewItem = new()
    {
        Name = "",
        Target = "widget:mapping",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Mapping,
        Size = TileSize.Large
    };

    /// <summary>预览用固定修改器项（扳手图标 + 名称，无实时状态；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem TrainerPreviewItem = new()
    {
        Name = "修改器",
        Target = "widget:trainer",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Trainer,
        Size = TileSize.Large
    };

    /// <summary>预览用固定游戏存档项（保存图标 + 名称，无实时状态；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem BackupPreviewItem = new()
    {
        Name = "游戏存档",
        Target = "widget:backup",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Backup,
        Size = TileSize.Large
    };

    /// <summary>预览用固定手柄映射项（手柄图标 + 开关状态，无实时状态；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem GamepadMapPreviewItem = new()
    {
        Name = "手柄映射",
        Target = "widget:gamepadmap",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.GamepadMap,
        Size = TileSize.Large
    };

    /// <summary>预览用固定 OptiScaler 项（图像图标 + 名称，无实时状态；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem OptiScalerPreviewItem = new()
    {
        Name = "OptiScaler",
        Target = "widget:optiscaler",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.OptiScaler,
        Size = TileSize.Large
    };

    /// <summary>预览用固定回收站项（垃圾桶图标 + 名称，无实时状态；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem RecycleBinPreviewItem = new()
    {
        Name = "回收站",
        Target = "widget:recyclebin",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.RecycleBin,
        Size = TileSize.Large
    };

    /// <summary>预览用固定前台应用项（显示当前前台应用的图标/进程名/状态；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem ForegroundPreviewItem = new()
    {
        Name = "前台应用",
        Target = "widget:foreground",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Foreground,
        Size = TileSize.Large
    };

    /// <summary>预览用固定系统项（默认显示"Win 设置"；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem SystemPreviewItem = new()
    {
        Name = "系统",
        Target = "widget:system",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.System,
        Size = TileSize.Wide
    };

    /// <summary>预览用固定性能监控项（状态点 + 已停止；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem MonitorPreviewItem = new()
    {
        Name = "性能监控",
        Target = "widget:monitor",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Monitor,
        Size = TileSize.Wide
    };

    /// <summary>预览用固定文件传输项（发送图标 + 名称；实际添加时为默认"长"尺寸）</summary>
    private static readonly LaunchItem FileTransferPreviewItem = new()
    {
        Name = "文件传输",
        Target = "widget:filetransfer",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.FileTransfer,
        Size = TileSize.Wide
    };

    /// <summary>预览磁贴框架（订阅天气变化时刷新文字）</summary>
    private TileStyle.WeatherTileRefs? _weatherPreviewRefs;

    public WidgetPage()
    {
        InitializeComponent();
        _formControls = [MusicWidgetCard, PhotoWidgetCard, WeatherWidgetCard, MappingWidgetCard, TrainerWidgetCard, BackupWidgetCard, GamepadMapWidgetCard, OptiScalerWidgetCard, RecycleBinWidgetCard, ForegroundWidgetCard, SystemWidgetCard, MonitorWidgetCard, FileTransferWidgetCard];
        // 磁贴式自适应：窗口宽度变化（侧边栏 ↔ 全屏/居中）时按可用宽度重排列数
        FormScroll.SizeChanged += (_, _) => LayoutCards();
        Loaded += (_, _) => LayoutCards();
    }

    // ---- 卡片排布（仿主页 LayoutTiles：卡片尺寸固定，列数随可用宽度变，整块居中）----

    /// <summary>卡片宽/高与间距：宽度取侧边栏下 3 列等宽时的值（约 142），窗口变宽就多排一列，卡片本身不变大</summary>
    private const double CardW = 142;
    private const double CardH = 204;
    private const double CardGap = 12;

    private void LayoutCards()
    {
        if (FormPanel is null || FormScroll is null) return;
        var avail = FormScroll.ViewportWidth;   // 已扣除 ScrollViewer 的 Padding
        if (avail <= 0) return;

        var pitch = CardW + CardGap;
        var cols = Math.Max(1, (int)((avail + CardGap) / pitch));
        var offsetX = Math.Max(0, (avail - (cols * pitch - CardGap)) / 2);

        double bottom = 0;
        for (var i = 0; i < _formControls.Length; i++)
        {
            var c = _formControls[i];
            var y = (double)(i / cols) * (CardH + CardGap);
            c.Width = CardW;
            c.Height = CardH;
            Canvas.SetLeft(c, offsetX + i % cols * pitch);
            Canvas.SetTop(c, y);
            bottom = Math.Max(bottom, y + CardH);
        }
        // Canvas 需要显式尺寸，纵向滚动条才能算对可滚高度
        FormPanel.Width = avail;
        FormPanel.Height = bottom;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 磁贴预览：与主页同一套样式构建（按钮传 null → 预览中禁用；图片组固定居中裁切）
        var (w, h) = (2 * TileStyle.Unit + TileStyle.Gap, 2 * TileStyle.Unit + TileStyle.Gap);
        TilePreviewHost.Content = TileStyle.BuildMusicTile(
            PreviewItem, w, h, TileStyle.AccentFor(PreviewItem), TileStyle.IsNoColor(PreviewItem),
            null, null, null).Tile;
        PhotoPreviewHost.Content = TileStyle.BuildPhotoTile(
            PhotoPreviewItem, w, h, null, "图片组\n按 R3 选择图片文件夹").Tile;
        // 天气预览：与主页同一构建 + 同一填充，数据变化时实时更新预览文字
        var (weatherTile, weatherRefs) = TileStyle.BuildWeatherTile(
            WeatherPreviewItem, w, h, TileStyle.AccentFor(WeatherPreviewItem),
            TileStyle.IsNoColor(WeatherPreviewItem));
        TileStyle.FillWeatherTile(weatherRefs);
        WeatherPreviewHost.Content = weatherTile;
        _weatherPreviewRefs = weatherRefs;
        // 映射预览：与主页同一构建（未设置按键 → 显示占位文案）
        MappingPreviewHost.Content = TileStyle.BuildMappingTile(
            MappingPreviewItem, w, h, TileStyle.AccentFor(MappingPreviewItem),
            TileStyle.IsNoColor(MappingPreviewItem));
        // 修改器预览：与主页同一构建（扳手图标 + 名称）
        TrainerPreviewHost.Content = TileStyle.BuildTrainerTile(
            TrainerPreviewItem, w, h, TileStyle.AccentFor(TrainerPreviewItem),
            TileStyle.IsNoColor(TrainerPreviewItem));
        // 游戏存档预览：与主页同一构建（保存图标 + 名称）
        BackupPreviewHost.Content = TileStyle.BuildBackupTile(
            BackupPreviewItem, w, h, TileStyle.AccentFor(BackupPreviewItem),
            TileStyle.IsNoColor(BackupPreviewItem));
        // 手柄映射预览：与主页同一构建（手柄图标 + 开关状态，预览显示"映射关"）
        var (gamepadMapTile, gamepadMapRefs) = TileStyle.BuildGamepadMapTile(
            GamepadMapPreviewItem, w, h, TileStyle.AccentFor(GamepadMapPreviewItem),
            TileStyle.IsNoColor(GamepadMapPreviewItem));
        TileStyle.FillGamepadMapState(gamepadMapRefs, GamepadMapPreviewItem);
        GamepadMapPreviewHost.Content = gamepadMapTile;
        // OptiScaler 预览：与主页同一构建（图像图标 + 名称）
        OptiScalerPreviewHost.Content = TileStyle.BuildOptiScalerTile(
            OptiScalerPreviewItem, w, h, TileStyle.AccentFor(OptiScalerPreviewItem),
            TileStyle.IsNoColor(OptiScalerPreviewItem));
        // 回收站预览：与主页同一构建（标准磁贴：垃圾桶图标 + 名称）
        RecycleBinPreviewHost.Content = TileStyle.BuildStandardTile(
            RecycleBinPreviewItem, null, w, h, TileStyle.AccentFor(RecycleBinPreviewItem),
            TileStyle.IsNoColor(RecycleBinPreviewItem));
        // 前台应用预览：与主页同一构建（应用图标 + 进程名 + 状态，实时取当前前台应用）
        var (fgTile, fgRefs) = TileStyle.BuildForegroundTile(
            ForegroundPreviewItem, w, h, TileStyle.AccentFor(ForegroundPreviewItem),
            TileStyle.IsNoColor(ForegroundPreviewItem));
        TileStyle.FillForegroundTile(fgRefs);
        ForegroundPreviewHost.Content = fgTile;
        // 系统预览：与主页同一构建（图标 + 面板项名称 + 读数）
        var (sysTile, sysRefs) = TileStyle.BuildSystemTile(
            SystemPreviewItem, w, h, TileStyle.AccentFor(SystemPreviewItem),
            TileStyle.IsNoColor(SystemPreviewItem));
        TileStyle.FillSystemTile(sysRefs, SystemPreviewItem);
        SystemPreviewHost.Content = sysTile;
        // 性能监控预览：与主页同一构建（状态点 + 运行状态，预览显示"已停止"）
        var (monTile, monRefs) = TileStyle.BuildMonitorTile(
            MonitorPreviewItem, w, h, TileStyle.AccentFor(MonitorPreviewItem),
            TileStyle.IsNoColor(MonitorPreviewItem));
        TileStyle.FillMonitorState(monRefs, MonitorPreviewItem);
        MonitorPreviewHost.Content = monTile;
        // 文件传输预览：标准磁贴外观（发送图标 + 名称）
        FileTransferPreviewHost.Content = TileStyle.BuildStandardTile(
            FileTransferPreviewItem, null, w, h, TileStyle.AccentFor(FileTransferPreviewItem),
            TileStyle.IsNoColor(FileTransferPreviewItem));
        WeatherService.StateChanged += OnWeatherChanged;
        WeatherService.EnsureFresh();

        _formIndex = 0;
        FocusForm(0);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        WeatherService.StateChanged -= OnWeatherChanged;
    }

    private void OnWeatherChanged() =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_weatherPreviewRefs is not null) TileStyle.FillWeatherTile(_weatherPreviewRefs);
        });

    // ---- 手柄 ----

    public void HandleGamepad(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.DPadUp)) MoveRow(-1);
        else if (b.HasFlag(GamepadButtons.DPadDown)) MoveRow(1);
        else if (b.HasFlag(GamepadButtons.DPadLeft)) MoveCol(-1);
        else if (b.HasFlag(GamepadButtons.DPadRight)) MoveCol(1);

        if (b.HasFlag(GamepadButtons.A) || b.HasFlag(GamepadButtons.X)) ActivateForm();
        else if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View))
            App.Main?.ShowMainPage();
    }

    private void ActivateForm()
    {
        var c = _formControls[_formIndex];
        if (ReferenceEquals(c, PhotoWidgetCard)) AddPhotoWidget();
        else if (ReferenceEquals(c, WeatherWidgetCard)) AddWeatherWidget();
        else if (ReferenceEquals(c, MappingWidgetCard)) AddMappingWidget();
        else if (ReferenceEquals(c, TrainerWidgetCard)) AddTrainerWidget();
        else if (ReferenceEquals(c, BackupWidgetCard)) AddBackupWidget();
        else if (ReferenceEquals(c, GamepadMapWidgetCard)) AddGamepadMapWidget();
        else if (ReferenceEquals(c, OptiScalerWidgetCard)) AddOptiScalerWidget();
        else if (ReferenceEquals(c, RecycleBinWidgetCard)) AddRecycleBinWidget();
        else if (ReferenceEquals(c, ForegroundWidgetCard)) AddForegroundWidget();
        else if (ReferenceEquals(c, SystemWidgetCard)) AddSystemWidget();
        else if (ReferenceEquals(c, MonitorWidgetCard)) AddMonitorWidget();
        else if (ReferenceEquals(c, FileTransferWidgetCard)) AddFileTransferWidget();
        else AddMusicWidget();
    }

    // 行感知导航（与 AddItemPage 同一套模式）
    private readonly List<List<(int idx, double x, double y)>> _rows = [];

    private void BuildRows()
    {
        _rows.Clear();
        var pts = new List<(int idx, double x, double y)>();
        for (var i = 0; i < _formControls.Length; i++)
        {
            if (_formControls[i].Visibility != Visibility.Visible) continue;
            if (!TryCenter(_formControls[i], out var x, out var y)) continue;
            pts.Add((i, x, y));
        }
        pts.Sort((a, b) => a.y.CompareTo(b.y));
        const double tol = 40;
        for (var k = 0; k < pts.Count;)
        {
            var rowY = pts[k].y;
            var row = new List<(int idx, double x, double y)>();
            while (k < pts.Count && pts[k].y - rowY <= tol) row.Add(pts[k++]);
            row.Sort((a, b) => a.x.CompareTo(b.x));
            _rows.Add(row);
        }
    }

    private (int row, int col) LocateCurrent()
    {
        for (var r = 0; r < _rows.Count; r++)
            for (var c = 0; c < _rows[r].Count; c++)
                if (_rows[r][c].idx == _formIndex) return (r, c);
        return (-1, -1);
    }

    private void MoveRow(int delta)
    {
        BuildRows();
        var (ri, ci) = LocateCurrent();
        if (ri < 0) { MoveForm(delta); return; }
        var target = ri + delta;
        if (target < 0 || target >= _rows.Count) return;
        var row = _rows[target];
        var curX = _rows[ri][ci].x;
        var best = row[0];
        var bd = Math.Abs(row[0].x - curX);
        foreach (var p in row)
        {
            var d = Math.Abs(p.x - curX);
            if (d < bd) { bd = d; best = p; }
        }
        _formIndex = best.idx;
        FocusForm(best.idx);
    }

    private void MoveCol(int delta)
    {
        BuildRows();
        var (ri, ci) = LocateCurrent();
        if (ri < 0) { MoveForm(delta); return; }
        var row = _rows[ri];
        var nc = ci + delta;
        if (nc < 0 || nc >= row.Count) return;
        _formIndex = row[nc].idx;
        FocusForm(row[nc].idx);
    }

    private void MoveForm(int delta)
    {
        var next = Math.Clamp(_formIndex + delta, 0, _formControls.Length - 1);
        if (next == _formIndex) return;
        _formIndex = next;
        FocusForm(next);
    }

    private bool TryCenter(Control c, out double x, out double y)
    {
        x = y = 0;
        if (c.ActualWidth <= 0 || c.ActualHeight <= 0) return false;
        try
        {
            var r = c.TransformToVisual(FormPanel)
                      .TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
            x = r.X + r.Width / 2;
            y = r.Y + r.Height / 2;
            return true;
        }
        catch { return false; }
    }

    private void FocusForm(int i)
    {
        var c = _formControls[i];
        UpdateCursor(c);
        // RadioButton：只移动光标高亮，不聚焦——WinUI 键盘聚焦会自动勾选，
        // 导致"移动即变更"；按 A 才真正切换选中（与 AddItemPage 同模式）
        if (c is not RadioButton)
            c.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        EnsureVisible(c);
    }

    /// <summary>高亮光标所在控件（描边）</summary>
    private void UpdateCursor(Control? cursor)
    {
        foreach (var ctl in _formControls)
            ctl.BorderBrush = ReferenceEquals(ctl, cursor)
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void EnsureVisible(Control c)
    {
        // 三列等宽布局：光标控件超出可视区时自动垂直滚入
        try
        {
            var p = c.TransformToVisual(FormScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            var vh = FormScroll.ViewportHeight;
            if (p.Y < 0)
                FormScroll.ChangeView(null, FormScroll.VerticalOffset + p.Y - 8, null);
            else if (p.Y + c.ActualHeight > vh)
                FormScroll.ChangeView(null, FormScroll.VerticalOffset + p.Y + c.ActualHeight - vh + 8, null);
        }
        catch { }
    }

    // ---- 添加到主页 ----

    /// <summary>新建小组件磁贴落盘并回主页；若存在"放进抽屉"的一次性请求（抽屉区域内按 X 添加），
    /// 则直接放进该抽屉而不是主页顶层。返回 false 表示被拒（抽屉放不下等），此时不落盘、留在本页提示。</summary>
    private bool CommitWidget(List<LaunchItem> items, LaunchItem it)
    {
        var req = App.Main?.PendingDrawerInsert;
        if (req is not null)
        {
            App.Main!.PendingDrawerInsert = null;   // 一次性请求，消费即清
            if (!DrawerService.TryInsert(items, req, it, out var error))
            {
                PageTitle.Text = error ?? "抽屉放不下这个磁贴";
                return false;
            }
        }
        else
        {
            items.Add(it);
        }
        ConfigService.Save(items);
        App.Main?.ShowMainPage();
        return true;
    }

    /// <summary>是否已存在该类小组件（顶层 + 抽屉内）——只允许一个的组件用它做守卫</summary>
    private static bool ExistsWidget(List<LaunchItem> items, WidgetKind kind)
        => DrawerService.Anywhere(items, it => it.Kind == LaunchKind.Widget && it.WidgetKind == kind);

    private void AddMusicWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.Music)) { PageTitle.Text = "音乐控制器已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "音乐控制器",
            Target = "widget:music",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Music,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Large
        });
    }

    private void AddPhotoWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.Photos)) { PageTitle.Text = "图片组已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "图片组",
            Target = "widget:photos",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Photos,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Large
        });
    }

    /// <summary>天气组件：默认"长"尺寸（可后续编辑大小与颜色），只允许一个（避免多个显示同一份天气）</summary>
    private void AddWeatherWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.Weather)) { PageTitle.Text = "天气已在主页"; return; }
        if (!CommitWidget(items, new LaunchItem
        {
            Name = "天气",
            Target = "widget:weather",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Weather,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        })) return;
        WeatherService.EnsureFresh();
    }

    /// <summary>映射小组件：默认"长"尺寸（可后续编辑大小、颜色与映射按键），可添加多个（各映射不同按键）。
    /// 名称留空：未设自定义标题时磁贴显示映射的按键名。</summary>
    private void AddMappingWidget()
    {
        var items = ConfigService.Load();
        CommitWidget(items, new LaunchItem
        {
            Name = "",
            Target = "widget:mapping:" + Guid.NewGuid().ToString("N"),
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Mapping,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    /// <summary>修改器组件：默认"长"尺寸（可后续编辑大小与颜色），只允许一个——
    /// 其行为始终是"按当前游戏进程取最新修改器"，多份没有区别。</summary>
    private void AddTrainerWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.Trainer)) { PageTitle.Text = "修改器已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "修改器",
            Target = "widget:trainer",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Trainer,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    /// <summary>游戏存档组件：默认"长"尺寸（可后续编辑大小、颜色、备份目标与保留份数），只允许一个——
    /// 其行为始终是"备份当前正在玩的那款游戏的存档"（Y 键/长按=同步），多份没有区别。</summary>
    private void AddBackupWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.Backup)) { PageTitle.Text = "游戏存档已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "游戏存档",
            Target = "widget:backup",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Backup,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    /// <summary>手柄映射组件：默认"长"尺寸（可后续编辑大小、颜色与映射列表），可添加多个
    /// （各承载不同的映射集合）。设置好映射后，A/点击=开关映射，长按=关闭映射。</summary>
    private void AddGamepadMapWidget()
    {
        var items = ConfigService.Load();
        CommitWidget(items, new LaunchItem
        {
            Name = "手柄映射",
            Target = "widget:gamepadmap:" + Guid.NewGuid().ToString("N"),
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.GamepadMap,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    /// <summary>OptiScaler 组件：默认"长"尺寸（可后续编辑大小、颜色、注入 DLL 与 ini 关键项），只允许一个——
    /// 其行为始终是"检测当前正在玩的游戏并注入 OptiScaler"，多份没有区别。</summary>
    private void AddOptiScalerWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.OptiScaler)) { PageTitle.Text = "OptiScaler 已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "OptiScaler",
            Target = "widget:optiscaler",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.OptiScaler,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    /// <summary>回收站组件：默认"长"尺寸（可后续编辑大小与颜色），只允许一个——
    /// 其行为始终是"打开系统回收站 / 一键清空回收站"，多份没有区别。</summary>
    private void AddRecycleBinWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.RecycleBin)) { PageTitle.Text = "回收站已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "回收站",
            Target = "widget:recyclebin",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.RecycleBin,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    /// <summary>前台应用监测组件：默认"长"尺寸（可后续编辑大小与颜色），只允许一个——
    /// 其行为始终是"监测当前前台应用并冻结/结束它"，多份没有区别。</summary>
    private void AddForegroundWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.Foreground)) { PageTitle.Text = "前台应用已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "前台应用",
            Target = "widget:foreground",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Foreground,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    /// <summary>系统组件：默认"长"尺寸（可后续编辑大小与颜色），只允许一个——
    /// 一个磁贴即可收纳全部系统入口与开关，多份没有意义（右摇杆←→切换面板项）。</summary>
    private void AddSystemWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.System)) { PageTitle.Text = "系统已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "系统",
            Target = "widget:system",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.System,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    /// <summary>性能监控组件：默认"长"尺寸（可后续编辑大小与颜色），只允许一个——
    /// 一个置顶监控条即可覆盖全部指标，多份只会重复显示。</summary>
    private void AddMonitorWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.Monitor)) { PageTitle.Text = "性能监控已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "性能监控",
            Target = "widget:monitor",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Monitor,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    /// <summary>文件传输组件：默认"长"尺寸（可后续编辑大小与颜色）；一个浮层即可发起/接收，只允许一个</summary>
    private void AddFileTransferWidget()
    {
        var items = ConfigService.Load();
        if (ExistsWidget(items, WidgetKind.FileTransfer)) { PageTitle.Text = "文件传输已在主页"; return; }
        CommitWidget(items, new LaunchItem
        {
            Name = "文件传输",
            Target = "widget:filetransfer",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.FileTransfer,
            PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新磁贴归到主页当前所在页
            Size = TileSize.Wide
        });
    }

    // ---- 鼠标 ----

    private void OnBackClick(object sender, RoutedEventArgs e) => App.Main?.ShowMainPage();

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: "photo" }) AddPhotoWidget();
        else if (sender is Button { Tag: "weather" }) AddWeatherWidget();
        else if (sender is Button { Tag: "mapping" }) AddMappingWidget();
        else if (sender is Button { Tag: "trainer" }) AddTrainerWidget();
        else if (sender is Button { Tag: "backup" }) AddBackupWidget();
        else if (sender is Button { Tag: "gamepadmap" }) AddGamepadMapWidget();
        else if (sender is Button { Tag: "optiscaler" }) AddOptiScalerWidget();
        else if (sender is Button { Tag: "recyclebin" }) AddRecycleBinWidget();
        else if (sender is Button { Tag: "foreground" }) AddForegroundWidget();
        else if (sender is Button { Tag: "system" }) AddSystemWidget();
        else if (sender is Button { Tag: "monitor" }) AddMonitorWidget();
        else if (sender is Button { Tag: "filetransfer" }) AddFileTransferWidget();
        else AddMusicWidget();
    }
}
