using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>控制设置：上半区「快捷映射」= 呼出组合键 / 键盘呼出键 / 全屏组合键捕获；下半区「基本操作」= 简化后的通用操作说明。
/// 手柄：方向键按"整张卡片"上下切换（每次只选中一张卡，卡内说明只读、不可单独选中），A=捕获当前卡片的键位，
/// View/B=返回设置主页。键盘呼出键仅键盘有效，按下即刻呼出（无需长按），可含 Ctrl/Shift/Alt/Win 组合。</summary>
public sealed partial class KeybindPage : Page, ISettingsGamepad
{
    private bool _ready;

    /// <summary>是否处于呼出键位捕获模式（MainWindow 据此转发原始按键）</summary>
    public bool IsCapturing { get; private set; }
    private GamepadButtons _captured;

    // 键盘呼出键捕获状态：主键虚拟键码 + 快照的修饰键标志；_liveMods 累积当前按住的修饰键
    private int _capturedKey;
    private int _capturedMods;
    private int _liveMods;

    /// <summary>捕获目标：呼出键 / 键盘呼出键 / 全屏键</summary>
    private enum CaptureTarget { Summon, KeySummon, Fullscreen }
    private CaptureTarget _target;

    /// <summary>手柄焦点所在卡片序号：0=呼出键 1=键盘呼出键 2=全屏键 3=基本操作说明</summary>
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
        KeyCaptureText.Text = KeySendService.MappingLabel(0, SettingsService.Current.KeyboardSummonKey,
            SettingsService.Current.KeyboardSummonMods);
        FsCaptureText.Text = GamepadService.Describe(SettingsService.Current.FullscreenCombo);

        // 说明数据源：只保留一份"基本操作"（快捷映射与说明分开，不细分到每个页面）
        LvBasic.ItemsSource = ControlHelp.Basic;

        _cards = [CardSummon, CardKeySummon, CardFullscreen, CardBasic];
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
        // 离开页面时确保钩子恢复，避免捕获中途返回导致呼出键永久失效
        App.Main?.KeyboardHook.SetSuspended(false);
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready || IsCapturing) return;

        if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft)) Move(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight)) Move(1);
        else if (buttons.HasFlag(GamepadButtons.A)) ActivateCapture();
    }

    /// <summary>键盘等价操作：方向键=整卡切换，Enter/空格=捕获当前卡片键位。
    /// 捕获键盘呼出键时改由 OnKeyDown/OnKeyUp 直接记录按键（钩子已暂停，事件正常到达本页）。</summary>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsCapturing)
        {
            if (_target == CaptureTarget.KeySummon) CaptureKeyDown(e);
            return;
        }
        if (!_ready) return;
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

    /// <summary>键盘呼出键捕获：修饰键累积，主键记录并在松开时保存</summary>
    private void CaptureKeyDown(KeyRoutedEventArgs e)
    {
        var vk = (int)e.Key;
        var mod = KeySendService.ModFlag(vk);
        if (mod != 0)
        {
            // 修饰键：只累积，等主键按下时一并快照
            _liveMods |= mod;
            e.Handled = true;
            return;
        }
        _capturedKey = vk;
        _capturedMods = _liveMods;
        e.Handled = true;
        KeyCaptureText.Text = KeySendService.Describe(_capturedKey, _capturedMods) + "（松开即保存）";
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        if (!IsCapturing || _target != CaptureTarget.KeySummon) return;
        var vk = (int)e.Key;
        var mod = KeySendService.ModFlag(vk);
        if (mod != 0)
        {
            _liveMods &= ~mod;
            e.Handled = true;
            return;
        }
        if (vk == _capturedKey && _capturedKey != 0)
        {
            e.Handled = true;
            FinishKeyboardCapture();
        }
    }

    /// <summary>A 键：捕获当前选中卡片对应的键位（呼出键/键盘呼出键/全屏键之外均为只读说明卡，无动作）</summary>
    private void ActivateCapture()
    {
        if (_focus == 0) StartCapture(CaptureTarget.Summon);
        else if (_focus == 1) StartCapture(CaptureTarget.KeySummon);
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

    /// <summary>鼠标点击键盘呼出键位按钮</summary>
    private void OnKeyCaptureClick(object sender, RoutedEventArgs e) => StartCapture(CaptureTarget.KeySummon);

    /// <summary>鼠标点击全屏键位按钮</summary>
    private void OnFsCaptureClick(object sender, RoutedEventArgs e) => StartCapture(CaptureTarget.Fullscreen);

    /// <summary>进入捕获模式：等待用户按下组合键并松开。
    /// 捕获期间暂停全局键盘钩子，避免正在捕获的按键被钩子吞掉。</summary>
    private void StartCapture(CaptureTarget target)
    {
        if (IsCapturing) return;
        IsCapturing = true;
        _target = target;
        _captured = GamepadButtons.None;
        _capturedKey = 0;
        _capturedMods = 0;
        _liveMods = 0;
        App.Main?.KeyboardHook.SetSuspended(true);
        if (target == CaptureTarget.KeySummon)
        {
            SetCaptureText("请按下想要的按键…");
            Focus(FocusState.Programmatic); // 键盘捕获需页面持有焦点才能收到按键事件
        }
        else SetCaptureText("请按住组合键…");
    }

    private void SetCaptureText(string text)
    {
        switch (_target)
        {
            case CaptureTarget.Summon: CaptureText.Text = text; break;
            case CaptureTarget.KeySummon: KeyCaptureText.Text = text; break;
            default: FsCaptureText.Text = text; break;
        }
    }

    /// <summary>键盘呼出键捕获完成：保存并立即生效</summary>
    private void FinishKeyboardCapture()
    {
        if (_capturedKey == 0) return;
        IsCapturing = false;
        App.Main?.KeyboardHook.SetSuspended(false);
        KeyCaptureText.Text = KeySendService.Describe(_capturedKey, _capturedMods);
        SettingsService.Update(s =>
        {
            s.KeyboardSummonKey = _capturedKey;
            s.KeyboardSummonMods = _capturedMods;
        });
        App.Main?.SyncKeyboardHook();
    }

    /// <summary>捕获模式下累积按下的按键（MainWindow 转发边沿）；键盘呼出键不走此路径</summary>
    public void CaptureButtons(GamepadButtons pressed)
    {
        if (!IsCapturing || _target == CaptureTarget.KeySummon) return;
        _captured |= pressed;
        var desc = GamepadService.Describe(_captured);
        SetCaptureText(desc == "未设置" ? "请按住组合键…" : desc);
    }

    /// <summary>所有键松开 → 结束捕获并保存（手柄呼出/全屏键；键盘呼出键由 OnKeyUp 收尾）</summary>
    public void NotifyAllReleased()
    {
        if (!IsCapturing || _target == CaptureTarget.KeySummon) return;
        if (_captured != GamepadButtons.None)
        {
            IsCapturing = false;
            App.Main?.KeyboardHook.SetSuspended(false);
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
