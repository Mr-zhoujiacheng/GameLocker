using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>控制设置：上半区「快捷映射」= 呼出组合键 / 键盘呼出键 / 全屏组合键捕获 + 恢复默认；下半区「基本操作」= 简化后的通用操作说明。
/// 手柄：方向键按行上下切换（快捷映射逐行选中，基本操作整卡选中），A=捕获当前行的键位 / 恢复默认，
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

    /// <summary>手柄焦点：0=呼出键 1=键盘呼出键 2=全屏键 3=恢复默认 4=基本操作卡</summary>
    private int _focus;

    private Border[] _cards = default!;

    private const int BasicFocus = 4;

    private readonly Brush _cardIdleBg = CardGradient(0x22, 0x33);
    private readonly Brush _cardFocusBg = CardGradient(0x2E, 0x4D);
    private readonly Brush _lineIdle = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private readonly Brush _lineFocus = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    /// <summary>卡片底色：白→浅灰竖向渐变（与关于页同一套灰阶）</summary>
    private static LinearGradientBrush CardGradient(byte topAlpha, byte bottomAlpha) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop
            {
                Color = Windows.UI.Color.FromArgb(topAlpha, 0xFF, 0xFF, 0xFF), Offset = 0
            },
            new GradientStop
            {
                Color = Windows.UI.Color.FromArgb(bottomAlpha, 0xB7, 0xE6, 0xB0), Offset = 1
            }
        }
    };

    public KeybindPage()
    {
        InitializeComponent();
        CaptureText.Text = GamepadService.Describe(SettingsService.Current.SummonCombo);
        KeyCaptureText.Text = KeySendService.MappingLabel(0, SettingsService.Current.KeyboardSummonKey,
            SettingsService.Current.KeyboardSummonMods);
        FsCaptureText.Text = GamepadService.Describe(SettingsService.Current.FullscreenCombo);

        // 说明数据源：只保留一份"基本操作"（快捷映射与说明分开，不细分到每个页面）
        LvBasic.ItemsSource = ControlHelp.Basic;

        _cards = [CardSummon, CardKeySummon, CardFullscreen, CardReset, CardBasic];
        ApplyCardFocus();

        // 快捷映射四张卡：宽窗口两列，侧边栏窄窗口回成一列
        PageScroller.SizeChanged += (_, _) => LayoutCards();

        // 布局完成后再复位：OnNavigatedTo 时元素尚未排列，滚动偏移不可用
        Loaded += (_, _) =>
        {
            LayoutCards();
            _focus = 0;
            ApplyCardFocus();
            PageScroller.ScrollToVerticalOffset(0);
            Focus(FocusState.Programmatic); // 键盘方向键等价于手柄方向键（整卡切换）
        };
    }

    private const double TwoColumnMinWidth = 520;

    /// <summary>当前是否为两列布局（决定方向键的移动规则）</summary>
    private bool _twoColumn;

    /// <summary>按可用宽度重排快捷映射：宽=两列两行，窄=单列四行</summary>
    private void LayoutCards()
    {
        if (MapGrid is null || PageScroller is null) return;
        var avail = PageScroller.ViewportWidth;
        if (avail <= 0) return;
        var two = avail >= TwoColumnMinWidth;
        _twoColumn = two;

        MapGrid.ColumnDefinitions[1].Width = two
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        for (var i = 0; i < 4; i++)
        {
            Grid.SetRow(_cards[i], two ? i / 2 : i);
            Grid.SetColumn(_cards[i], two ? i % 2 : 0);
        }
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

        // 两列布局下方向键按"上下走行、左右走列"移动，不能把左右合并到上下
        if (buttons.HasFlag(GamepadButtons.DPadUp)) Move(MoveDirection.Up);
        else if (buttons.HasFlag(GamepadButtons.DPadDown)) Move(MoveDirection.Down);
        else if (buttons.HasFlag(GamepadButtons.DPadLeft)) Move(MoveDirection.Left);
        else if (buttons.HasFlag(GamepadButtons.DPadRight)) Move(MoveDirection.Right);
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
                Move(MoveDirection.Up);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Down:
                Move(MoveDirection.Down);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left:
                Move(MoveDirection.Left);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                Move(MoveDirection.Right);
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

    /// <summary>A 键：捕获当前卡片的键位；"恢复默认"卡则还原全部键位</summary>
    private void ActivateCapture()
    {
        switch (_focus)
        {
            case 0: StartCapture(CaptureTarget.Summon); break;
            case 1: StartCapture(CaptureTarget.KeySummon); break;
            case 2: StartCapture(CaptureTarget.Fullscreen); break;
            case 3: ResetDefaults(); break;
        }
    }

    private enum MoveDirection { Up, Down, Left, Right }

    /// <summary>方向键移动：两列布局下 ↑↓ 走同一列的上一行/下一行，←→ 走同行的左右；
    /// 单列布局下只有 ↑↓ 有效。基本操作卡是整行卡，上下移动都落在它上面。</summary>
    private void Move(MoveDirection dir)
    {
        var next = _focus;
        if (_twoColumn)
        {
            switch (dir)
            {
                case MoveDirection.Up:
                    if (_focus == BasicFocus) next = 2;
                    else if (_focus >= 2) next = _focus - 2;
                    break;
                case MoveDirection.Down:
                    if (_focus < 2) next = _focus + 2;
                    else if (_focus < BasicFocus) next = BasicFocus;
                    break;
                case MoveDirection.Left:
                    if (_focus is 1 or 3) next = _focus - 1;
                    break;
                case MoveDirection.Right:
                    if (_focus is 0 or 2) next = _focus + 1;
                    break;
            }
        }
        else if (dir is MoveDirection.Up or MoveDirection.Down)
        {
            next = Math.Clamp(_focus + (dir == MoveDirection.Up ? -1 : 1), 0, BasicFocus);
        }

        if (next == _focus) return;
        _focus = next;
        ApplyCardFocus();
        EnsureCardVisible(_cards[_focus]);
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

    /// <summary>确保当前卡片完整可见：只在它超出可视区时才滚最小距离（留 8px 余量），
    /// 和关于页/外观页一样不做"滚到顶部"，避免每按一次方向键整页跳动</summary>
    private void EnsureCardVisible(FrameworkElement card)
    {
        try
        {
            var p = card.TransformToVisual(PageScroller)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var vh = PageScroller.ViewportHeight;
            if (vh <= 0) return;
            if (p.Y < 0)
                PageScroller.ChangeView(null, Math.Max(0, PageScroller.VerticalOffset + p.Y - 8), null);
            else if (p.Y + card.ActualHeight > vh)
                PageScroller.ChangeView(null, PageScroller.VerticalOffset + p.Y + card.ActualHeight - vh + 8, null);
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

    /// <summary>鼠标点击"恢复默认键位"</summary>
    private void OnResetClick(object sender, RoutedEventArgs e) => ResetDefaults();

    /// <summary>还原全部键位到初始值（呼出=LT+RT，键盘=F7，全屏=菜单键），保存并立即生效</summary>
    private void ResetDefaults()
    {
        if (IsCapturing) return;
        var d = new GameLocker.Models.AppSettings();   // 直接取模型里的默认值，避免硬编码
        SettingsService.Update(s =>
        {
            s.SummonCombo = d.SummonCombo;
            s.FullscreenCombo = d.FullscreenCombo;
            s.KeyboardSummonKey = d.KeyboardSummonKey;
            s.KeyboardSummonMods = d.KeyboardSummonMods;
        });
        App.Main?.Gamepad.SetSummonCombo(d.SummonCombo);
        App.Main?.Gamepad.SetFullscreenCombo(d.FullscreenCombo);
        App.Main?.SyncKeyboardHook();
        CaptureText.Text = GamepadService.Describe(d.SummonCombo);
        KeyCaptureText.Text = KeySendService.MappingLabel(0, d.KeyboardSummonKey, d.KeyboardSummonMods);
        FsCaptureText.Text = GamepadService.Describe(d.FullscreenCombo);
    }

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
