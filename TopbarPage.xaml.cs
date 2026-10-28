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

        _switchRows =
        [
            (ClockRow, ClockToggle),
            (BatteryRow, BatteryToggle),
            (WifiRow, WifiToggle),
            (BluetoothRow, BluetoothToggle),
            (GamepadRow, GamepadIconToggle)
        ];
        _controls = [ClockRow, BatteryRow, WifiRow, BluetoothRow, GamepadRow];

        // 卡片网格：宽窗口两列并整块居中，侧边栏窄窗口回成一列靠上
        ContentScroll.SizeChanged += (_, _) => LayoutCards();
        Loaded += (_, _) => LayoutCards();
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
        SwitchGrid.VerticalAlignment = two ? VerticalAlignment.Center : VerticalAlignment.Top;

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

        if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft))
            MoveRow(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight))
            MoveRow(1);
        else if (buttons.HasFlag(GamepadButtons.A))
            ActivateCurrent();
    }

    /// <summary>上下移动：单列时逐个移动，两列时左右键在同行内换列、上下键换行</summary>
    private void MoveRow(int delta)
    {
        var two = SwitchGrid.ColumnDefinitions[1].Width.Value > 0;
        if (!two)
        {
            var next = Math.Clamp(_index + delta, 0, _controls.Length - 1);
            if (next == _index) return;
            _index = next;
            FocusIndex(next);
            return;
        }

        // 两列：行内左右（±1），跨行（±2）
        var row = _index / 2;
        var col = _index % 2;
        var rows = (_controls.Length + 1) / 2;
        if (delta == -1 && col == 1) { _index--; FocusIndex(_index); return; }
        if (delta == 1 && col == 0 && _index + 1 < _controls.Length) { _index++; FocusIndex(_index); return; }

        var targetRow = Math.Clamp(row + (delta < 0 ? -1 : 1), 0, rows - 1);
        if (targetRow == row) return;
        var target = Math.Min(targetRow * 2 + col, _controls.Length - 1);
        _index = target;
        FocusIndex(target);
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
        });
        // 即时应用到主页顶栏（若已创建）
        App.Main?.ApplyTopbarVisibility();
    }

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
