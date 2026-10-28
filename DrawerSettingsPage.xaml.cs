using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 抽屉设置：主页下方磁贴抽屉区域的显示总开关 + 抽屉控件颜色（标签底色 / 选中下划线 / 面板描边）。
/// 手柄：方向=移动焦点，A=切换开关/选中颜色，View(Select)/B=返回设置主页。
/// </summary>
public sealed partial class DrawerSettingsPage : Page, ISettingsGamepad
{
    /// <summary>可导航控件：4 张开关卡片 + 6 张大色卡</summary>
    private Button[] _controls = [];
    /// <summary>开关卡片 → 对应开关（按 A / 点击时切换真正状态）</summary>
    private (Button row, ToggleSwitch toggle)[] _switchRows = [];
    private int _index;
    private bool _ready;
    private string _colorHex = "none";

    /// <summary>预设色：色值（"none"=默认，用抽屉自身的强调色） → 大色卡 + 勾选标记</summary>
    private readonly (string Hex, Button Card, FontIcon Check)[] Palette;

    private static readonly SolidColorBrush CursorBrush =
        new(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SelectedBrush =
        new(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xE9, 0xA0));
    private static readonly SolidColorBrush ClearBrush =
        new(Microsoft.UI.Colors.Transparent);

    public DrawerSettingsPage()
    {
        InitializeComponent();

        Palette =
        [
            ("none", DefaultColorCard, DefaultCheck),
            ("#202020", DarkGrayCard, DarkGrayCheck),
            ("#000000", BlackCard, BlackCheck),
            ("#102A43", BlueCard, BlueCheck),
            ("#2D1B4E", PurpleCard, PurpleCheck),
            ("#1B3A2A", GreenCard, GreenCheck),
        ];

        var s = SettingsService.Current;
        DrawerToggle.IsOn = s.DrawerEnabled;
        UniformSmallToggle.IsOn = s.DrawerUniformSmall;
        AutoHideToggle.IsOn = s.DrawerAutoHide;
        TransparentToggle.IsOn = s.DrawerTransparent;
        _colorHex = s.DrawerColor;

        _switchRows =
        [
            (DrawerRow, DrawerToggle),
            (UniformSmallRow, UniformSmallToggle),
            (AutoHideRow, AutoHideToggle),
            (TransparentRow, TransparentToggle)
        ];

        _controls =
        [
            DrawerRow, UniformSmallRow, AutoHideRow, TransparentRow,
            DefaultColorCard, DarkGrayCard, BlackCard, BlueCard, PurpleCard, GreenCard
        ];

        UpdateColorCards();

        // 卡片网格：宽窗口两列并整块居中，侧边栏窄窗口回成一列靠上
        ContentScroll.SizeChanged += (_, _) => LayoutCards();
        Loaded += (_, _) => LayoutCards();
    }

    /// <summary>可否排两列的最小可用宽度</summary>
    private const double TwoColumnMinWidth = 560;

    /// <summary>按可用宽度重排：宽=开关两列 + 上下居中；窄=单列 + 靠上</summary>
    private void LayoutCards()
    {
        if (SwitchGrid is null || ContentScroll is null) return;
        var avail = ContentScroll.ViewportWidth;
        if (avail <= 0) return;
        var two = avail >= TwoColumnMinWidth;

        SwitchGrid.ColumnDefinitions[1].Width = two
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        SwitchGrid.VerticalAlignment = two ? VerticalAlignment.Center : VerticalAlignment.Top;

        for (var i = 0; i < _switchRows.Length; i++)
        {
            var card = _switchRows[i].row;
            Grid.SetRow(card, two ? i / 2 : i);
            Grid.SetColumn(card, two ? i % 2 : 0);
        }

        var baseRow = two ? (_switchRows.Length + 1) / 2 : _switchRows.Length;
        Grid.SetRow(ColorSection, baseRow);
        Grid.SetColumn(ColorSection, 0);
        Grid.SetColumnSpan(ColorSection, 2);
        Grid.SetRow(ColorsCard, baseRow + 1);
        Grid.SetColumn(ColorsCard, 0);
        Grid.SetColumnSpan(ColorsCard, 2);
    }

    /// <summary>刷新色卡选中标记：选中=浅蓝描边 + 勾，未选中=无描边</summary>
    private void UpdateColorCards()
    {
        foreach (var (hex, card, check) in Palette)
        {
            var on = hex == _colorHex;
            check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // 光标框由 UpdateCursor 单独绘制，这里只处理"选中"视觉
            if (!ReferenceEquals(card, CursorCard()))
                card.BorderBrush = on ? SelectedBrush : ClearBrush;
        }
    }

