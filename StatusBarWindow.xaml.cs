using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.System;

namespace GameLocker;

/// <summary>
/// 顶栏2（系统顶栏）：贴屏幕顶边的全宽常驻信息条。
/// 只负责“显示 + 轻量点击”，占位/显隐/全屏挂起由 <see cref="StatusBarHost"/> 管。
/// 窗口用 WS_EX_NOACTIVATE，点击信息段不会把前台应用切走。
/// </summary>
public sealed partial class StatusBarWindow : Window
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_NOACTIVATE = 0x8000000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly AlwaysActiveAcrylicBackdrop _acrylic = new();
    private readonly DispatcherTimer _clockTimer = new();
    private readonly DispatcherTimer _batteryTimer = new();
    private readonly DispatcherTimer _networkTimer = new();
    private readonly DispatcherTimer _imeTimer = new();
    private readonly DispatcherTimer _notificationTimer = new();
    private readonly DispatcherTimer _resourceTimer = new();
    private readonly DispatcherTimer _batteryHealthTimer = new();
    private readonly DispatcherTimer _timerTicker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _trayStateTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _maximizedTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _maximizedWindow;

    private double _scale = 1;
    private int _barHeight = 32;
    private int _fontSize = 12;
    private DateTime _notificationSeen = DateTime.Now;
    private bool _notificationOpen;

    private TimeSpan _timerRemaining = TimeSpan.FromMinutes(25);
    private int _timerMinutes = 25;
    private bool _timerRunning;
    private bool _timerInitialized;

    /// <summary>DPI / 分辨率变化，宿主据此重新占位与摆放。</summary>
    public Action? LayoutInvalidated { get; set; }

    public nint Hwnd => WindowNative.GetWindowHandle(this);

    public double Scale => _scale > 0 ? _scale : 1;

    public int PreferredHeight => (int)Math.Round(_barHeight * Scale);

    /// <summary>顶部任务栏模式：把顶栏高度设成系统任务栏实际高度，填满顶部预留条。</summary>
    public void SetTopTaskbarHeight(int dip)
    {
        _barHeight = Math.Clamp(dip, 24, 80);
        ApplyClockLayout();
    }

    public StatusBarWindow()
    {
        InitializeComponent();
        Title = "GameLocker Status Bar";
        ToolTipService.SetToolTip(TraySegment, "打开 Windows 系统托盘");
        ApplyChrome();
        RefreshScale();

        _clockTimer.Tick += (_, _) => { UpdateClock(); ScheduleClock(); };
        _batteryTimer.Interval = TimeSpan.FromSeconds(5);
        _batteryTimer.Tick += (_, _) => UpdateBattery();
        _networkTimer.Interval = TimeSpan.FromSeconds(5);
        _networkTimer.Tick += (_, _) => UpdateNetwork();
        _imeTimer.Interval = TimeSpan.FromSeconds(1);
        _imeTimer.Tick += (_, _) => UpdateIme();
        _notificationTimer.Interval = TimeSpan.FromSeconds(10);
        _notificationTimer.Tick += (_, _) => UpdateNotifications();
        _trayStateTimer.Tick += (_, _) => UpdateTrayState();
        _maximizedTimer.Tick += (_, _) => UpdateMaximizedState();
        _resourceTimer.Interval = TimeSpan.FromSeconds(2);
        _resourceTimer.Tick += (_, _) => UpdateResource();
        _batteryHealthTimer.Interval = TimeSpan.FromMinutes(5);
        _batteryHealthTimer.Tick += async (_, _) => await UpdateBatteryHealthAsync();
        _timerTicker.Tick += (_, _) => TickTimer();

        Root.Loaded += (_, _) =>
        {
            RefreshScale();
            if (Root.XamlRoot is not null)
            {
                Root.XamlRoot.Changed += (_, _) =>
                {
                    var before = Scale;
                    RefreshScale();
                    if (Math.Abs(Scale - before) > 0.01) LayoutInvalidated?.Invoke();
                };
            }
        };
        Closed += (_, _) => StopSegmentTimers();
        Root.SizeChanged += (_, e) =>
            DebugLog.Write($"[顶栏2] size {e.NewSize.Width:0.#}x{e.NewSize.Height:0.#} scale={Scale:0.##} " +
                           $"content={ContentGrid.ActualWidth:0.#} right={RightPanel.ActualWidth:0.#}/{RightPanel.Visibility}");
    }

    /// <summary>按设置重建信息段、外观与定时器。写在宿主创建/改设置时调用。</summary>
    public async Task ApplySettingsAsync()
    {
        var s = SettingsService.Current;
        RefreshScale();

        _barHeight = Math.Clamp(s.StatusBarHeight, 24, 40);
        _fontSize = Math.Clamp(s.StatusBarFontSize, 10, 18);

        _timerMinutes = Math.Clamp(s.StatusBarTimerMinutes, 5, 120);
        if (!_timerInitialized)
        {
            _timerRemaining = TimeSpan.FromMinutes(_timerMinutes);
            _timerInitialized = true;
        }

        ApplyBackdrop(s);
        ApplyFloatingBar(s);
        ApplyFontAndColor(s);
        ApplyClockLayout();
        ApplySegmentVisibility(s);
        ApplyClockFormat();

        _notificationSeen = ParseSeenTime(s.StatusBarNotificationSeenTime);
        if (string.IsNullOrWhiteSpace(s.StatusBarNotificationSeenTime))
            SettingsService.Update(x => x.StatusBarNotificationSeenTime = DateTime.UtcNow.ToString("O"));

        UpdateClock();
        UpdateBattery();
        UpdateNetwork();
        UpdateIme();
        UpdateNotifications();
        UpdateFocus();
        UpdateTrayState();
        UpdateMaximizedState();
        UpdateTimer();
        UpdateResource();
        if (SettingsService.Current.StatusBarShowBatteryHealth) _ = UpdateBatteryHealthAsync();

        StartTimers();
        DebugLog.Write($"[顶栏2] apply scale={Scale:0.##} height={_barHeight} font={_fontSize} " +
                       $"mode={s.StatusBarBackdropMode} right={RightPanel.Visibility} " +
                       $"clock={ClockSegment.Visibility} net={NetworkSegment.Visibility} " +
                       $"bat={BatterySegment.Visibility} ime={ImeSegment.Visibility} " +
                       $"notify={NotificationSegment.Visibility} " +
                       $"res={ResourceSegment.Visibility} health={BatteryHealthSegment.Visibility}");
        await Task.CompletedTask;
    }

    public void MoveTo(int x, int y, int width, int height)
    {
        RefreshScale();
        SetWindowPos(Hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
    }

    public void SetVisible(bool visible)
    {
        ShowWindow(Hwnd, visible ? 4 : 0);
        if (!visible)
        {
            StatusBarControlCenterWindow.CloseIfOpen();
            // 只停信息段定时器；全屏检测必须继续跑，否则退出全屏后没人把窗口叫回来
            StopSegmentTimers();
        }
        else
        {
            StartTimers();
            // 从全屏/挂起恢复时立即刷新，不等下一个定时器周期
            UpdateClock();
            UpdateBattery();
            UpdateNetwork();
            UpdateIme();
            UpdateNotifications();
            UpdateFocus();
            UpdateResource();
            if (SettingsService.Current.StatusBarShowBatteryHealth) _ = UpdateBatteryHealthAsync();
        }
    }

    private void RefreshScale()
    {
        var scale = Root?.XamlRoot?.RasterizationScale ?? 0;
        if (scale <= 0) scale = GetDpiForWindow(Hwnd) / 96.0;
        _scale = scale > 0 ? scale : 1;
    }

    // ---------------- 外观 ----------------

    private void ApplyBackdrop(AppSettings s)
    {
        try
        {
            switch (Math.Clamp(s.StatusBarBackdropMode, 0, 3))
            {
                case 0: // 跟随系统亚克力：窗口背景走 SystemBackdrop，Bar 只叠一层极淡染色
                    SystemBackdrop = _acrylic;
                    Bar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x38, 0x10, 0x10, 0x14));
                    break;
                case 1: // 半透明
                    SystemBackdrop = null;
                    Bar.Background = new SolidColorBrush(ParseColor(s.StatusBarColor));
                    break;
                case 2: // 纯色
                    SystemBackdrop = null;
                    Bar.Background = new SolidColorBrush(ParseColor(s.StatusBarColor, forceOpaque: true));
                    break;
                default: // 渐变
                    SystemBackdrop = null;
                    Bar.Background = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0),
                        EndPoint = new Windows.Foundation.Point(1, 0),
                        GradientStops =
                        {
                            new GradientStop { Color = ParseColor(s.StatusBarColor), Offset = 0 },
                            new GradientStop { Color = ParseColor(s.StatusBarColor2), Offset = 1 }
                        }
                    };
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("[顶栏2] 应用底盘材质失败：" + ex.Message);
        }
    }

    private void ApplyFloatingBar(AppSettings s)
    {
        // 贴满窗口：四周不留内边距，也不做圆角
        Bar.Margin = new Thickness(0);
        Bar.CornerRadius = new CornerRadius(0);
        if (SystemBackdrop is not TransparentBackdrop)
            SystemBackdrop = new TransparentBackdrop();

        // 有最大化窗口：不透明纯色，不染色；桌面：右深左透明的渐变
        if (_maximizedWindow)
        {
            Bar.Background = new SolidColorBrush(ParseColor(s.StatusBarColor, forceOpaque: true));
            return;
        }

        // 主题只改底色，左右渐变透明的 alpha 结构保持不变
        var themeIndex = Math.Clamp(s.StatusBarTheme, 0, DockThemes.All.Length - 1);
        var theme = DockThemes.Get(themeIndex);
        var baseColor = themeIndex == 0
            ? ParseColor(s.StatusBarColor, forceOpaque: true)
            : ParseColor(theme.Bar, forceOpaque: true);
        var endColor = themeIndex == 0
            ? baseColor
            : ParseColor(theme.Bar2 ?? theme.Bar, forceOpaque: true);
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };

        void Stop(double offset, byte alpha, Windows.UI.Color color) => gradient.GradientStops.Add(new GradientStop
        {
            Offset = offset,
            Color = Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B)
        });

        Stop(0.00, 0x00, baseColor);
        Stop(0.45, 0x00, baseColor);
        Stop(0.65, 0x48, baseColor);
        Stop(0.85, 0xB8, endColor);
        Stop(1.00, 0xE8, endColor);
        Bar.Background = gradient;
    }

    private void ApplyFontAndColor(AppSettings s)
    {
        var color = ParseColor(s.StatusBarTextColor);
        var brush = new SolidColorBrush(color);

        ClockText.FontSize = _fontSize + 1.5;
        DateText.FontSize = Math.Max(10, _fontSize - 0.5);
        ClockText.Foreground = brush;
        DateText.Foreground = brush;

        BatteryText.FontSize = Math.Max(10, _fontSize - 1);
        BatteryText.Foreground = brush;
        BatteryBolt.FontSize = Math.Max(10, _fontSize - 1);
        BatteryBolt.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        ImeText.FontSize = Math.Max(9, _fontSize - 2);
        ImeText.Foreground = brush;
        NotificationCount.FontSize = Math.Max(9, _fontSize - 1.5);
        NotificationCount.Foreground = brush;
        ResourceText.FontSize = Math.Max(9, _fontSize - 2);
        BatteryHealthText.FontSize = Math.Max(9, _fontSize - 2);
        TimerText.FontSize = Math.Max(9, _fontSize - 2);
        ResourceText.Foreground = brush;
        BatteryHealthText.Foreground = brush;
        TimerText.Foreground = brush;

        NetworkIcon.FontSize = _fontSize + 1;
        BatteryIcon.FontSize = _fontSize + 6;
        BatteryBolt.FontSize = Math.Max(10, _fontSize - 1);
        NotificationIcon.FontSize = _fontSize + 1;
        ControlCenterIcon.FontSize = _fontSize + 1;
        FocusIcon.FontSize = _fontSize + 1;
        TrayIcon.FontSize = _fontSize + 1;
        NetworkIcon.Foreground = brush;
        NotificationIcon.Foreground = brush;
        ControlCenterIcon.Foreground = brush;
        FocusIcon.Foreground = brush;
        TrayIcon.Foreground = brush;
        // 电池图标保留电量分档色，由 UpdateBattery 覆盖
    }

    private void ApplyClockLayout()
    {
        // 始终两行：上时间、下日期；日期在时间正下方居中
        var tall = _barHeight >= 42;
        ClockStack.Orientation = Orientation.Vertical;
        ClockStack.Spacing = 0;
        ClockStack.HorizontalAlignment = HorizontalAlignment.Right;
        ClockText.HorizontalAlignment = HorizontalAlignment.Center;
        DateText.HorizontalAlignment = HorizontalAlignment.Center;
        DateText.Margin = new Thickness(0);
        ClockText.FontSize = tall ? _fontSize + 3 : _fontSize + 1;
        DateText.FontSize = tall ? Math.Max(8.5, _fontSize - 3.5) : Math.Max(7.5, _fontSize - 4.5);
        ClockText.LineHeight = tall ? 18 : 15;
        DateText.LineHeight = tall ? 11 : 9;
    }

    private void ApplySegmentVisibility(AppSettings s)
    {
        ClockSegment.Visibility = s.StatusBarShowClock ? Visibility.Visible : Visibility.Collapsed;
        BatterySegment.Visibility = s.StatusBarShowBattery ? Visibility.Visible : Visibility.Collapsed;
        NetworkSegment.Visibility = s.StatusBarShowNetwork ? Visibility.Visible : Visibility.Collapsed;
        ImeSegment.Visibility = s.StatusBarShowIme ? Visibility.Visible : Visibility.Collapsed;
        NotificationSegment.Visibility = s.StatusBarShowNotifications ? Visibility.Visible : Visibility.Collapsed;
        ControlCenterSegment.Visibility = s.StatusBarShowControlCenter ? Visibility.Visible : Visibility.Collapsed;
        FocusSegment.Visibility = s.StatusBarShowFocus ? Visibility.Visible : Visibility.Collapsed;
        TraySegment.Visibility = s.StatusBarShowTray ? Visibility.Visible : Visibility.Collapsed;
        ResourceSegment.Visibility = s.StatusBarShowResources ? Visibility.Visible : Visibility.Collapsed;
        BatteryHealthSegment.Visibility = s.StatusBarShowBatteryHealth ? Visibility.Visible : Visibility.Collapsed;
        TimerSegment.Visibility = s.StatusBarShowTimer ? Visibility.Visible : Visibility.Collapsed;
        RightPanel.Visibility = s.StatusBarShowClock || s.StatusBarShowBattery || s.StatusBarShowNetwork
            || s.StatusBarShowIme || s.StatusBarShowNotifications || s.StatusBarShowControlCenter
            || s.StatusBarShowResources || s.StatusBarShowBatteryHealth || s.StatusBarShowFocus
            || s.StatusBarShowTimer || s.StatusBarShowTray
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyClockFormat()
    {
        // 目前固定 24 小时制；格式自定义归入 P2-04
        ScheduleClock();
    }

    // ---------------- 定时器 ----------------

    private void StartTimers()
    {
        var s = SettingsService.Current;
        StopSegmentTimers();

        if (s.StatusBarShowClock) ScheduleClock();
        if (s.StatusBarShowBattery) _batteryTimer.Start();
        if (s.StatusBarShowNetwork) _networkTimer.Start();
        if (s.StatusBarShowIme) _imeTimer.Start();
        if (s.StatusBarShowNotifications) _notificationTimer.Start();
        if (s.StatusBarShowTray) _trayStateTimer.Start();
        _maximizedTimer.Start();
        if (s.StatusBarShowResources) _resourceTimer.Start();
        if (s.StatusBarShowBatteryHealth)
        {
            _ = UpdateBatteryHealthAsync();
            _batteryHealthTimer.Start();
        }
        if (s.StatusBarShowTimer && _timerRunning) _timerTicker.Start();
    }

    private void StopSegmentTimers()
    {
        _clockTimer.Stop();
        _batteryTimer.Stop();
        _networkTimer.Stop();
        _imeTimer.Stop();
        _notificationTimer.Stop();
        _trayStateTimer.Stop();
        _maximizedTimer.Stop();
        _resourceTimer.Stop();
        _batteryHealthTimer.Stop();
        _timerTicker.Stop();
    }

    private void ScheduleClock()
    {
        var now = DateTime.Now;
        var seconds = 60 - now.Second + 0.05;
        _clockTimer.Interval = TimeSpan.FromSeconds(seconds);
        _clockTimer.Start();
    }

    // ---------------- 信息段 ----------------

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = $"周{"日一二三四五六"[(int)now.DayOfWeek]} {now.Month}月";
        ToolTipService.SetToolTip(ClockSegment, now.ToString("yyyy年M月d日 dddd"));
    }

    private void UpdateBattery()
    {
        if (!SettingsService.Current.StatusBarShowBattery)
        {
            BatterySegment.Visibility = Visibility.Collapsed;
            return;
        }

        var pct = BatteryService.GetPercent();
        if (pct is null)
        {
            BatterySegment.Visibility = Visibility.Collapsed;
            return;
        }

        BatterySegment.Visibility = Visibility.Visible;
        var (glyph, color) = BatteryService.Describe(pct.Value);
        BatteryIcon.Glyph = glyph;
        BatteryIcon.Foreground = new SolidColorBrush(color);
        BatteryText.Text = $"{pct.Value}%";
        BatteryBolt.Visibility = BatteryService.GetOnAc() == true ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(BatterySegment, BatteryService.GetOnAc() == true ? "已接通电源" : "使用电池");
    }

    private void UpdateNetwork()
    {
        if (!SettingsService.Current.StatusBarShowNetwork)
        {
            NetworkSegment.Visibility = Visibility.Collapsed;
            return;
        }

        NetworkSegment.Visibility = Visibility.Visible;
        var brush = new SolidColorBrush(ParseColor(SettingsService.Current.StatusBarTextColor));

        if (NetworkService.IsWifiConnected())
        {
            NetworkIcon.Glyph = "\uE701";
            NetworkIcon.Foreground = brush;
            ToolTipService.SetToolTip(NetworkSegment, "Wi-Fi 已连接");
        }
        else if (NetworkInterface.GetIsNetworkAvailable())
        {
            NetworkIcon.Glyph = "\uE839";
            NetworkIcon.Foreground = brush;
            ToolTipService.SetToolTip(NetworkSegment, "有线网络已连接");
        }
        else
        {
            NetworkIcon.Glyph = "\uE783";
            NetworkIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xB9, 0xBE, 0xC6));
            ToolTipService.SetToolTip(NetworkSegment, "未连接网络");
        }
    }

    private void UpdateIme()
    {
        if (!SettingsService.Current.StatusBarShowIme)
        {
            ImeSegment.Visibility = Visibility.Collapsed;
            return;
        }

        var chinese = ImeStateWatcher.IsChinese();
        if (chinese is null)
        {
            ImeSegment.Visibility = Visibility.Collapsed;
            return;
        }

        ImeSegment.Visibility = Visibility.Visible;
        ImeText.Text = chinese.Value ? "中" : "EN";
        ImeSegment.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ToolTipService.SetToolTip(ImeSegment, chinese.Value ? "中文输入" : "英文输入");
    }

    private void UpdateNotifications()
    {
        if (!SettingsService.Current.StatusBarShowNotifications)
        {
            NotificationSegment.Visibility = Visibility.Collapsed;
            return;
        }

        NotificationSegment.Visibility = Visibility.Visible;

        if (SettingsService.Current.StatusBarFocusMode)
        {
            NotificationIcon.Glyph = "\uE7ED";
            NotificationCount.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(NotificationSegment, "专注模式：仅隐藏通知计数");
            return;
        }

        NotificationIcon.Glyph = "\uEA8F";
        var count = NotificationService.CountSince(_notificationSeen);
        NotificationCount.Text = count > 99 ? "99+" : count.ToString();
        NotificationCount.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(NotificationSegment, count > 0 ? $"{count} 个应用有新通知" : "通知中心");
    }

    /// <summary>专注模式：只改本程序表现（高亮入口、通知段变免打扰），不碰系统勿扰。</summary>
    private void UpdateFocus()
    {
        var on = SettingsService.Current.StatusBarFocusMode;
        FocusSegment.Background = new SolidColorBrush(on
            ? Windows.UI.Color.FromArgb(0x40, 0x7A, 0xD9, 0xC4)
            : Microsoft.UI.Colors.Transparent);
        FocusIcon.Foreground = on
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x7A, 0xD9, 0xC4))
            : new SolidColorBrush(ParseColor(SettingsService.Current.StatusBarTextColor));
        FocusIcon.Opacity = on ? 1 : 0.78;
        ToolTipService.SetToolTip(FocusSegment, on ? "专注模式：开（点击关闭）" : "专注模式：关（点击开启）");
    }

    private void UpdateMaximizedState()
    {
        var next = WindowStateWatcher.HasVisibleMaximizedWindow();
        if (next == _maximizedWindow) return;
        _maximizedWindow = next;
        DebugLog.Write($"[顶栏2] 最大化窗口={next}，顶栏改为{(next ? "不透明" : "渐变透明")}");
        ApplyFloatingBar(SettingsService.Current);
    }

    private void UpdateTrayState()
    {
        var open = TrayAppService.IsSystemTrayOpen();
        TrayIcon.Glyph = open ? "\uE70D" : "\uE70E"; // 打开=向下，关闭=向上
        ToolTipService.SetToolTip(TraySegment, open ? "关闭系统托盘" : "打开 Windows 系统托盘");
    }

    private void UpdateTimer()
    {
        if (!SettingsService.Current.StatusBarShowTimer)
        {
            TimerSegment.Visibility = Visibility.Collapsed;
            _timerTicker.Stop();
            return;
        }

        TimerSegment.Visibility = Visibility.Visible;
        TimerText.Text = $"{(int)_timerRemaining.TotalMinutes:00}:{_timerRemaining.Seconds:00}";
        TimerSegment.Background = new SolidColorBrush(_timerRunning
            ? Windows.UI.Color.FromArgb(0x40, 0x7A, 0xD9, 0xC4)
            : Microsoft.UI.Colors.Transparent);
        ToolTipService.SetToolTip(TimerSegment, _timerRunning
            ? "计时中，点击暂停"
            : _timerRemaining <= TimeSpan.Zero
                ? "时间到，点击重新开始"
                : "点击开始计时");
    }

    private void TickTimer()
    {
        if (!_timerRunning) return;

        _timerRemaining -= TimeSpan.FromSeconds(1);
        if (_timerRemaining <= TimeSpan.Zero)
        {
            _timerRemaining = TimeSpan.Zero;
            _timerRunning = false;
            _timerTicker.Stop();
            FloatingToastService.Show($"计时结束：{_timerMinutes} 分钟到了");
            try { if (SettingsService.Current.UiSoundEnabled) UiSound.Play(UiSoundKind.Select); } catch { }
        }
        UpdateTimer();
    }

    private void OnTimerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_timerRemaining <= TimeSpan.Zero)
            _timerRemaining = TimeSpan.FromMinutes(_timerMinutes);

        _timerRunning = !_timerRunning;
        if (_timerRunning) _timerTicker.Start();
        else _timerTicker.Stop();
        UpdateTimer();
    }

    private void UpdateResource()
    {
        if (!SettingsService.Current.StatusBarShowResources)
        {
            ResourceSegment.Visibility = Visibility.Collapsed;
            return;
        }

        ResourceSegment.Visibility = Visibility.Visible;
        var cpu = SystemResourceSampler.CpuPercent();
        var memory = SystemResourceSampler.MemoryPercent();
        ResourceText.Text = cpu is null ? "CPU --" : $"CPU {cpu}%";
        if (memory is not null) ResourceText.Text += $" · {memory}%";
        ToolTipService.SetToolTip(ResourceSegment,
            $"CPU {cpu?.ToString() ?? "--"}% · 内存 {memory?.ToString() ?? "--"}%");
    }

    private async Task UpdateBatteryHealthAsync()
    {
        if (!SettingsService.Current.StatusBarShowBatteryHealth)
        {
            BatteryHealthSegment.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var health = await BatteryHealthService.ReadAsync();
            if (health?.HealthPercent is null)
            {
                BatteryHealthSegment.Visibility = Visibility.Collapsed;
                return;
            }

            BatteryHealthSegment.Visibility = Visibility.Visible;
            BatteryHealthText.Text = $"健康 {health.HealthPercent}%";
            ToolTipService.SetToolTip(BatteryHealthSegment,
                $"设计容量 {health.DesignCapacity} mWh · 满充 {health.FullChargeCapacity} mWh" +
                (health.CycleCount is null ? "" : $" · 循环 {health.CycleCount} 次"));
        }
        catch
        {
            BatteryHealthSegment.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- 点击 ----------------

    private async void OnBatteryTapped(object sender, TappedRoutedEventArgs e)
        => await LaunchSettingsAsync("ms-settings:batterysaver");

    private async void OnBatteryHealthTapped(object sender, TappedRoutedEventArgs e)
        => await LaunchSettingsAsync("ms-settings:batterysaver");

    private void OnResourceTapped(object sender, TappedRoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("taskmgr.exe"); }
        catch (Exception ex) { DebugLog.Write("[顶栏2] 打开任务管理器失败：" + ex.Message); }
    }

    private async void OnNetworkTapped(object sender, TappedRoutedEventArgs e)
        => await LaunchSettingsAsync("ms-settings:network");

    private async void OnNotificationTapped(object sender, TappedRoutedEventArgs e)
    {
        // 已经打开就再点一次关闭
        if (_notificationOpen && NotificationService.IsCenterForeground())
        {
            NotificationService.CloseCenter();
            _notificationOpen = false;
            DebugLog.Write("[顶栏2] 通知中心已打开，再次点击关闭");
            return;
        }

        _notificationOpen = true;

        _notificationSeen = DateTime.Now;
        SettingsService.Update(s => s.StatusBarNotificationSeenTime = DateTime.UtcNow.ToString("O"));
        UpdateNotifications();

        // 用 ms-actioncenter: 打开通知中心，比注入 Win+N 可靠
        try
        {
            var ok = await Launcher.LaunchUriAsync(new Uri("ms-actioncenter:"));
            DebugLog.Write($"[顶栏2] 打开通知中心 actioncenter={ok}");
            if (ok) return;
        }
        catch (Exception ex)
        {
            DebugLog.Write("[顶栏2] ms-actioncenter 失败：" + ex.Message);
        }

        NotificationService.OpenCenter();
    }

    private async void OnClockTapped(object sender, TappedRoutedEventArgs e)
    {
        // 点时间打开/关闭 Windows 通知中心/日历
        StatusBarControlCenterWindow.CloseIfOpen();
        if (_notificationOpen && NotificationService.IsCenterForeground())
        {
            NotificationService.CloseCenter();
            _notificationOpen = false;
            DebugLog.Write("[顶栏2] 时间：再次点击，关闭系统日历");
            return;
        }

        _notificationOpen = true;
        try
        {
            var ok = await Launcher.LaunchUriAsync(new Uri("ms-actioncenter:"));
            DebugLog.Write($"[顶栏2] 时间点击打开系统日历 actioncenter={ok}");
            if (ok) return;
        }
        catch (Exception ex)
        {
            DebugLog.Write("[顶栏2] 打开系统日历失败：" + ex.Message);
        }
        NotificationService.OpenCenter();
    }

    private async void OnImeTapped(object sender, TappedRoutedEventArgs e)
    {
        // 先把 UI 切过去，再改前台 IME 转换态，避免等 1 秒轮询才变化
        var wasChinese = ImeText.Text == "中";
        ImeText.Text = wasChinese ? "EN" : "中";
        try
        {
            if (!ImeStateWatcher.Toggle()) KeySendService.Send(0x10 /* VK_SHIFT */, 0);
        }
        catch (Exception ex) { DebugLog.Write("[顶栏2] 切换输入法失败：" + ex.Message); }

        await Task.Delay(120);
        UpdateIme();
    }

    private void OnFocusTapped(object sender, TappedRoutedEventArgs e)
    {
        var next = !SettingsService.Current.StatusBarFocusMode;
        SettingsService.Update(s => s.StatusBarFocusMode = next);
        UpdateFocus();
        UpdateNotifications();
    }

    private void OnTrayTapped(object sender, TappedRoutedEventArgs e)
    {
        // 只打开 / 关闭 Windows 自己的通知区域
        StatusBarControlCenterWindow.CloseIfOpen();

        if (TrayAppService.IsSystemTrayOpen()) TrayAppService.CloseSystemTray();
        else TrayAppService.OpenSystemTray();

        UpdateTrayState();
    }

    private void OnControlCenterTapped(object sender, TappedRoutedEventArgs e)
    {
        // 设置里关掉「本程序控制中心」时，改用 Windows 自带的控制中心（Win+A 快捷设置）
        if (!SettingsService.Current.StatusBarCustomControlCenter)
        {
            StatusBarControlCenterWindow.CloseIfOpen();
            var sent = KeySendService.Send((int)'A', KeySendService.ModWin);
            DebugLog.Write($"[顶栏2] 控制中心改用系统面板：Win+A={sent}");
            return;
        }
        if (!GetWindowRect(Hwnd, out var rect)) return;
        DebugLog.Write($"[顶栏2] 控制中心点击 rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
        var upward = TopTaskbarMode.IsActive && TopTaskbarMode.Edge == TaskbarEdge.Bottom;
        StatusBarControlCenterWindow.Toggle(rect.Right, rect.Top, rect.Bottom, upward);
    }

    private static async Task LaunchSettingsAsync(string uri)
    {
        try { await Launcher.LaunchUriAsync(new Uri(uri)); }
        catch (Exception ex) { DebugLog.Write("[顶栏2] 打开系统设置失败：" + ex.Message); }
    }

    // ---------------- 悬停 / 按压 ----------------

    private void OnSegmentPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b)
            b.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    }

    private void OnSegmentPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border b) return;
        if (b == FocusSegment)
        {
            b.Background = new SolidColorBrush(SettingsService.Current.StatusBarFocusMode
                ? Windows.UI.Color.FromArgb(0x40, 0x7A, 0xD9, 0xC4)
                : Microsoft.UI.Colors.Transparent);
        }
        else
        {
            b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        if (b.RenderTransform is CompositeTransform t)
        {
            t.ScaleX = 1;
            t.ScaleY = 1;
        }
    }

    private void OnSegmentPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border b || b.RenderTransform is not CompositeTransform t) return;
        t.CenterX = b.ActualWidth / 2;
        t.CenterY = b.ActualHeight / 2;
        t.ScaleX = 0.94;
        t.ScaleY = 0.94;
    }

    private void OnSegmentPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border b || b.RenderTransform is not CompositeTransform t) return;
        t.ScaleX = 1;
        t.ScaleY = 1;
    }

    // ---------------- 窗口样式 ----------------

    private void ApplyChrome()
    {
        var hwnd = Hwnd;
        if (hwnd == IntPtr.Zero) return;

        // 去掉标题栏/边框，改成无边框弹出窗；不激活、不进 Alt+Tab
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= -13565953;
        style |= 0x80000000; // WS_POPUP
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)style);

        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        var corner = 1; // DWMWCP_DONOTROUND：全宽贴顶，不需要圆角
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // 允许内容透明：让 DWM 把整个窗口当玻璃区域，渐变左侧才能真正透出桌面
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        var backdropNone = 1; // DWMSBT_NONE
        DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref backdropNone, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    private static DateTime ParseSeenTime(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
            return parsed.ToLocalTime();
        return DateTime.Now;
    }

    private static Windows.UI.Color ParseColor(string? value, bool forceOpaque = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return Microsoft.UI.Colors.Transparent;
        var hex = value.Trim().TrimStart('#');
        try
        {
            var a = 0xFF;
            if (hex.Length == 8)
            {
                a = Convert.ToByte(hex[..2], 16);
                hex = hex[2..];
            }
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex.Substring(2, 2), 16);
            var b = Convert.ToByte(hex.Substring(4, 2), 16);
            return Windows.UI.Color.FromArgb(forceOpaque ? (byte)0xFF : (byte)a, r, g, b);
        }
        catch
        {
            return Microsoft.UI.Colors.Transparent;
        }
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
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

/// <summary>完全透明的 SystemBackdrop：让窗口自身不出底色，渐变左侧才能透出桌面。</summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private Windows.UI.Composition.CompositionBrush? _brush;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        var compositor = new Windows.UI.Composition.Compositor();
        _brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        target.SystemBackdrop = _brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _brush?.Dispose();
        _brush = null;
    }
}
