using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;
using Windows.System;

namespace GameLocker;

/// <summary>
/// 顶栏2 的控制中心弹层：Wi-Fi / 蓝牙 / 夜间模式 / 静音 + 音量/亮度滑杆。
/// 独立可激活小窗（与 WS_EX_NOACTIVATE 的顶栏不同），失焦自动收起；
/// 位置由顶栏在 ShowAt 里给出，贴右边缘、顶栏正下方。
/// </summary>
public sealed partial class StatusBarControlCenterWindow : Window
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private static StatusBarControlCenterWindow? _current;

    private readonly AlwaysActiveAcrylicBackdrop _acrylic = new();
    private readonly DispatcherTimer _brightnessDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private bool _visible;
    private Storyboard? _storyboard;
    private bool _updating;
    private bool _autoHide;
    private DateTime _shownAt;
    private bool _expanded;
    private bool _upward;
    private int _currentHeightPx;
    private int _x, _y, _widthPx, _collapsedHeightPx, _expandedHeightPx;

    private readonly List<string> _topIds = ["wifi", "bluetooth", "night", "mute"];
    private readonly List<string> _extraIds = ["screenshot", "theme", "hotspot", "airplane", "energy", "restart"];
    private string? _dragId;
    private Button? _dragSource;
    private Windows.Foundation.Point _dragStart;
    private bool _dragging;
    private bool _invokingAction;
    private CompositeTransform? _dragTransform;

    public StatusBarControlCenterWindow()
    {
        InitializeComponent();
        Title = "GameLocker Control Center";
        ApplyChrome();
        LoadLayout();
        RelayoutTiles();
        AttachTilePointerHandlers();
        SystemBackdrop = _acrylic;

        // 滑杆统一用顶栏的青色
        var accent = Windows.UI.Color.FromArgb(0xFF, 0x7A, 0xD9, 0xC4);
        foreach (var slider in new[] { VolumeSlider, BrightnessSlider })
        {
            slider.Resources["SliderThumbBackground"] = new SolidColorBrush(accent);
            slider.Resources["SliderThumbBackgroundPointerOver"] = new SolidColorBrush(accent);
            slider.Resources["SliderThumbBackgroundPressed"] = new SolidColorBrush(accent);
            slider.Resources["SliderOuterThumbBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            slider.Resources["SliderThumbBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            slider.Resources["SliderTrackValueFill"] = new SolidColorBrush(accent);
            slider.Resources["SliderTrackValueFillPointerOver"] = new SolidColorBrush(accent);
            slider.Resources["SliderTrackValueFillPressed"] = new SolidColorBrush(accent);
            slider.Resources["SliderTrackFill"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }

        _brightnessDebounce.Tick += async (_, _) =>
        {
            _brightnessDebounce.Stop();
            if (_updating) return;
            var value = (int)Math.Round(BrightnessSlider.Value);
            BrightnessText.Text = $"{value}%";
            await SystemPanelService.SetBrightnessPercentAsync(value);
        };

        // 预览模式下不因失焦自动收起，方便截图；正常使用时失焦即收起
        _autoHide = Array.IndexOf(Environment.GetCommandLineArgs(), "--control-center-preview") < 0;

        Activated += (_, e) =>
        {
            if (!_autoHide) return;
            if (e.WindowActivationState != WindowActivationState.Deactivated) return;
            // 刚显示时可能因前台限制立刻失焦一次，300ms 内不收起，避免闪一下就没了
            if (DateTime.Now - _shownAt < TimeSpan.FromMilliseconds(300)) return;
            HidePanel();
        };
    }

    /// <summary>打开/收起。right 是顶栏右边缘，barTop/barBottom 是顶栏上下边缘。</summary>
    public static void Toggle(int right, int barTop, int barBottom, bool upward)
    {
        _current ??= new StatusBarControlCenterWindow();
        if (_current._visible || IsWindowVisible(_current.Hwnd)) _current.HidePanel();
        else _current.ShowAt(right, barTop, barBottom, upward);
    }

    public static void CloseIfOpen()
    {
        _current?.HidePanel();
    }

    private void ShowAt(int right, int barTop, int barBottom, bool upward)
    {
        _upward = upward;
        var scale = GetDpiForWindow(Hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        var width = (int)Math.Round(380 * scale);
        _collapsedHeightPx = (int)Math.Round(420 * scale);
        _expandedHeightPx = (int)Math.Round(584 * scale);
        var height = _expanded ? _expandedHeightPx : _collapsedHeightPx;
        var x = right - width - (int)Math.Round(10 * scale);
        var y = upward
            ? barTop - height - (int)Math.Round(8 * scale)
            : barBottom + (int)Math.Round(8 * scale);
        var monitor = MonitorFromWindow(Hwnd, 2);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                x = Math.Clamp(x, info.rcWork.Left, Math.Max(info.rcWork.Left, info.rcWork.Right - width));
                y = Math.Clamp(y, info.rcWork.Top, Math.Max(info.rcWork.Top, info.rcWork.Bottom - height));
            }
        }

        _x = x;
        _y = y;
        _widthPx = width;
        _currentHeightPx = height;
        SetWindowPos(Hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
        _storyboard?.Stop();
        _storyboard = null;
        RootTransform.TranslateY = _upward ? 24 : -24;
        Root.Opacity = 0;
        ShowWindow(Hwnd, 5); // SW_SHOW
        Activate();
        PlayShowAnimation();
        _visible = true;
        _shownAt = DateTime.Now;
        _ = RefreshAsync();
        if (_expanded) _ = RefreshExtrasAsync();
    }

    private void PlayShowAnimation()
    {
        var slide = new DoubleAnimation
        {
            From = _upward ? 24 : -24,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, RootTransform);
        Storyboard.SetTargetProperty(slide, "TranslateY");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        _storyboard = storyboard;
        try
        {
            storyboard.Begin();
        }
        catch
        {
            RootTransform.TranslateY = 0;
            Root.Opacity = 1;
        }
    }

    private void HidePanel()
    {
        if (!_visible)
        {
            if (IsWindowVisible(Hwnd)) ShowWindow(Hwnd, 0);
            return;
        }

        _visible = false;
        var slide = new DoubleAnimation
        {
            From = RootTransform.TranslateY,
            To = _upward ? 24 : -24,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(slide, RootTransform);
        Storyboard.SetTargetProperty(slide, "TranslateY");

        var fade = new DoubleAnimation
        {
            From = Root.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        _storyboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_storyboard, storyboard)) return;
            _storyboard = null;
            ShowWindow(Hwnd, 0);
            RootTransform.TranslateY = 0;
            Root.Opacity = 1;
        };
        storyboard.Begin();
    }

    private async Task RefreshAsync()
    {
        _updating = true;
        try
        {
            await SystemPanelService.RefreshRadiosAsync();
            var volume = SystemPanelService.GetVolumeState();
            if (volume is not null)
            {
                VolumeSlider.IsEnabled = true;
                VolumeSlider.Value = volume.Value.Percent;
                VolumeText.Text = volume.Value.Mute ? "静音" : $"{volume.Value.Percent}%";
            }
            else
            {
                VolumeSlider.IsEnabled = false;
                VolumeText.Text = "—";
            }

            var brightness = await SystemPanelService.ReadBrightnessPercentAsync();
            if (brightness is not null)
            {
                BrightnessSlider.IsEnabled = true;
                BrightnessSlider.Value = brightness.Value;
                BrightnessText.Text = $"{brightness.Value}%";
            }
            else
            {
                BrightnessSlider.IsEnabled = false;
                BrightnessText.Text = "不支持";
            }

            UpdateTile(WifiTile, WifiState, SystemPanelService.Reading(SystemPanel.Wifi));
            UpdateTile(BluetoothTile, BluetoothState, SystemPanelService.Reading(SystemPanel.Bluetooth));
            UpdateTile(NightTile, NightState, SystemPanelService.NightLightOn ? "已开启" : "已关闭",
                SystemPanelService.NightLightOn);
            UpdateTile(MuteTile, MuteState, volume?.Mute == true ? "已静音" : "未静音",
                volume?.Mute == true);
        }
        finally
        {
            _updating = false;
        }
    }

    private static void UpdateTile(Button tile, TextBlock stateText, string state, bool? on = null)
    {
        stateText.Text = state;
        var enabled = on ?? state.Contains("已开启") || state.Contains("已连接");
        tile.BorderBrush = new SolidColorBrush(enabled
            ? Windows.UI.Color.FromArgb(0x80, 0x7A, 0xD9, 0xC4)
            : Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    }

    private async void OnWifiClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        await ToggleAsync(SystemPanel.Wifi);
    }

    private async void OnBluetoothClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        await ToggleAsync(SystemPanel.Bluetooth);
    }

    private async void OnNightClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        await ToggleAsync(SystemPanel.NightLight);
    }

    private async void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        await ToggleAsync(SystemPanel.Volume);
    }

    private async Task ToggleAsync(SystemPanel panel)
    {
        var message = await SystemPanelService.ToggleAsync(panel);
        DebugLog.Write($"[控制中心] {SystemPanelService.Label(panel)}：{message}");
        await RefreshAsync();
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        var value = (int)Math.Round(VolumeSlider.Value);
        VolumeText.Text = $"{value}%";
        SystemPanelService.SetVolumePercent(value);
        var state = SystemPanelService.GetVolumeState();
        if (state is not null)
            UpdateTile(MuteTile, MuteState, state.Value.Mute ? "已静音" : "未静音", state.Value.Mute);
    }

    private void OnBrightnessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        BrightnessText.Text = $"{(int)Math.Round(BrightnessSlider.Value)}%";
        _brightnessDebounce.Stop();
        _brightnessDebounce.Start();
    }

    private Button? TileById(string id) => id switch
    {
        "wifi" => WifiTile,
        "bluetooth" => BluetoothTile,
        "night" => NightTile,
        "mute" => MuteTile,
        "screenshot" => ScreenshotTile,
        "theme" => ThemeTile,
        "hotspot" => HotspotTile,
        "airplane" => AirplaneTile,
        "energy" => EnergyTile,
        "restart" => RestartTile,
        _ => null
    };

    private void LoadLayout()
    {
        var saved = SettingsService.Current.StatusBarControlCenterLayout;
        if (string.IsNullOrWhiteSpace(saved)) return;
        var parts = saved.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 8) return;
        if (parts.Any(p => TileById(p) is null)) return;
        if (parts.Distinct().Count() != parts.Length) return;

        _topIds.Clear();
        _extraIds.Clear();
        for (var i = 0; i < 4; i++) _topIds.Add(parts[i]);
        for (var i = 4; i < parts.Length && _extraIds.Count < 8; i++) _extraIds.Add(parts[i]);

        // 旧布局只有 8 个控件，补齐后来新增的控件
        foreach (var id in new[] { "energy", "restart" })
        {
            if (_topIds.Contains(id) || _extraIds.Contains(id)) continue;
            _extraIds.Add(id);
        }
    }

    private void SaveLayout()
    {
        var value = string.Join(';', _topIds.Concat(_extraIds));
        SettingsService.Update(s => s.StatusBarControlCenterLayout = value);
    }

    private void RelayoutTiles()
    {
        TopTilesGrid.Children.Clear();
        MorePanel.Children.Clear();

        for (var i = 0; i < _topIds.Count && i < 4; i++)
        {
            if (TileById(_topIds[i]) is not { } tile) continue;
            tile.Style = Root.Resources["TileStyle"] as Style;
            Grid.SetRow(tile, i / 2);            Grid.SetColumn(tile, i % 2);
            TopTilesGrid.Children.Add(tile);
        }

        for (var i = 0; i < _extraIds.Count && i < 8; i++)
        {
            if (TileById(_extraIds[i]) is not { } tile) continue;
            tile.Style = Root.Resources["TileStyle"] as Style;
            Grid.SetRow(tile, i / 4);
            Grid.SetColumn(tile, i % 4);
            MorePanel.Children.Add(tile);
        }
    }

    private void AttachTilePointerHandlers()
    {
        foreach (var id in _topIds.Concat(_extraIds))
        {
            if (TileById(id) is not { } tile) continue;
            tile.CanDrag = false;
            tile.AllowDrop = false;
            tile.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnTilePointerPressed), handledEventsToo: true);
            tile.AddHandler(UIElement.PointerMovedEvent,
                new PointerEventHandler(OnTilePointerMoved), handledEventsToo: true);
            tile.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnTilePointerReleased), handledEventsToo: true);
        }
    }

    private void OnTilePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string) return;
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragSource = button;
        _dragStart = point.Position;
        _dragging = false;
        _dragTransform = new CompositeTransform();
        button.RenderTransform = _dragTransform;
    }

    private void OnTilePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragSource is null || _dragTransform is null) return;
        var position = e.GetCurrentPoint(Root).Position;
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStart.X) < 8 && Math.Abs(position.Y - _dragStart.Y) < 8) return;
            _dragging = true;
            _dragSource.Opacity = 0.86;
            _dragTransform.ScaleX = 1.06;
            _dragTransform.ScaleY = 1.06;
            Canvas.SetZIndex(_dragSource, 1000);
        }

        _dragTransform.TranslateX = position.X - _dragStart.X;
        _dragTransform.TranslateY = position.Y - _dragStart.Y;
    }

    private void OnTilePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragSource is not { } source) return;
        var wasDragging = _dragging;
        var sourceId = source.Tag as string;
        var position = e.GetCurrentPoint(Root).Position;
        var moved = Math.Abs(position.X - _dragStart.X) > 8 || Math.Abs(position.Y - _dragStart.Y) > 8;

        _dragSource = null;
        _dragging = false;
        _dragTransform = null;
        source.Opacity = 1;
        Canvas.SetZIndex(source, 0);
        source.RenderTransform = new CompositeTransform();

        if (!wasDragging && !moved)
        {
            // 所有控件动作都由这里触发；Button.Click 被永久抑制，避免拖拽松手误触
            if (sourceId is not null) ExecuteTileAction(sourceId);
            return;
        }

        if (sourceId is null) return;
        string? targetId = null;
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(position, Root))
        {
            if (element is not Button target || target == source || target.Tag is not string id) continue;
            if (id == sourceId) continue;
            targetId = id;
            break;
        }

        if (targetId is null) return;
        SwapTiles(sourceId, targetId);
        AnimateSwap(TileById(sourceId), TileById(targetId));
    }

    private void ExecuteTileAction(string id)
    {
        DebugLog.Write($"[控制中心] 执行控件 {id}");
        _invokingAction = true;
        try
        {
            switch (id)
            {
                case "wifi": OnWifiClick(null!, null!); break;
                case "bluetooth": OnBluetoothClick(null!, null!); break;
                case "night": OnNightClick(null!, null!); break;
                case "mute": OnMuteClick(null!, null!); break;
                case "screenshot": OnScreenshotClick(null!, null!); break;
                case "theme": OnThemeClick(null!, null!); break;
                case "hotspot": OnHotspotClick(null!, null!); break;
                case "airplane": OnAirplaneClick(null!, null!); break;
                case "energy": OnEnergyClick(null!, null!); break;
                case "restart": OnRestartClick(null!, null!); break;
            }
        }
        finally
        {
            _invokingAction = false;
        }
    }

    private static void AnimateSwap(Button? source, Button? target)
    {
        foreach (var tile in new[] { source, target })
        {
            if (tile is null) continue;
            var transform = new CompositeTransform { ScaleX = 0.9, ScaleY = 0.9 };
            tile.RenderTransform = transform;
            tile.Opacity = 0;
            var storyboard = new Storyboard();

            var scaleX = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(scaleX, transform);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");
            var scaleY = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(scaleY, transform);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");
            var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(140) };
            Storyboard.SetTarget(fade, tile);
            Storyboard.SetTargetProperty(fade, "Opacity");

            storyboard.Children.Add(scaleX);
            storyboard.Children.Add(scaleY);
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }
    }

    private bool SuppressTileClick() => !_invokingAction;

    private void OnTileDragStarting(object sender, DragStartingEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        _dragId = id;
        e.Data.Properties["id"] = id;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnTileDragOver(object sender, DragEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        if (_dragId is null || id == _dragId) return;
        e.AcceptedOperation = DataPackageOperation.Move;
    }

    private void OnTileDrop(object sender, DragEventArgs e)
    {
        if (_dragId is null) return;
        if (sender is not Button target || target.Tag is not string targetId) return;
        if (targetId == _dragId) return;
        SwapTiles(_dragId, targetId);
        _dragId = null;
    }

    private void SwapTiles(string sourceId, string targetId)
    {
        var sourceTop = _topIds.IndexOf(sourceId);
        var targetTop = _topIds.IndexOf(targetId);
        var sourceExtra = _extraIds.IndexOf(sourceId);
        var targetExtra = _extraIds.IndexOf(targetId);

        if (sourceTop >= 0 && targetTop >= 0)
            (_topIds[sourceTop], _topIds[targetTop]) = (_topIds[targetTop], _topIds[sourceTop]);
        else if (sourceExtra >= 0 && targetExtra >= 0)
            (_extraIds[sourceExtra], _extraIds[targetExtra]) = (_extraIds[targetExtra], _extraIds[sourceExtra]);
        else if (sourceTop >= 0 && targetExtra >= 0)
        {
            _topIds[sourceTop] = targetId;
            _extraIds[targetExtra] = sourceId;
        }
        else if (sourceExtra >= 0 && targetTop >= 0)
        {
            _extraIds[sourceExtra] = targetId;
            _topIds[targetTop] = sourceId;
        }
        else return;

        RelayoutTiles();
        SaveLayout();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        MorePanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        MoreChevron.Glyph = _expanded ? "\uE70E" : "\uE70D";

        var height = _expanded ? _expandedHeightPx : _collapsedHeightPx;
        if (_upward)
        {
            // 顶栏在底部：窗口底边保持不动，向上展开
            var bottom = _y + _currentHeightPx;
            _y = bottom - height;
        }
        _currentHeightPx = height;
        SetWindowPos(Hwnd, HWND_TOPMOST, _x, _y, _widthPx, height, SWP_NOACTIVATE);
        if (_expanded) _ = RefreshExtrasAsync();
    }

    private void OnScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        var ok = ScreenshotService.CaptureToClipboard();
        FloatingToastService.Show(ok ? "已截取当前屏幕并复制到剪贴板" : "截图失败");
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        var dark = ThemeService.Toggle();
        ThemeState.Text = dark ? "深色" : "浅色";
        ThemeIcon.Glyph = "\uE790";
    }

    private async void OnHotspotClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        var (ok, state) = await NetworkModeService.ToggleHotspotAsync();
        HotspotState.Text = ok ? state : "不可用";
        if (!ok) FloatingToastService.Show("热点切换失败：" + state);
    }

    private async void OnAirplaneClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        var (ok, on, state) = await NetworkModeService.ToggleAirplaneAsync();
        AirplaneState.Text = ok ? state : "不可用";
        if (!ok) FloatingToastService.Show("飞行模式切换失败：" + state);
    }

    private void OnEnergyClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        var enable = !EnergySaverService.IsEnergySaver();
        var ok = EnergySaverService.SetEnergySaver(enable);
        EnergyState.Text = ok ? (enable ? "已开启" : "已关闭") : "不可用";
        if (!ok) FloatingToastService.Show("节能模式切换失败");
    }

    private async Task RefreshExtrasAsync()
    {
        ThemeState.Text = ThemeService.IsDark() ? "深色" : "浅色";
        ThemeIcon.Glyph = "\uE790";
        HotspotState.Text = await NetworkModeService.IsHotspotOnAsync() ? "已开启" : "已关闭";
        AirplaneState.Text = await NetworkModeService.IsAirplaneOnAsync() ? "已开启" : "已关闭";
        EnergyState.Text = EnergySaverService.IsEnergySaver() ? "已开启" : "已关闭";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HidePanel();

    private void OnThisPcClick(object sender, RoutedEventArgs e)
    {
        HidePanel();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FloatingToastService.Show("打开我的电脑失败：" + ex.Message);
        }
    }

    private async void OnSystemSettingsClick(object sender, RoutedEventArgs e)
    {
        HidePanel();
        try { await Launcher.LaunchUriAsync(new Uri("ms-settings:")); }
        catch (Exception ex) { DebugLog.Write("[控制中心] 打开系统设置失败：" + ex.Message); }
    }

    private void OnLockClick(object sender, RoutedEventArgs e)
    {
        HidePanel();
        PowerService.Lock();
    }

    private void OnSleepClick(object sender, RoutedEventArgs e)
    {
        HidePanel();
        _ = Task.Run(() => PowerService.Execute(PowerAction.Sleep));
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (SuppressTileClick()) return;
        await ConfirmAndRunAsync(PowerAction.Restart, "重启");
    }

    private async void OnShutdownClick(object sender, RoutedEventArgs e)
        => await ConfirmAndRunAsync(PowerAction.Shutdown, "关机");

    private async Task ConfirmAndRunAsync(PowerAction action, string label)
    {
        var dialog = new ContentDialog
        {
            Title = $"确认{label}？",
            Content = $"{label}会关闭所有应用，未保存的内容可能丢失。",
            PrimaryButtonText = label,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        HidePanel();
        await Task.Run(() => PowerService.Execute(action));
    }

    private void ApplyChrome()
    {
        var hwnd = Hwnd;
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= -13565953;
        style |= 0x80000000; // WS_POPUP
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)style);

        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(ex | WS_EX_TOOLWINDOW));

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        var corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var disableTransitions = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disableTransitions, sizeof(int));
    }

    private nint Hwnd => WindowNative.GetWindowHandle(this);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static nint GetWindowLongPtr(nint hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, value.ToInt32());

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
