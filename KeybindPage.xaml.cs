using GameLocker.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>控制设置：呼出组合键捕获 + 手柄/触控操作对照说明。
/// 手柄：A=进入捕获（按住目标组合键，全松手即保存生效），View(Select)/B=返回设置主页。</summary>
public sealed partial class KeybindPage : Page, ISettingsGamepad
{
    private bool _ready;

    /// <summary>是否处于呼出键位捕获模式（MainWindow 据此转发原始按键）</summary>
    public bool IsCapturing { get; private set; }
    private GamepadButtons _captured;

    private ListView[] _lists = default!;

    /// <summary>捕获目标：呼出键 / 全屏键</summary>
    private enum CaptureTarget { Summon, Fullscreen }
    private CaptureTarget _target;

    public KeybindPage()
    {
        InitializeComponent();
        CaptureText.Text = GamepadService.Describe(SettingsService.Current.SummonCombo);
        FsCaptureText.Text = GamepadService.Describe(SettingsService.Current.FullscreenCombo);
        // 说明行数据源：ListView 自带可见选中高亮，手柄方向键可逐行定位
        LvHome.ItemsSource = ControlHelp.Sections[0].Rows;
        LvEdit.ItemsSource = ControlHelp.Sections[1].Rows;
        LvSettings.ItemsSource = ControlHelp.Sections[2].Rows;
        LvGlobal.ItemsSource = ControlHelp.Sections[3].Rows;
        _lists = [LvHome, LvEdit, LvSettings, LvGlobal];

        // 布局完成后再落焦点：OnNavigatedTo 时元素尚未排列，Focus() 会静默失败（光标"消失"的根因）
        Loaded += (_, _) =>
        {
            _focus = 0;
            PageScroller.ScrollToVerticalOffset(0);
            CaptureButton.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        _focus = 0; // 默认焦点在捕获按钮（序列 0=捕获按钮 ≥1=说明行；返回箭头不可手柄选中，B 键返回）
        CaptureButton.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _ready = false;
        IsCapturing = false;
    }

    /// <summary>手柄导航：方向键按视觉顺序在 呼出捕获→全屏捕获→各说明行（跨 4 张卡片展平）间移动，
    /// 说明行仅选中查看；A=捕获焦点按钮对应组合键；返回箭头不可手柄选中（B 键返回）；捕获模式期间按键交给捕获逻辑</summary>
    private int _focus; // 0=呼出捕获 1=全屏捕获 ≥2=说明行（展平索引）

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready || IsCapturing) return;

        if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft)) Move(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight)) Move(1);
        else if (buttons.HasFlag(GamepadButtons.A))
        {
            if (_focus == 0) StartCapture(CaptureTarget.Summon);
            else if (_focus == 1) StartCapture(CaptureTarget.Fullscreen);
            // 说明行：仅选中，A 无动作
        }
    }

    private void Move(int delta)
    {
        var total = 2 + _lists.Sum(l => l.Items.Count);
        var next = Math.Clamp(_focus + delta, 0, total - 1);
        if (next == _focus) return;
        _focus = next;

        if (next == 0)
        {
            // 回到呼出捕获按钮：清除全部行选中高亮（避免"光标还在下面"的错觉），并整页滚回最顶
            foreach (var lv in _lists) lv.SelectedIndex = -1;
            CaptureButton.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
            PageScroller.ScrollToVerticalOffset(0);
            return;
        }
        if (next == 1)
        {
            foreach (var lv in _lists) lv.SelectedIndex = -1;
            FsCaptureButton.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
            PageScroller.ScrollToVerticalOffset(0);
            return;
        }

        // 说明行：展平索引 → (列表, 项)；选中行项容器并聚焦，再把行手动滚入外层可视区
        var i = next - 2;
        foreach (var lv in _lists)
        {
            if (i < lv.Items.Count)
            {
                foreach (var other in _lists)
                    if (!ReferenceEquals(other, lv)) other.SelectedIndex = -1; // 高亮只保留在当前列表
                lv.SelectedIndex = i;
                if (lv.ContainerFromIndex(i) is ListViewItem lvi)
                {
                    lvi.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
                    EnsureVisible(lvi);
                }
                return;
            }
            i -= lv.Items.Count;
        }
    }

    /// <summary>把元素滚动到外层 PageScroller 可视范围内（上下各留 6px 余量）</summary>
    private void EnsureVisible(Microsoft.UI.Xaml.FrameworkElement el)
    {
        try
        {
            var y = el.TransformToVisual(PageScroller)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (y < 6)
                PageScroller.ScrollToVerticalOffset(PageScroller.VerticalOffset + y - 6);
            else if (y + el.ActualHeight > PageScroller.ViewportHeight - 6)
                PageScroller.ScrollToVerticalOffset(
                    PageScroller.VerticalOffset + y + el.ActualHeight - PageScroller.ViewportHeight + 6);
        }
        catch { /* 未连接视觉树时忽略 */ }
    }

    private void OnBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        App.Main?.ShowSettingsHub();

    /// <summary>鼠标点击呼出键位按钮</summary>
    private void OnCaptureClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => StartCapture(CaptureTarget.Summon);

    /// <summary>鼠标点击全屏键位按钮</summary>
    private void OnFsCaptureClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => StartCapture(CaptureTarget.Fullscreen);

    /// <summary>进入捕获模式：等待用户按下组合键并松开</summary>
    private void StartCapture(CaptureTarget target)
    {
        if (IsCapturing) return;
        IsCapturing = true;
        _target = target;
        _captured = GamepadButtons.None;
        SetCaptureText("请按住组合键…");
    }

    private void SetCaptureText(string text)
    {
        if (_target == CaptureTarget.Summon) CaptureText.Text = text;
        else FsCaptureText.Text = text;
    }

    /// <summary>捕获模式下累积按下的按键（MainWindow 转发边沿）</summary>
    public void CaptureButtons(GamepadButtons pressed)
    {
        if (!IsCapturing) return;
        _captured |= pressed;
        var desc = GamepadService.Describe(_captured);
        SetCaptureText(desc == "未设置" ? "请按住组合键…" : desc);
    }

    /// <summary>所有键松开 → 结束捕获并保存</summary>
    public void NotifyAllReleased()
    {
        if (!IsCapturing) return;
        if (_captured != GamepadButtons.None)
        {
            IsCapturing = false;
            SetCaptureText(GamepadService.Describe(_captured));
            if (_target == CaptureTarget.Summon)
            {
                App.Main?.Gamepad.SetSummonCombo(_captured);
                SettingsService.Update(s => s.SummonCombo = _captured);
            }
            else
            {
                App.Main?.Gamepad.SetFullscreenCombo(_captured);
                SettingsService.Update(s => s.FullscreenCombo = _captured);
            }
        }
        // 若一个键都没捕获到（误触），保持捕获模式等待真正按键
    }
}
