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
/// 外观设置：窗口玻璃/透明度/宽度/背景颜色 + 磁贴主题/形状/透明度/间距。
/// 手柄：方向=移动光标，A=切换开关/选色/±微调，View(Select)/B=返回设置主页。
/// </summary>
public sealed partial class AppearancePage : Page, ISettingsGamepad
{
    private List<FrameworkElement> _controls = [];
    private int _index;
    private bool _ready;
    private double _opacity = 1.0;
    private double _spacing = 10;
    private double _tileOpacity = 1.0;
    private double _widthPct = 0.40;

    /// <summary>磁贴主题可选项（名称 + 说明 + 迷你预览条）</summary>
    private static readonly TileThemeOption[] ThemeOptions =
    [
        new(TileTheme.Colorful, "彩色磁贴", "按颜色 / 随机配色"),
        new(TileTheme.Win10, "简约磁贴", "无颜色 · 深灰"),
        new(TileTheme.Gradient, "渐变磁贴", "斜向亮暗渐变"),
        new(TileTheme.Glass, "玻璃磁贴", "磨砂色底 · 无底部色条"),
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

    /// <summary>预设色：色值 → 大色卡 + 勾</summary>
    private readonly (string Hex, Button Card, FontIcon Check)[] Palette;

    /// <summary>开关卡片 → 对应开关（按 A / 点击卡片时切换真正状态）</summary>
    private readonly (Button Card, ToggleSwitch Toggle)[] _switchCards;

    private string _selectedColorHex = "#202020";

    private static readonly SolidColorBrush CursorBrush =
        new(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SelectedBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xB9, 0xA0, 0xFF));
    private static readonly SolidColorBrush TransparentBrush =
        new(Microsoft.UI.Colors.Transparent);

    public AppearancePage()
    {
        InitializeComponent();

        Palette =
        [
            ("#202020", DarkGrayCard, DarkGrayCheck),
            ("#000000", BlackCard, BlackCheck),
            ("#102A43", BlueCard, BlueCheck),
            ("#2D1B4E", PurpleCard, PurpleCheck),
            ("#1B3A2A", GreenCard, GreenCheck),
            ("#3E2723", BrownCard, BrownCheck),
        ];

        _switchCards =
        [
            (GlassCard, GlassToggle),
            (CornerCard, TileCornerToggle),
            (NameCard, TileNameToggle),
            (GroupAnimCard, GroupAnimToggle),
            (RandomColorCard, RandomColorToggle),
        ];

        var s = SettingsService.Current;
        GlassToggle.IsOn = s.GlassEffect;
        FillTileThemes(s.TileTheme);
        TileCornerToggle.IsOn = TileStyle.IsSquareRadius;
        TileNameToggle.IsOn = s.ShowTileName;
        GroupAnimToggle.IsOn = s.GroupExpandAnimation;
        RandomColorToggle.IsOn = s.RandomTileColor;
        _selectedColorHex = s.BackgroundColor;
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

        // 卡片网格：宽窗口两列并整块居中，侧边栏窄窗口回成一列靠上
        ContentScroll.SizeChanged += (_, _) => { LayoutCards(); UpdateBars(); };
        Loaded += (_, _) => { LayoutCards(); UpdateBars(); };
    }

    /// <summary>可否排两列的最小可用宽度（低于此宽度文字会被挤压，改回单列）</summary>
    private const double TwoColumnMinWidth = 560;

    /// <summary>按可用宽度重排卡片：宽=两列 + 上下居中；窄=单列 + 靠上</summary>
    private void LayoutCards()
    {
        if (SettingsGrid is null || ContentScroll is null) return;
        var avail = ContentScroll.ViewportWidth;
        if (avail <= 0) return;
        var two = avail >= TwoColumnMinWidth;

        SettingsGrid.ColumnDefinitions[1].Width = two
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        SettingsGrid.VerticalAlignment = two ? VerticalAlignment.Center : VerticalAlignment.Top;

        if (two)
        {
            // 宽：左侧玻璃效果（保持原高度）+ 其下透明度/宽度合并卡，右侧色卡 3×2 跨两行对齐整列
            Grid.SetRowSpan(ColorsCard, 2);
            Place(WindowSection, 0, 0, 2);
            Place(GlassCard, 1, 0);
            Place(SlidersCard, 2, 0);
            Place(ColorsCard, 1, 1);
            Place(TileSection, 3, 0, 2);
            Place(TileThemeCard, 4, 0, 2);
            Place(CornerCard, 5, 0);
            Place(NameCard, 5, 1);
            Place(GroupAnimCard, 6, 0);
            Place(RandomColorCard, 6, 1);
            Place(TileOpacityCard, 7, 0);
            Place(SpacingCard, 7, 1);
        }
        else
        {
            Grid.SetRowSpan(ColorsCard, 1);
            var row = 0;
            Place(WindowSection, row++, 0, 2);
            Place(GlassCard, row++, 0, 2);
            Place(ColorsCard, row++, 0, 2);
            Place(SlidersCard, row++, 0, 2);
            Place(TileSection, row++, 0, 2);
            Place(TileThemeCard, row++, 0, 2);
            Place(CornerCard, row++, 0, 2);
            Place(NameCard, row++, 0, 2);
            Place(GroupAnimCard, row++, 0, 2);
            Place(RandomColorCard, row++, 0, 2);
            Place(TileOpacityCard, row++, 0, 2);
            Place(SpacingCard, row, 0, 2);
        }
    }

