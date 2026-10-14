using GameLocker.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 顶栏设置：时钟 / 电池 / 无线网络 / 蓝牙 / 手柄连接图标显示开关。
/// 手柄：方向=移动焦点，A=切换开关，View(Select)/B=返回设置主页。
/// </summary>
public sealed partial class TopbarPage : Page, ISettingsGamepad
{
    private Control[] _controls = [];
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

        _controls = [ClockToggle, BatteryToggle, WifiToggle, BluetoothToggle, GamepadIconToggle];
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
            Move(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight))
            Move(1);
        else if (buttons.HasFlag(GamepadButtons.A))
            ActivateCurrent();
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
        if (_controls[_index] is ToggleSwitch ts)
            ts.IsOn = !ts.IsOn;
    }

    private void FocusIndex(int i)
    {
        var c = _controls[i];
        // 开关：移动只移动光标（高亮描边），不聚焦；按 A 才切换并保存
        UpdateCursor(c);
        EnsureVisible(c);
    }

    private void UpdateCursor(Control? cursor)
    {
        foreach (var ctl in _controls)
        {
            if (ctl is not ToggleSwitch ts) continue;
            var on = ReferenceEquals(ts, cursor);
            ts.BorderBrush = on
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void EnsureVisible(Control c)
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
