using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>磁贴主题下拉项：名称加粗，后面跟普通小字说明</summary>
public sealed record TileThemeOption(TileTheme Theme, string Name, string Desc);

/// <summary>
/// 外观设置：窗口背景颜色 + 窗口透明度。
/// 手柄：方向=移动焦点，A=选中颜色/按下 −/+ 调整，View(Select)/B=返回设置主页。
/// </summary>
public sealed partial class AppearancePage : Page, ISettingsGamepad
{
    private List<FrameworkElement> _controls = [];
    private int _index;
    private bool _ready;
    private double _opacity = 1.0;
    private double _spacing = 10;
    private double _tileOpacity = 1.0;
    private double _widthPct = 0.38;

    /// <summary>磁贴主题可选项（名称 + 说明 + 迷你预览条）</summary>
    private static readonly TileThemeOption[] ThemeOptions =
    [
        new(TileTheme.Colorful, "彩色磁贴", "按颜色 / 随机配色"),
        new(TileTheme.Win10, "简约磁贴", "无颜色 · 深灰"),
        new(TileTheme.Gradient, "渐变磁贴", "斜向亮暗渐变"),
        new(TileTheme.Glass, "玻璃磁贴", "磨砂底 + 底部色条"),
        new(TileTheme.Transparent, "透明磁贴", "无底色 · 不选颜色"),
    ];