    private static void Place(FrameworkElement el, int row, int col, int span = 1)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        Grid.SetColumnSpan(el, span);
    }

    /// <summary>滑轨填充宽度按当前值比例刷新</summary>
    private void UpdateBars()
    {
        SetBar(OpacityFill, OpacityTrack, (_opacity - 0.3) / 0.7);
        SetBar(WidthFill, WidthTrack, (_widthPct - 0.38) / 0.12);
        SetBar(TileOpacityFill, TileOpacityTrack, (_tileOpacity - 0.3) / 0.7);
        SetBar(SpacingFill, SpacingTrack, _spacing / 30.0);
    }

    private static void SetBar(Border fill, Grid track, double pct)
    {
        var w = track.ActualWidth;
        if (w <= 0) w = 120;
        fill.Width = Math.Max(4, w * Math.Clamp(pct, 0, 1));
    }

    /// <summary>按展开状态重建可导航控件列表：折叠时移除选项行，
    /// 避免光标落到已隐藏的行上（ActualHeight 残留会让 BuildRows 仍把它们算成一行）</summary>
    private void SyncControls()
    {
        var controls = new List<FrameworkElement>
        {
            GlassCard, OpacityDownButton, OpacityUpButton,
            WidthDownButton, WidthUpButton
        };
        foreach (var (_, card, _) in Palette) controls.Add(card);
        controls.Add(TileThemeCard);
        if (_themeExpanded) controls.AddRange(_themeRows);
        controls.AddRange([CornerCard, NameCard, GroupAnimCard, RandomColorCard,
            TileOpacityDownButton, TileOpacityUpButton, SpacingDownButton, SpacingUpButton]);
        _controls = controls;
    }

    /// <summary>色值 → 勾选对应色卡（找不到预设时回落到深灰）</summary>
    private void CheckColor(string hex)
    {
        var match = hex;
        if (Array.FindIndex(Palette, p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase)) < 0)
            match = Palette[0].Hex;
        _selectedColorHex = match;

        foreach (var (value, card, check) in Palette)
        {
            var on = string.Equals(value, match, StringComparison.OrdinalIgnoreCase);
            check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            card.BorderBrush = on ? SelectedBrush : TransparentBrush;
        }
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

        // 行感知导航：开关/色卡为多列，−/＋ 为一行两控件，
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
        var c = _controls[_index];
        // 磁贴主题：A 键展开/收起卡片内列表；选项行 A 键直接选中
        if (c is Border b && ReferenceEquals(b, TileThemeCard))
        {
            ToggleThemeList();
            return;
        }
        if (c is Border row && TryGetTheme(row, out var theme))
        {
            SelectTheme(theme);
            return;
        }
        if (c is not Button btn) return;

        foreach (var (card, toggle) in _switchCards)
            if (ReferenceEquals(card, btn)) { toggle.IsOn = !toggle.IsOn; return; }
        foreach (var (hex, card, _) in Palette)
            if (ReferenceEquals(card, btn)) { ApplyColor(hex); return; }

        if (ReferenceEquals(btn, OpacityDownButton)) { ChangeOpacity(-0.1); return; }
        if (ReferenceEquals(btn, OpacityUpButton)) { ChangeOpacity(+0.1); return; }
        if (ReferenceEquals(btn, WidthDownButton)) { ChangeWidth(-0.02); return; }
        if (ReferenceEquals(btn, WidthUpButton)) { ChangeWidth(+0.02); return; }
        if (ReferenceEquals(btn, TileOpacityDownButton)) { ChangeTileOpacity(-0.1); return; }
        if (ReferenceEquals(btn, TileOpacityUpButton)) { ChangeTileOpacity(+0.1); return; }
        if (ReferenceEquals(btn, SpacingDownButton)) { ChangeSpacing(-2); return; }
        if (ReferenceEquals(btn, SpacingUpButton)) { ChangeSpacing(+2); }
    }

    private void FocusIndex(int i)
    {
        var c = _controls[i];
        // 先清掉上一项的系统焦点框：页面用自绘光标/选框，残留的焦点框不会跟着光标走
        FocusRing.HideCurrent(XamlRoot);
        if (IsCursorDrawn(c))
        {
            // 卡片类（开关行 / 色卡 / 主题行）：移动只重绘光标，不聚焦——避免焦点/空格等连带变更；
            // 只有按 A（ActivateCurrent）才真正切换并保存。
            UpdateCursor(c);
            EnsureVisible(c);
            return;
        }
        UpdateCursor(null);
        if (c is Control ctl) FocusRing.FocusWithRing(ctl);
        EnsureVisible(c);
    }

    /// <summary>该控件是否由页面自绘光标（改为 BorderBrush），而不是系统焦点框</summary>
    private bool IsCursorDrawn(FrameworkElement c) =>
        // 卡片与 ± 步进按钮统一自绘白框（系统焦点框在这些小按钮上几乎看不见）
        c is Border or Button;

    /// <summary>高亮当前光标所在控件：卡片/步进按钮=白框；色卡未被选中时恢复浅紫框</summary>
    private void UpdateCursor(FrameworkElement? cursor)
    {
        foreach (var ctl in _controls)
        {
            var on = ReferenceEquals(ctl, cursor);
            switch (ctl)
            {
                case Button b:
                    var selected = false;
                    foreach (var (hex, card, _) in Palette)
                        if (ReferenceEquals(card, b) &&
                            string.Equals(hex, _selectedColorHex, StringComparison.OrdinalIgnoreCase))
                            selected = true;
                    b.BorderBrush = on ? CursorBrush : selected ? SelectedBrush : TransparentBrush;
                    break;
                // 主题卡片与展开的选项行：描边直接画在该行 Border 上
                case Border bd:
                    bd.BorderBrush = on ? CursorBrush : TransparentBrush;
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

    /// <summary>点击开关卡片：光标移过去并切换（手柄走 A 键）</summary>
    private void OnGlassCardClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ToggleCard(GlassCard, GlassToggle);

    private void OnCornerCardClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ToggleCard(CornerCard, TileCornerToggle);

    private void OnNameCardClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ToggleCard(NameCard, TileNameToggle);

    private void OnGroupAnimCardClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ToggleCard(GroupAnimCard, GroupAnimToggle);

    private void OnRandomColorCardClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ToggleCard(RandomColorCard, RandomColorToggle);

    private void ToggleCard(Button card, ToggleSwitch toggle)
    {
        if (!_ready) return;
        SelectCard(card);
        toggle.IsOn = !toggle.IsOn;
    }

    /// <summary>点击大色卡：选中并立即应用窗口背景色</summary>
    private void OnColorCardClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is not Button btn || btn.Tag is not string hex) return;
        SelectCard(btn);
        ApplyColor(hex);
    }

    private void SelectCard(Button card)
    {
        var i = _controls.IndexOf(card);
        if (i < 0) return;
        _index = i;
        FocusIndex(i);
    }

    private void ApplyColor(string hex)
    {
        CheckColor(hex);
        SettingsService.Update(s => s.BackgroundColor = hex);
        App.Main?.ApplyBackground();
        UpdateCursor(_controls.Count > _index ? _controls[_index] : null);
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
        // 迷你预览条：与磁贴一致（玻璃用磨砂色底本身，不再叠加底部色条；渐变加顶部高光）
        var overlay = new Grid();
        if (opt.Theme == TileTheme.Gradient)
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

    /// <summary>透明度 ±0.1（范围 0.3~1.0）</summary>
    private void ChangeOpacity(double delta)
    {
        _opacity = Math.Clamp(_opacity + delta, 0.3, 1.0);
        OpacityText.Text = $"{(int)Math.Round(_opacity * 100)}%";
        SettingsService.Update(s => s.WindowOpacity = _opacity);
        App.Main?.ApplyBackground();
        UpdateBars();
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
        UpdateBars();
    }

    private void OnWidthDown(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeWidth(-0.02);
    private void OnWidthUp(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeWidth(+0.02);

    /// <summary>磁贴间距 ±2px（范围 0~30）；主页 LayoutTiles 动态读取该值，返回主页即生效</summary>
    private void ChangeSpacing(double delta)
    {
        _spacing = Math.Clamp(_spacing + delta, 0, 30);
        SpacingText.Text = $"{(int)Math.Round(_spacing)}px";
        SettingsService.Update(s => s.TileSpacing = _spacing);
        UpdateBars();
    }

    private void OnSpacingDown(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeSpacing(-2);
    private void OnSpacingUp(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeSpacing(+2);

    /// <summary>磁贴透明度 ±10%（范围 30%~100%）</summary>
    private void ChangeTileOpacity(double delta)
    {
        _tileOpacity = Math.Clamp(_tileOpacity + delta, 0.3, 1.0);
        TileOpacityText.Text = $"{(int)Math.Round(_tileOpacity * 100)}%";
        SettingsService.Update(s => s.TileOpacity = _tileOpacity);
        UpdateBars();
    }

    private void OnTileOpacityDown(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeTileOpacity(-0.1);
    private void OnTileOpacityUp(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChangeTileOpacity(+0.1);

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
