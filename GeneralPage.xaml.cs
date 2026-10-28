using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 常规设置：开机自启、启动行为、侧边栏停靠位置。
/// 手柄：方向=移动焦点，A=切换开关/选中单选，View(Select)/B=返回设置主页。
/// </summary>
public sealed partial class GeneralPage : Page, ISettingsGamepad
{
    private Control[] _controls = [];
    /// <summary>开关行卡片 → 对应开关（按 A / 点击时切换真正状态）</summary>
    private (Button row, ToggleSwitch toggle)[] _switchRows = [];
    private int _index;
    private bool _ready;

    public GeneralPage()
    {
        InitializeComponent();

        var s = SettingsService.Current;
        AutoStartToggle.IsOn = s.AutoStart;
        HideAfterLaunchToggle.IsOn = s.HideAfterLaunch;
        StartFullscreenToggle.IsOn = s.StartFullscreen;
        AnimationToggle.IsOn = s.SummonAnimation;
        UiSoundToggle.IsOn = s.UiSoundEnabled;
        EdgeGestureToggle.IsOn = s.EdgeGestureSummon;
        TabletModeToggle.IsOn = s.TabletMode;
        StartMenuToggle.IsOn = s.StartMenuMode;
        if (s.SidebarSide == DockSide.Right) RightRadio.IsChecked = true;
        else if (s.SidebarSide == DockSide.Center) CenterRadio.IsChecked = true;
        else LeftRadio.IsChecked = true;

        _switchRows =
        [
            (AutoStartRow, AutoStartToggle),
            (HideAfterLaunchRow, HideAfterLaunchToggle),
            (StartFullscreenRow, StartFullscreenToggle),
            (AnimationRow, AnimationToggle),
            (UiSoundRow, UiSoundToggle),
            (EdgeGestureRow, EdgeGestureToggle),
            (TabletModeRow, TabletModeToggle),
            (StartMenuRow, StartMenuToggle)
        ];

        UpdateDockVisibility();
        RebuildControls();

        // 卡片网格：宽窗口两列并整块居中，侧边栏窄窗口回成一列靠上
        ContentScroll.SizeChanged += (_, _) => LayoutCards();
        Loaded += (_, _) => LayoutCards();
    }

    /// <summary>可否排两列的最小可用宽度（低于此宽度文字会被挤压，改回单列）</summary>
    private const double TwoColumnMinWidth = 560;