    private Button? CursorCard() => _index >= 0 && _index < _controls.Length ? _controls[_index] : null;

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
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready) return;

        // 行感知导航：开关为整行单控件，颜色为 3 列 × 2 行，
        // 上下在相邻行之间移动（取最接近当前列的），左右在同一行内移动。
        if (buttons.HasFlag(GamepadButtons.DPadUp)) MoveRow(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown)) MoveRow(1);
        else if (buttons.HasFlag(GamepadButtons.DPadLeft)) MoveCol(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadRight)) MoveCol(1);
        else if (buttons.HasFlag(GamepadButtons.A))
            ActivateCurrent();
    }

    // 按控件中心 Y 聚成的"行"，每行内按 X 排序（每次按键实时重建，滚动后坐标仍准确）
    private readonly List<List<(int idx, double x, double y)>> _rows = [];

    private void BuildRows()
    {
        _rows.Clear();
        var pts = new List<(int idx, double x, double y)>();
        for (var i = 0; i < _controls.Length; i++)
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

    private bool TryCenter(Button c, out double x, out double y)
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
        var next = Math.Clamp(_index + delta, 0, _controls.Length - 1);
        if (next == _index) return;
        _index = next;
        FocusIndex(next);
    }

    private void ActivateCurrent()
    {
        var card = _controls[_index];
        foreach (var (row, toggle) in _switchRows)
        {
            if (!ReferenceEquals(row, card)) continue;
            toggle.IsOn = !toggle.IsOn;
            return;
        }
        if (TryGetColor(card, out var hex)) ApplyColor(hex, card);
    }

    private bool TryGetColor(Button card, out string hex)
    {
        foreach (var (value, c, _) in Palette)
        {
            if (!ReferenceEquals(c, card)) continue;
            hex = value;
            return true;
        }
        hex = "none";
        return false;
    }

    /// <summary>选中颜色：更新标记 + 立即保存并应用到主页抽屉</summary>
    private void ApplyColor(string hex, Button card)
    {
        _colorHex = hex;
        UpdateColorCards();
        UpdateCursor(card);

        SettingsService.Update(s => s.DrawerColor = hex);
        App.Main?.ApplyDrawerSettings();
    }

    private void FocusIndex(int i)
    {
        var c = _controls[i];
        // 卡片：移动只移动光标（白框），不聚焦；按 A 才切换并保存
        UpdateCursor(c);
        EnsureVisible(c);
    }

    private void UpdateCursor(Button? cursor)
    {
        foreach (var ctl in _controls)
        {
            var on = ReferenceEquals(ctl, cursor);
            if (on)
            {
                ctl.BorderBrush = CursorBrush;
            }
            else if (TryGetColor(ctl, out var hex))
            {
                ctl.BorderBrush = hex == _colorHex ? SelectedBrush : ClearBrush;
            }
            else
            {
                ctl.BorderBrush = ClearBrush;
            }
        }
    }

    private void EnsureVisible(Button c)
    {
        try
        {
            var p = c.TransformToVisual(ContentScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            var vh = ContentScroll.ViewportHeight;
            if (p.Y < 44)
                ContentScroll.ChangeView(null, Math.Max(0, ContentScroll.VerticalOffset + p.Y - 44), null);
            else if (p.Y + c.ActualHeight > vh)
                ContentScroll.ChangeView(null, ContentScroll.VerticalOffset + p.Y + c.ActualHeight - vh + 8, null);
        }
        catch { }
    }

    private void OnBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        App.Main?.ShowSettingsHub();

    /// <summary>点击开关卡片：光标移过来并切换开关</summary>
    private void OnRowClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button row) return;
        var i = Array.IndexOf(_controls, row);
        if (i >= 0) FocusIndex(i);
        ActivateCurrent();
    }

    /// <summary>点击色卡：选中该颜色（不切换开关）</summary>
    private void OnColorCardClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button card) return;
        var i = Array.IndexOf(_controls, card);
        if (i >= 0) _index = i;
        if (TryGetColor(card, out var hex)) ApplyColor(hex, card);
        EnsureVisible(card);
    }

    private void OnToggleChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.DrawerEnabled = DrawerToggle.IsOn);
        // 即时应用到主页（若已创建）：关闭则隐藏抽屉区域，打开则确保宿主存在并显示
        App.Main?.ApplyDrawerSettings();
    }

    private void OnUniformSmallChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.DrawerUniformSmall = UniformSmallToggle.IsOn);
        // 即时重排抽屉内层磁贴（若主页已创建）
        App.Main?.ApplyDrawerSettings();
    }

    private void OnAutoHideChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.DrawerAutoHide = AutoHideToggle.IsOn);
        // 即时应用：打开立即收起抽屉（带动画），关闭恢复常驻显示
        App.Main?.ApplyDrawerSettings();
    }

    private void OnTransparentChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.DrawerTransparent = TransparentToggle.IsOn);
        // 即时应用：切换玻璃底/透明底与描边粗细
        App.Main?.ApplyDrawerSettings();
    }

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
