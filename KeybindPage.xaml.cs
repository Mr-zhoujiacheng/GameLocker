using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>控制设置：呼出/全屏组合键捕获 + 手柄 ↔ 触控操作对照。
/// 手柄：方向键按"整张卡片"上下切换（每次只选中一张卡，卡内说明只读、不可单独选中，
/// 往下移动即看下一张卡的全部内容），A=捕获当前卡片的组合键，View/B=返回设置主页。</summary>
public sealed partial class KeybindPage : Page, ISettingsGamepad
{
    private bool _ready;

    /// <summary>是否处于呼出键位捕获模式（MainWindow 据此转发原始按键）</summary>
    public bool IsCapturing { get; private set; }
    private GamepadButtons _captured;

    /// <summary>捕获目标：呼出键 / 全屏键</summary>
    private enum CaptureTarget { Summon, Fullscreen }
    private CaptureTarget _target;

    /// <summary>手柄焦点所在卡片序号：0=阅读提示 1=呼出键 2=全屏键 ≥3=说明卡片</summary>
    private int _focus;

    private Border[] _cards = default!;

    private readonly SolidColorBrush _cardIdleBg = new(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _cardFocusBg = new(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _lineIdle = new(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _lineFocus = new(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));

    public KeybindPage()
    {
        InitializeComponent();
        CaptureText.Text = GamepadService.Describe(SettingsService.Current.SummonCombo);
        FsCaptureText.Text = GamepadService.Describe(SettingsService.Current.FullscreenCombo);

        // 说明数据源：按 ControlHelp 的分组顺序逐个卡片填充（含"关于"分组）
        LvHome.ItemsSource = ControlHelp.Sections[0].Rows;
        LvEdit.ItemsSource = ControlHelp.Sections[1].Rows;
        LvSettings.ItemsSource = ControlHelp.Sections[2].Rows;
        LvAbout.ItemsSource = ControlHelp.Sections[3].Rows;
        LvGlobal.ItemsSource = ControlHelp.Sections[4].Rows;

        _cards = [CardLegend, CardSummon, CardFullscreen, CardHome, CardEdit, CardSettings, CardAbout, CardGlobal];
        ApplyCardFocus();

        // 布局完成后再复位：OnNavigatedTo 时元素尚未排列，滚动偏移不可用
        Loaded += (_, _) =>
        {
            _focus = 0;
            ApplyCardFocus();
            PageScroller.ScrollToVerticalOffset(0);
            Focus(FocusState.Programmatic); // 键盘方向键等价于手柄方向键（整卡切换）
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        _focus = 0;
        ApplyCardFocus();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _ready = false;
        IsCapturing = false;
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready || IsCapturing) return;

        if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft)) Move(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight)) Move(1);
        else if (buttons.HasFlag(GamepadButtons.A)) ActivateCapture();
    }

    /// <summary>键盘等价操作：方向键=整卡切换，Enter/空格=捕获当前卡片键位（旧版由 ListView 焦点导航提供，粒度改为整张卡片）</summary>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!_ready || IsCapturing) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Up:
            case Windows.System.VirtualKey.Left:
                Move(-1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Down:
            case Windows.System.VirtualKey.Right:
                Move(1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter:
            case Windows.System.VirtualKey.Space:
                ActivateCapture();
                e.Handled = true;
                break;
        }
    }

    /// <summary>A 键：捕获当前选中卡片对应的组合键（阅读提示 / 说明卡片只读，无动作）</summary>
    private void ActivateCapture()
    {
        if (_focus == 1) StartCapture(CaptureTarget.Summon);
        else if (_focus == 2) StartCapture(CaptureTarget.Fullscreen);
    }

    /// <summary>整卡上下移动：每次只切换一张卡片，并把该卡滚到可视区顶部，方便直接读完卡内全部内容</summary>
    private void Move(int delta)
    {
        var next = Math.Clamp(_focus + delta, 0, _cards.Length - 1);
        if (next == _focus) return;
        _focus = next;
        ApplyCardFocus();
        ScrollCardToTop(_cards[_focus]);
    }

    /// <summary>刷新选中样式：只有当前卡片有高亮描边与加亮底色</summary>
    private void ApplyCardFocus()
    {
        for (var i = 0; i < _cards.Length; i++)
        {
            var on = i == _focus;
            _cards[i].BorderBrush = on ? _lineFocus : _lineIdle;
            _cards[i].Background = on ? _cardFocusBg : _cardIdleBg;
        }
    }

    /// <summary>把卡片滚到外层 PageScroller 可视区顶部（留 8px 余量）</summary>
    private void ScrollCardToTop(FrameworkElement card)
    {
        try
        {
            var y = card.TransformToVisual(PageScroller)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            PageScroller.ScrollToVerticalOffset(PageScroller.VerticalOffset + y - 8);
        }
        catch { /* 未连接视觉树时忽略 */ }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) =>
        App.Main?.ShowSettingsHub();

    /// <summary>鼠标点击呼出键位按钮</summary>
    private void OnCaptureClick(object sender, RoutedEventArgs e) => StartCapture(CaptureTarget.Summon);

    /// <summary>鼠标点击全屏键位按钮</summary>
    private void OnFsCaptureClick(object sender, RoutedEventArgs e) => StartCapture(CaptureTarget.Fullscreen);

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