    /// <summary>按可用宽度重排卡片：宽=两列 + 上下居中；窄=单列 + 靠上</summary>
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
            Grid.SetColumnSpan(card, 1);
        }

        var baseRow = two ? (_switchRows.Length + 1) / 2 : _switchRows.Length;
        foreach (var (el, r) in new (FrameworkElement el, int r)[]
                 { (DockSideTitle, baseRow), (DockSideHint, baseRow + 1), (DockSidePanel, baseRow + 2) })
        {
            Grid.SetRow(el, r);
            Grid.SetColumn(el, 0);
            Grid.SetColumnSpan(el, 2);
        }
    }

    /// <summary>按开关状态重建导航控件表：开始菜单模式下「窗口位置」整组折叠，不参与手柄导航</summary>
    private void RebuildControls()
    {
        var list = new List<Control>
        {
            AutoStartRow, HideAfterLaunchRow, StartFullscreenRow,
            AnimationRow, UiSoundRow,
            EdgeGestureRow, TabletModeRow, StartMenuRow
        };
        if (!StartMenuToggle.IsOn) list.AddRange([LeftRadio, CenterRadio, RightRadio]);
        _controls = list.ToArray();
    }

    /// <summary>开始菜单模式下窗口固定居中停靠，隐藏「窗口位置」三选项</summary>
    private void UpdateDockVisibility()
    {
        var show = !StartMenuToggle.IsOn;
        DockSideTitle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DockSideHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DockSidePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        _index = 0;
        RebuildControls();
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

        // 行感知导航：开关为整行单控件、停靠位置/宽度为左右两控件，
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

    private bool TryCenter(Control c, out double x, out double y)
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
        switch (_controls[_index])
        {
            case RadioButton rb:
                rb.IsChecked = true;
                break;
            case Button row:
                // 开关行卡片：切换对应开关（Toggled 事件里保存）
                foreach (var (r, t) in _switchRows)
                    if (ReferenceEquals(r, row)) { t.IsOn = !t.IsOn; break; }
                break;
        }
    }

    private void FocusIndex(int i)
    {
        var c = _controls[i];
        // 先清掉上一项的系统焦点框：页面用自绘光标/选框，残留的焦点框不会跟着光标走
        FocusRing.HideCurrent(XamlRoot);
        if (c is RadioButton or Button)
        {
            // 单选/开关：移动只移动光标（高亮），不聚焦——避免焦点/空格等连带变更；
            // 只有按 A（ActivateCurrent）才真正切换并保存。
            UpdateCursor(c);
            EnsureVisible(c);
            return;
        }
        UpdateCursor(null);
        FocusRing.FocusWithRing(c);
        EnsureVisible(c);
    }

    /// <summary>高亮当前光标所在控件：单选/开关行=白框描边</summary>
    private void UpdateCursor(Control? cursor)
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
                case Button row:
                    row.BorderBrush = on
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    break;
            }
        }
    }

    /// <summary>触屏/鼠标点击开关行卡片：先选中再切换（与手柄 A 一致）</summary>
    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not Button row) return;
        for (var i = 0; i < _switchRows.Length; i++)
        {
            if (!ReferenceEquals(_switchRows[i].row, row)) continue;
            _index = Math.Max(0, Array.IndexOf(_controls, row));
            UpdateCursor(row);
            EnsureVisible(row);
            _switchRows[i].toggle.IsOn = !_switchRows[i].toggle.IsOn;
            break;
        }
    }

    private void EnsureVisible(Control c)
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
            // 上方保留 44px 余量：控件的分组标题也能进入可视区
            if (p.Y < 44)
                ContentScroll.ChangeView(null, Math.Max(0, ContentScroll.VerticalOffset + p.Y - 44), null);
            else if (p.Y + c.ActualHeight > vh)
                ContentScroll.ChangeView(null, ContentScroll.VerticalOffset + p.Y + c.ActualHeight - vh + 8, null);
        }
        catch { }
    }

    private void OnBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        App.Main?.ShowSettingsHub();

    private void OnToggleChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        Persist();
    }

    private void OnSideChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        Persist();
        App.Main?.ApplySidebarLayout();
    }

    /// <summary>边缘手势呼出开关 → 立即启停边缘监听</summary>
    private void OnEdgeGestureChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.EdgeGestureSummon = EdgeGestureToggle.IsOn);
        App.Main?.SyncEdgeGesture();
    }

    /// <summary>操作音效开关 → 保存；打开时立刻放一声确认音，便于确认已生效</summary>
    private void OnUiSoundChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.UiSoundEnabled = UiSoundToggle.IsOn);
        if (UiSoundToggle.IsOn) UiSound.Play(UiSoundKind.Confirm);
    }

    /// <summary>平板模式开关 → 保存并立即隐藏/恢复本页提示条（其他页面进入时由 GamepadHintBar 自行应用）</summary>
    private void OnTabletModeChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.TabletMode = TabletModeToggle.IsOn);
        HintBar.Visibility = TabletModeToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>替代 Windows 开始菜单开关 → 保存并立即切换窗口形态 + 重挂键盘钩子</summary>
    private void OnStartMenuChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        var on = StartMenuToggle.IsOn;
        SettingsService.Update(s =>
        {
            s.StartMenuMode = on;
            if (on) s.SidebarSide = DockSide.Center; // 开始菜单模式固定居中停靠
        });
        UpdateDockVisibility();
        RebuildControls();
        _index = Math.Clamp(_index, 0, _controls.Length - 1);
        FocusIndex(_index);
        App.Main?.ApplyStartMenuMode();
    }

    private void Persist()
    {
        SettingsService.Update(s =>
        {
            s.AutoStart = AutoStartToggle.IsOn;
            s.HideAfterLaunch = HideAfterLaunchToggle.IsOn;
            s.StartFullscreen = StartFullscreenToggle.IsOn;
            s.SummonAnimation = AnimationToggle.IsOn;
            s.StartMenuMode = StartMenuToggle.IsOn;
            s.SidebarSide = StartMenuToggle.IsOn ? DockSide.Center
                : RightRadio.IsChecked == true ? DockSide.Right
                : CenterRadio.IsChecked == true ? DockSide.Center
                : DockSide.Left;
        });
    }

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