    /// <summary>主题预览用的示例颜色（固定，避免预览随磁贴随机配色变化）</summary>
    private static readonly Windows.UI.Color PreviewAccent =
        Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);

    /// <summary>展开列表中的选项行（光标高亮 + 选中标记用）</summary>
    private readonly Dictionary<TileTheme, Border> _themeOptionBorders = [];
    private readonly Dictionary<TileTheme, FontIcon> _themeChecks = [];
    private readonly List<Border> _themeRows = [];
    private bool _themeExpanded;
    private TileTheme _currentTheme = TileTheme.Colorful;

    /// <summary>预设色：色值 → 单选控件</summary>
    private readonly (string Hex, RadioButton Radio)[] Palette;

    public AppearancePage()
    {
        InitializeComponent();

        Palette =
        [
            ("#202020", DarkGrayRadio),
            ("#000000", BlackRadio),
            ("#102A43", BlueRadio),
            ("#2D1B4E", PurpleRadio),
            ("#1B3A2A", GreenRadio),
            ("#3E2723", BrownRadio),
        ];

        var s = SettingsService.Current;
        GlassToggle.IsOn = s.GlassEffect;
        FillTileThemes(s.TileTheme);
        TileCornerToggle.IsOn = TileStyle.IsSquareRadius;
        TileNameToggle.IsOn = s.ShowTileName;
        GroupAnimToggle.IsOn = s.GroupExpandAnimation;
        RandomColorToggle.IsOn = s.RandomTileColor;
        CheckColor(s.BackgroundColor);
        _opacity = Math.Clamp(s.WindowOpacity, 0.3, 1.0);
        OpacityText.Text = $"{(int)Math.Round(_opacity * 100)}%";
        _widthPct = Math.Clamp(s.SidebarWidthPercent, 0.38, 0.5);
        WidthText.Text = $"{(int)Math.Round(_widthPct * 100)}%";
        _spacing = Math.Clamp(s.TileSpacing, 0, 30);
        SpacingText.Text = $"{(int)Math.Round(_spacing)}px";
        _tileOpacity = Math.Clamp(s.TileOpacity, 0.3, 1.0);
        TileOpacityText.Text = $"{(int)Math.Round(_tileOpacity * 100)}%";

        SyncControls();
    }

    /// <summary>按展开状态重建可导航控件列表：折叠时移除选项行，
    /// 避免光标落到已隐藏的行上（ActualHeight 残留会让 BuildRows 仍把它们算成一行）</summary>
    private void SyncControls()
    {
        var controls = new List<FrameworkElement>
        {
            GlassToggle,
            DarkGrayRadio, BlackRadio, BlueRadio, PurpleRadio, GreenRadio, BrownRadio,
            OpacityDownButton, OpacityUpButton,
            WidthDownButton, WidthUpButton,
            TileThemeCard
        };
        if (_themeExpanded) controls.AddRange(_themeRows);
        controls.AddRange([TileCornerToggle, TileNameToggle, GroupAnimToggle, RandomColorToggle,
            TileOpacityDownButton, TileOpacityUpButton, SpacingDownButton, SpacingUpButton]);
        _controls = controls;
    }

    /// <summary>色值 → 勾选对应单选（避免 SelectionChanged 递归）</summary>
    private void CheckColor(string hex)
    {
        var matched = false;
        foreach (var (value, radio) in Palette)
        {
            var on = value == hex;
            radio.IsChecked = on;
            matched |= on;
        }
        if (!matched) DarkGrayRadio.IsChecked = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        _index = 0;
        FocusIndex(0);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _ready = false;
        // 离开页面时收起主题列表（不移动光标，避免触发滚动）
        CollapseThemeList(false);
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready) return;
        _consumedBack = false;

        // 主题列表展开中：B/Select 只收起列表，不返回设置主页
        if (_themeExpanded && (buttons.HasFlag(GamepadButtons.B) || buttons.HasFlag(GamepadButtons.View)))
        {
            CollapseThemeList(true);
            _consumedBack = true;
            return;
        }

        // 行感知导航：开关为整行单控件、−/＋ 为左右两控件、颜色为多列，
        // 上下在相邻行之间移动（取最接近当前列的），左右在同一行内移动。
        if (buttons.HasFlag(GamepadButtons.DPadUp)) MoveRow(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown)) MoveRow(1);
        else if (buttons.HasFlag(GamepadButtons.DPadLeft)) MoveCol(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadRight)) MoveCol(1);
        else if (buttons.HasFlag(GamepadButtons.A))
            ActivateCurrent();
    }

    private bool _consumedBack;

    /// <summary>是否已消费本次 B（主题列表展开时只收起列表，不返回设置主页）</summary>
    public bool ConsumedBack => _consumedBack;

    /// <summary>展开/收起卡片内的主题列表（不使用弹层）</summary>
    private void ToggleThemeList()
    {
        if (_themeExpanded) CollapseThemeList(true);
        else ExpandThemeList();
    }

    private void ExpandThemeList()
    {
        _themeExpanded = true;
        TileThemeList.Visibility = Visibility.Visible;
        ThemeChevronRotate.Angle = 180;
        SyncControls();
        // 展开后光标直接落在当前选中的主题上：选中框始终可见，也方便直接按 A 确认
        if (_ready && _themeOptionBorders.TryGetValue(_currentTheme, out var selected))
        {
            _index = _controls.IndexOf(selected);
            if (_index >= 0) FocusIndex(_index);
        }
    }

    /// <summary>收起主题列表；restoreCursor=true 时把光标移回标题行（保证选中框不消失）</summary>
    private void CollapseThemeList(bool restoreCursor)
    {
        _themeExpanded = false;
        TileThemeList.Visibility = Visibility.Collapsed;
        ThemeChevronRotate.Angle = 0;
        SyncControls();
        // 无论光标此前在选项行还是已在标题行，收起后统一回到标题行并重绘选中框
        if (restoreCursor)
        {
            var header = _controls.IndexOf(TileThemeCard);
            if (header >= 0)
            {
                _index = header;
                FocusIndex(header);
            }
        }
        else if (_index >= _controls.Count)
        {
            _index = Math.Max(0, _controls.Count - 1); // 选项行已被移除，避免留下越界索引
        }
    }

    /// <summary>选择主题：立即保存、刷新选中标记与标题预览，并收起列表</summary>
    private void SelectTheme(TileTheme theme)
    {
        ApplyThemeSelection(theme);
        if (_ready) SettingsService.Update(s => s.TileTheme = theme);
        CollapseThemeList(true);
    }

    /// <summary>选项行 → 主题</summary>
    private bool TryGetTheme(Border row, out TileTheme theme)
    {
        foreach (var (t, b) in _themeOptionBorders)
        {
            if (!ReferenceEquals(b, row)) continue;
            theme = t;
            return true;
        }
        theme = default;
        return false;
    }

    // 按控件中心 Y 聚成的"行"，每行内按 X 排序（每次按键实时重建，滚动后坐标仍准确）
    private readonly List<List<(int idx, double x, double y)>> _rows = [];

    private void BuildRows()
    {
        _rows.Clear();
        var pts = new List<(int idx, double x, double y)>();
        for (var i = 0; i < _controls.Count; i++)
        {
            if (!TryCenter(_controls[i], out var x, out var y)) continue;
            pts.Add((i, x, y));
        }
        pts.Sort((a, b) => a.y.CompareTo(b.y));

        const double tol = 40; // 行间距 > 控件高+间隔，同行中心 Y 相同，40px 容差安全
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
                if (_rows[r][c].idx == _index) return (r, c);
        return (-1, -1);
    }

    /// <summary>上下：移动到相邻行，取 X 最接近当前列的控件；到顶/到底不移动</summary>
    private void MoveRow(int delta)
    {
        BuildRows();
        var (ri, ci) = LocateCurrent();
        if (ri < 0) { Move(delta); return; }
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
        _index = best.idx;
        FocusIndex(best.idx);
    }

    /// <summary>左右：同一行内移动；单控件行（开关）到头不移动</summary>
    private void MoveCol(int delta)
    {
        BuildRows();
        var (ri, ci) = LocateCurrent();
        if (ri < 0) { Move(delta); return; }
        var row = _rows[ri];
        var nc = ci + delta;
        if (nc < 0 || nc >= row.Count) return;
        _index = row[nc].idx;
        FocusIndex(row[nc].idx);
    }

    private bool TryCenter(FrameworkElement c, out double x, out double y)
    {
        x = y = 0;
        if (c.ActualWidth <= 0 || c.ActualHeight <= 0) return false;
        try
        {
            var r = c.TransformToVisual(ContentScroll)
                      .TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
            x = r.X + r.Width / 2;
            y = r.Y + r.Height / 2;
            return true;
        }
        catch { return false; }
    }

    private void Move(int delta)
    {
        var next = Math.Clamp(_index + delta, 0, _controls.Count - 1);
        if (next == _index) return;
        _index = next;
        FocusIndex(next);
    }

    private void ActivateCurrent()
    {
        switch (_controls[_index])
        {
            case ToggleSwitch ts:
                ts.IsOn = !ts.IsOn;
                break;
            case RadioButton rb:
                rb.IsChecked = true;
                break;
            // 磁贴主题：A 键展开/收起卡片内列表；选项行 A 键直接选中
            case Border b when ReferenceEquals(b, TileThemeCard):
                ToggleThemeList();
                break;
            case Border b when TryGetTheme(b, out var theme):
                SelectTheme(theme);
                break;
            case Button when ReferenceEquals(_controls[_index], OpacityDownButton):
                ChangeOpacity(-0.1);
                break;
            case Button when ReferenceEquals(_controls[_index], OpacityUpButton):
                ChangeOpacity(+0.1);
                break;
            case Button when ReferenceEquals(_controls[_index], WidthDownButton):
                ChangeWidth(-0.02);
                break;
            case Button when ReferenceEquals(_controls[_index], WidthUpButton):
                ChangeWidth(+0.02);
                break;
            case Button when ReferenceEquals(_controls[_index], TileOpacityDownButton):
                ChangeTileOpacity(-0.1);
                break;
            case Button when ReferenceEquals(_controls[_index], TileOpacityUpButton):
                ChangeTileOpacity(+0.1);
                break;
            case Button when ReferenceEquals(_controls[_index], SpacingDownButton):
                ChangeSpacing(-2);
                break;
            case Button when ReferenceEquals(_controls[_index], SpacingUpButton):
                ChangeSpacing(+2);
                break;
        }
    }

    private void FocusIndex(int i)
    {
        var c = _controls[i];
        if (c is RadioButton or ToggleSwitch or Border)
        {
            // 单选/开关/主题卡片行：移动只移动光标（高亮），不聚焦——避免焦点/空格等连带变更；
            // 只有按 A（ActivateCurrent）才真正切换并保存。
            UpdateCursor(c);
            EnsureVisible(c);
            return;
        }
        UpdateCursor(null);
        c.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        EnsureVisible(c);
    }

    /// <summary>高亮当前光标所在控件：单选/开关/主题行=描边</summary>
    private void UpdateCursor(FrameworkElement? cursor)
    {
        foreach (var ctl in _controls)
        {
            var on = ReferenceEquals(ctl, cursor);
            switch (ctl)
            {
                case RadioButton rb:
                    rb.BorderBrush = on
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    break;
                case ToggleSwitch ts:
                    ts.BorderBrush = on
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    break;
                // 主题卡片与展开的选项行：描边直接画在该行 Border 上
                case Border b:
                    b.BorderBrush = on
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    break;
            }
        }
    }

    private void EnsureVisible(FrameworkElement c)
    {
        // 窄窗口下焦点控件自动滚入可视区（WinUI3 无 BringIntoView，用 ChangeView 手动实现）
        try
        {
            // 光标在首/尾控件：整页滚到顶/底，避免上方（分组标题）或下方内容被裁切
            if (ReferenceEquals(c, _controls[0]))
            {
                ContentScroll.ChangeView(null, 0, null);
                return;
            }
            if (ReferenceEquals(c, _controls[^1]))
            {
                ContentScroll.ChangeView(null, ContentScroll.ScrollableHeight, null);
                return;
            }

            var p = c.TransformToVisual(ContentScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            var vh = ContentScroll.ViewportHeight;
            // 上方保留 44px 余量：控件的分组标题（如"磁贴透明度"）也能进入可视区
            if (p.Y < 44)
                ContentScroll.ChangeView(null, Math.Max(0, ContentScroll.VerticalOffset + p.Y - 44), null);
            else if (p.Y + c.ActualHeight > vh)
                ContentScroll.ChangeView(null, ContentScroll.VerticalOffset + p.Y + c.ActualHeight - vh + 8, null);
        }
        catch { }
    }

    private void OnBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        App.Main?.ShowSettingsHub();

    /// <summary>玻璃效果开关 → 立即保存并应用</summary>
    private void OnGlassChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.GlassEffect = GlassToggle.IsOn);
        App.Main?.ApplyBackground();
    }

    /// <summary>鼠标/触屏点击标题行展开/收起主题列表（手柄走 A 键）；点击选项行不冒泡到这里</summary>
    private void OnTileThemeCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (!_ready) return;
        ToggleThemeList();
    }

    /// <summary>按主题表生成展开列表的行（预览条 + 名称 + 说明 + 选中勾）</summary>
    private void FillTileThemes(TileTheme current)
    {
        TileThemeList.Children.Clear();
        _themeRows.Clear();
        _themeOptionBorders.Clear();
        _themeChecks.Clear();
        foreach (var opt in ThemeOptions)
            TileThemeList.Children.Add(BuildThemeRow(opt));
        ApplyThemeSelection(current);
    }

    private Border BuildThemeRow(TileThemeOption opt)
    {
        // 迷你预览条：与磁贴一致（玻璃加底部色条、渐变加顶部高光）
        var overlay = new Grid();
        if (opt.Theme == TileTheme.Glass)
            overlay.Children.Add(new Border
            {
                Height = 3,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(PreviewAccent),
                IsHitTestVisible = false
            });
        else if (opt.Theme == TileTheme.Gradient)
            overlay.Children.Add(new Border
            {
                Height = 7,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 1 }
                    }
                },
                IsHitTestVisible = false
            });
        var swatch = new Border
        {
            Width = 30,
            Height = 20,
            CornerRadius = new CornerRadius(3),
            Background = TileStyle.FillFor(opt.Theme, PreviewAccent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = overlay
        };

        var name = new TextBlock
        {
            Text = opt.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        var desc = new TextBlock
        {
            Text = opt.Desc,
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var text = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(desc);

        var check = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(check, 2);
        grid.Children.Add(swatch);
        grid.Children.Add(text);
        grid.Children.Add(check);

        var row = new Border
        {
            Padding = new Thickness(8, 6, 10, 6),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(1.5),
            Child = grid
        };
        // 选项行自行处理点按并阻止冒泡，避免误触外层卡片把列表收起
        row.Tapped += (_, e) =>
        {
            e.Handled = true;
            if (!_ready) return;
            SelectTheme(opt.Theme);
        };

        _themeRows.Add(row);
        _themeOptionBorders[opt.Theme] = row;
        _themeChecks[opt.Theme] = check;
        return row;
    }

    /// <summary>刷新选中状态（选项行底色 + 勾）+ 标题行的当前主题名称与预览</summary>
    private void ApplyThemeSelection(TileTheme current)
    {
        _currentTheme = current;
        foreach (var (theme, row) in _themeOptionBorders)
        {
            var on = theme == current;
            row.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                on ? (byte)0x2E : (byte)0x14, 0xFF, 0xFF, 0xFF));
            _themeChecks[theme].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        ThemeCurrentSwatch.Background = TileStyle.FillFor(current, PreviewAccent);
        ThemeCurrentText.Text = System.Array.Find(ThemeOptions, o => o.Theme == current)?.Name
                                ?? ThemeOptions[0].Name;
    }

    private void OnTileNameChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.ShowTileName = TileNameToggle.IsOn);
    }

    /// <summary>方形/圆角开关 → 立即保存为显式设置（之后不再随主题默认变化）</summary>
    private void OnTileCornerChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.TileCornerShape =
            TileCornerToggle.IsOn ? TileCorner.Square : TileCorner.Rounded);
    }

    /// <summary>磁贴组展开动画开关 → 立即保存；主页展开/收起时动态读取</summary>
    private void OnGroupAnimChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.GroupExpandAnimation = GroupAnimToggle.IsOn);
    }

    private void OnRandomColorChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.RandomTileColor = RandomColorToggle.IsOn);
    }

    private void OnColorChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        var hex = "#202020";
        foreach (var (value, radio) in Palette)
            if (radio.IsChecked == true) { hex = value; break; }

        SettingsService.Update(s => s.BackgroundColor = hex);
        App.Main?.ApplyBackground();
    }

    /// <summary>透明度 ±0.1（范围 0.3~1.0）</summary>
    private void ChangeOpacity(double delta)
    {
        _opacity = Math.Clamp(_opacity + delta, 0.3, 1.0);
        OpacityText.Text = $"{(int)Math.Round(_opacity * 100)}%";
        SettingsService.Update(s => s.WindowOpacity = _opacity);
        App.Main?.ApplyBackground();
    }

    private void OnOpacityDown(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeOpacity(-0.1);
    private void OnOpacityUp(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeOpacity(+0.1);

    /// <summary>侧边栏宽度 ±2%（范围 38%~50%），即时落盘并重排窗口</summary>
    private void ChangeWidth(double delta)
    {
        _widthPct = Math.Clamp(_widthPct + delta, 0.38, 0.5);
        WidthText.Text = $"{(int)Math.Round(_widthPct * 100)}%";
        SettingsService.Update(s => s.SidebarWidthPercent = _widthPct);
        App.Main?.ApplySidebarLayout();
    }

    private void OnWidthDown(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeWidth(-0.02);
    private void OnWidthUp(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeWidth(+0.02);

    /// <summary>磁贴间距 ±2px（范围 0~30）；主页 LayoutTiles 动态读取该值，返回主页即生效</summary>
    private void ChangeSpacing(double delta)
    {
        _spacing = Math.Clamp(_spacing + delta, 0, 30);
        SpacingText.Text = $"{(int)Math.Round(_spacing)}px";
        SettingsService.Update(s => s.TileSpacing = _spacing);
    }

    private void OnSpacingDown(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeSpacing(-2);
    private void OnSpacingUp(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeSpacing(+2);

    /// <summary>磁贴透明度 ±10%（范围 30%~100%）</summary>
    private void ChangeTileOpacity(double delta)
    {
        _tileOpacity = Math.Clamp(_tileOpacity + delta, 0.3, 1.0);
        TileOpacityText.Text = $"{(int)Math.Round(_tileOpacity * 100)}%";
        SettingsService.Update(s => s.TileOpacity = _tileOpacity);
    }

    private void OnTileOpacityDown(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeTileOpacity(-0.1);
    private void OnTileOpacityUp(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeTileOpacity(+0.1);

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
