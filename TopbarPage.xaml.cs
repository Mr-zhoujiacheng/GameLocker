using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 顶栏设置：时钟 / 电池 / 无线网络 / 蓝牙 / 手柄连接图标显示开关。
/// 手柄：方向=移动焦点，A=切换开关，View(Select)/B=返回设置主页。
/// </summary>
public sealed partial class TopbarPage : Page, ISettingsGamepad
{
    /// <summary>可导航的开关卡片（一卡一开关）</summary>
    private Button[] _controls = [];
    /// <summary>卡片 → 对应开关（按 A / 点击时切换真正状态）</summary>
    private (Button row, ToggleSwitch toggle)[] _switchRows = [];
    private int _index;
    private bool _ready;

    public TopbarPage()
    {
        InitializeComponent();

        var s = SettingsService.Current;
        ClockToggle.IsOn = s.ShowClock;
        BatteryToggle.IsOn = s.ShowBattery;
        WifiToggle.IsOn = s.ShowWifi;
        BluetoothToggle.IsOn = s.ShowBluetooth;
        GamepadIconToggle.IsOn = s.ShowGamepadIcon;
        NotificationToggle.IsOn = s.ShowNotifications;

        _switchRows =
        [
            (ClockRow, ClockToggle),
            (BatteryRow, BatteryToggle),
            (WifiRow, WifiToggle),
            (BluetoothRow, BluetoothToggle),
            (GamepadRow, GamepadIconToggle),
            (NotificationRow, NotificationToggle)
        ];
        _controls = [ClockRow, BatteryRow, WifiRow, BluetoothRow, GamepadRow, NotificationRow];

        // 卡片网格：宽窗口两列并整块居中，侧边栏窄窗口回成一列靠上
        ContentScroll.SizeChanged += (_, _) => LayoutCards();
        Loaded += (_, _) =>
        {
            LayoutCards();
            UpdatePreview();
        };
    }

    /// <summary>
    /// 顶栏预览：用和主页顶栏一模一样的圆角气泡，按当前开关 + 真实设备状态演示效果。
    /// 拨动任一开关（A 键/点击）都会重新调用，所以能立刻看到图标出现/消失。
    /// </summary>
    private async void UpdatePreview()
    {
        var s = SettingsService.Current;

        // 左：时钟用当前时间充当示例
        var now = DateTime.Now;
        PreviewClockText.Text = now.ToString("HH:mm");
        PreviewDateText.Text = $"{now:M月d日} {"日一二三四五六"[(int)now.DayOfWeek]}";
        PreviewClock.Visibility = s.ShowClock ? Visibility.Visible : Visibility.Collapsed;

        // 右：四枚状态气泡，颜色规则与主页一致（Wi-Fi/蓝牙按无线电开关状态着色）
        var wifiOn = await RadioService.IsOnAsync(Windows.Devices.Radios.RadioKind.WiFi)
                     ?? NetworkService.IsWifiConnected();
        var btOn = await RadioService.IsOnAsync(Windows.Devices.Radios.RadioKind.Bluetooth)
                   ?? NetworkService.IsBluetoothConnected();
        UpdateStatusIcon(PreviewWifiBubble, PreviewWifiIcon, s.ShowWifi, wifiOn);
        UpdateStatusIcon(PreviewBluetoothBubble, PreviewBluetoothIcon, s.ShowBluetooth, btOn);

        var pct = BatteryService.GetPercent();
        var showBattery = s.ShowBattery && pct is not null;
        PreviewBatteryBubble.Visibility = showBattery ? Visibility.Visible : Visibility.Collapsed;
        if (showBattery)
        {
            var (glyph, color) = BatteryService.Describe(pct!.Value);
            PreviewBatteryIcon.Glyph = glyph;
            PreviewBatteryIcon.Foreground = new SolidColorBrush(color);
        }

        PreviewGamepadBubble.Visibility = s.ShowGamepadIcon ? Visibility.Visible : Visibility.Collapsed;
        PreviewGamepadIcon.Foreground = new SolidColorBrush(
            App.Main?.Gamepad.HasGamepad == true
                ? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B)
                : Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x50, 0x4A));

        PreviewNotificationBubble.Visibility = s.ShowNotifications ? Visibility.Visible : Visibility.Collapsed;

        // 五枚状态图标全关时，外面的玻璃框一起隐藏
        var anyStatus = s.ShowWifi || s.ShowBluetooth || showBattery || s.ShowGamepadIcon || s.ShowNotifications;
        PreviewTray.Visibility = anyStatus ? Visibility.Visible : Visibility.Collapsed;

        // 顶栏各项全关时给个说明，避免预览条只剩一条空底
        var anyShown = s.ShowClock || s.ShowWifi || s.ShowBluetooth || showBattery
                       || s.ShowGamepadIcon || s.ShowNotifications;
        PreviewEmpty.Visibility = anyShown ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void UpdateStatusIcon(Border bubble, FontIcon icon, bool show, bool on)
    {
        bubble.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        icon.Foreground = new SolidColorBrush(on
            ? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B)   // 绿=开着
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)); // 白=关着
        icon.Opacity = on ? 1.0 : 0.35;
    }

    /// <summary>可否排两列的最小可用宽度</summary>
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
        // 整块（预览 + 开关卡）一起居中/靠上，预览与开关宽度一致、紧挨着
        ContentStack.VerticalAlignment = two ? VerticalAlignment.Center : VerticalAlignment.Top;

        for (var i = 0; i < _controls.Length; i++)
        {
            Grid.SetRow(_controls[i], two ? i / 2 : i);
            Grid.SetColumn(_controls[i], two ? i % 2 : 0);
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
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready) return;

        // 宽窗口两列：左右在同行内换列、上下换行；窄窗口单列：四个方向都逐项移动
        if (buttons.HasFlag(GamepadButtons.DPadUp)) MoveBy(0, -1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown)) MoveBy(0, 1);
        else if (buttons.HasFlag(GamepadButtons.DPadLeft)) MoveBy(-1, 0);
        else if (buttons.HasFlag(GamepadButtons.DPadRight)) MoveBy(1, 0);
        else if (buttons.HasFlag(GamepadButtons.A))
            ActivateCurrent();
    }

    private void MoveBy(int dx, int dy)
    {
        var two = SwitchGrid.ColumnDefinitions[1].Width.Value > 0;

        if (!two)
        {
            var step = dx != 0 ? dx : dy;
            var next = Math.Clamp(_index + step, 0, _controls.Length - 1);
            if (next == _index) return;
            _index = next;
            FocusIndex(next);
            return;
        }

        var row = _index / 2;
        var col = _index % 2;

        if (dx != 0)
        {
            var nc = col + dx;
            if (nc < 0 || nc > 1) return;
            var target = row * 2 + nc;
            if (target >= _controls.Length) return;
            _index = target;
            FocusIndex(target);
            return;
        }

        var rows = (_controls.Length + 1) / 2;
        var targetRow = row + dy;
        if (targetRow < 0 || targetRow >= rows) return;
        var targetIndex = Math.Min(targetRow * 2 + col, _controls.Length - 1);
        if (targetIndex == _index) return;
        _index = targetIndex;
        FocusIndex(targetIndex);
    }

    private void ActivateCurrent()
    {
        var row = _controls[_index];
        foreach (var (card, toggle) in _switchRows)
        {
            if (!ReferenceEquals(card, row)) continue;
            toggle.IsOn = !toggle.IsOn;
            return;
        }
    }

    private void FocusIndex(int i)
    {
        var c = _controls[i];
        // 卡片：移动只移动光标（白框），不聚焦；按 A 才切换并保存
        UpdateCursor(c);
        EnsureVisible(c);
    }

    private void UpdateCursor(Control? cursor)
    {
        foreach (var ctl in _controls)
        {
            var on = ReferenceEquals(ctl, cursor);
            ctl.BorderBrush = on
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void EnsureVisible(FrameworkElement c)
    {
        try
        {
            var p = c.TransformToVisual(ContentScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            var vh = ContentScroll.ViewportHeight;
            if (p.Y < 0)
                ContentScroll.ChangeView(null, ContentScroll.VerticalOffset + p.Y - 8, null);
            else if (p.Y + c.ActualHeight > vh)
                ContentScroll.ChangeView(null, ContentScroll.VerticalOffset + p.Y + c.ActualHeight - vh + 8, null);
        }
        catch { }
    }

    private void OnBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        App.Main?.ShowSettingsHub();

    /// <summary>点击卡片：选中并把焦点光标移过来，同时切换开关</summary>
    private void OnRowClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button row) return;
        var i = Array.IndexOf(_controls, row);
        if (i >= 0) FocusIndex(i);
        ActivateCurrent();
    }

    private void OnToggleChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s =>
        {
            s.ShowClock = ClockToggle.IsOn;
            s.ShowBattery = BatteryToggle.IsOn;
            s.ShowWifi = WifiToggle.IsOn;
            s.ShowBluetooth = BluetoothToggle.IsOn;
            s.ShowGamepadIcon = GamepadIconToggle.IsOn;
            s.ShowNotifications = NotificationToggle.IsOn;
        });
        // 即时应用到主页顶栏（若已创建），并刷新本页预览
        App.Main?.ApplyTopbarVisibility();
        UpdatePreview();
    }

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
