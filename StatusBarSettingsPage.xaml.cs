using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GameLocker;

/// <summary>
/// 「顶栏2」设置页：总开关、占位/全屏行为、高度/材质/字号与信息段开关。
/// 所有改动即时落盘并调用 <see cref="StatusBarHost.ApplyAsync"/> 重建窗口；
/// 滑杆做 250ms 防抖，避免拖动时反复注册 AppBar。
/// 手柄：方向=移动光标，A=切换/循环，B=返回设置主页（由 MainWindow 统一处理）。
/// </summary>
public sealed partial class StatusBarSettingsPage : Page, ISettingsGamepad
{
    private readonly List<Button> _rows = [];
    private readonly List<Action> _activate = [];

    private readonly DispatcherTimer _applyDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private int _index;
    private bool _ready;
    private GamepadButtons _capturedCombo;
    private TextBlock? _comboText;
    private Button? _comboButton;
    /// <summary>录制阶段：0=空闲，1=倒计时（不响应输入，给手留出松开 A 的时间），2=正式录制</summary>
    private int _comboPhase;
    private int _comboSecondsLeft;
    private DispatcherTimer? _comboTimer;
    private StackPanel? _topControlsPanel;
    private readonly List<ToggleSwitch> _topControlRows = [];
    private bool _topControlsExpanded;
    private TextBlock? _topControlsLabel;
    private FontIcon? _topControlsChevron;

    private static readonly SolidColorBrush FocusBrush = SelectionGlow.CursorBrush;
    private SelectionGlow? _glow;
    private static readonly SolidColorBrush IdleBrush =
        new(Microsoft.UI.Colors.Transparent);

    private static readonly string[] BackdropNames = ["跟随系统（亚克力）", "半透明", "纯色", "渐变"];

    public StatusBarSettingsPage()
    {
        InitializeComponent();
        // 设置项行用手绘白框当光标：子控件拿到焦点时不再画系统焦点框
        FocusRing.Attach(this);
        if (Content is Panel root) _glow = SelectionGlow.Attach(root);
        Build();

        _applyDebounce.Tick += (_, _) =>
        {
            _applyDebounce.Stop();
            ApplyNow();
        };

        // 样张日期用当天，避免看起来像过期截图
        var now = DateTime.Now;
        PreviewDate.Text = $"周{"日一二三四五六"[(int)now.DayOfWeek]} {now:M/dd}";

        Loaded += (_, _) =>
        {
            _ready = true;
            UpdatePreview();
            UpdateStatus();
            UpdateCursor();
        };
        Unloaded += (_, _) =>
        {
            _ready = false;
            _applyDebounce.Stop();
        };
    }

    private void Build()
    {
        var s = SettingsService.Current;

        ToggleSwitch? master = null;
        master = AddToggle("启用系统", "打开后隐藏任务栏，顶栏跟随任务栏所在的上/下边缘", "\uE950",
            s.SystemEnabled, v =>
            {
                if (v && !TaskbarEdgeService.CanUse(out var message))
                {
                    FloatingToastService.Show(message, warning: true);
                    if (master is not null) master.IsOn = false;
                    return;
                }
                SettingsService.Update(x =>
                {
                    x.SystemEnabled = v;
                    x.DockTaskbarMode = 0;
                });
                // 关闭「启用系统」后，下面所有设置项一起变灰且不可操作
                UpdateMasterGate(v);
                if (SettingsService.Current.StartMenuMode) App.Main?.ApplySidebarLayout();
            });

        var currentTop = !TaskbarEdgeService.TryGet(out var edge, out _) || edge == TaskbarEdge.Top;

        AddSection("顶栏设置", "TOP BAR");

        AddCycle("顶栏位置", "上 / 下；切换会同步 Windows 任务栏位置", "\uE8A9",
            new[] { "上", "下" }, currentTop ? 0 : 1,
            v => _ = SetTaskbarDirectionAsync(v == 0));

        AddCycle("顶栏大小", "大尺寸会同步使用 Windows 大任务栏高度", "\uE740",
            new[] { "小", "大" }, s.StatusBarLarge ? 1 : 0,
            v => SettingsService.Update(x => x.StatusBarLarge = v == 1));
        AddCycle("顶栏主题", "深色/浅色/纯色/渐变/高透；始终保持左右渐变透明", "\uE793",
            DockThemes.Names, Math.Clamp(s.StatusBarTheme, 0, DockThemes.All.Length - 1),
            v => SettingsService.Update(x => x.StatusBarTheme = v));

        AddToggle("控制中心", "打开用本程序控制中心；关闭改用 Windows 自带控制中心（Win+A）", "\uE713",
            s.StatusBarCustomControlCenter, v => SettingsService.Update(x => x.StatusBarCustomControlCenter = v));

        BuildTopControlsGroup(s);

        AddSection("底栏设置", "DOCK");

        AddComboCapture();

        AddCycle("底栏主题", "深浅玻璃 / 纯色 / 渐变 / 高透", "\uE771",
            DockThemes.Names, Math.Clamp(s.DockTheme, 0, DockThemes.All.Length - 1),
            v => SettingsService.Update(x => x.DockTheme = v));

        AddToggle("悬停放大", "鼠标经过时图标弹簧放大", "\uE8A3",
            s.DockMagnify, v => SettingsService.Update(x => x.DockMagnify = v));

        AddToggle("按压反馈", "按下图标时轻微缩小", "\uE7FC",
            s.DockPressFeedback, v => SettingsService.Update(x => x.DockPressFeedback = v));

        AddToggle("自动隐藏", "鼠标离开后收起，留一条边缘触发带", "\uE8AB",
            s.DockAutoHide, v => SettingsService.Update(x => x.DockAutoHide = v));

        AddCycle("图标大小", "条高随图标自动缩放", "\uE740",
            new[] { "小", "中", "大" },
            Array.IndexOf(new[] { AppSettings.DockIconSmall, AppSettings.DockIconMedium, AppSettings.DockIconLarge },
                AppSettings.DockIconPreset(s.DockIconSize)),
            v => SettingsService.Update(x => x.DockIconSize = v switch
            {
                0 => AppSettings.DockIconSmall,
                1 => AppSettings.DockIconMedium,
                _ => AppSettings.DockIconLarge
            }));

        UpdateMasterGate(s.SystemEnabled);
    }

    /// <summary>
    /// 总开关关闭时，把「启用系统」以下的全部设置行置灰并禁用；
    /// 重新打开时恢复。行内控件随 Button.IsEnabled 一起失效，无需逐个处理。
    /// </summary>
    private void UpdateMasterGate(bool enabled)
    {
        for (var i = 1; i < _rows.Count; i++)
        {
            _rows[i].IsEnabled = enabled;
            _rows[i].Opacity = enabled ? 1 : 0.45;
        }
    }

    private async Task SetTaskbarDirectionAsync(bool top)
    {
        var (ok, message) = await TaskbarPositionService.SetEdgeAsync(top);
        if (!ok) FloatingToastService.Show(message, warning: true);
        else await StatusBarHost.ApplyAsync();
    }

    private void BuildTopControlsGroup(AppSettings s)
    {
        _topControlsLabel = new TextBlock
        {
            Text = "展开",
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _topControlsChevron = new FontIcon
        {
            Glyph = "\uE70D",
            FontSize = 10,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center
        };
        var pillContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        pillContent.Children.Add(_topControlsLabel);
        pillContent.Children.Add(_topControlsChevron);

        var pill = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0x7A, 0xD9, 0xC4)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x7A, 0xD9, 0xC4)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = pillContent
        };

        AddRow("顶栏控件", "时间 / 电量 / 网络 / 输入法 / 通知 / 专注 / 托盘 / 控制中心 / 资源", "\uE71D",
            pill, ToggleTopControls);

        _topControlsPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        Rows.Children.Add(_topControlsPanel);

        _topControlRows.Add(AddToggle("时间日期", "左侧显示当前时间与日期", "\uE787",
            s.StatusBarShowClock, v => SettingsService.Update(x => x.StatusBarShowClock = v), _topControlsPanel));
        _topControlRows.Add(AddToggle("电量", "电池百分比与充电闪电；台式机自动隐藏", "\uE850",
            s.StatusBarShowBattery, v => SettingsService.Update(x => x.StatusBarShowBattery = v), _topControlsPanel));
        _topControlRows.Add(AddToggle("网络", "Wi-Fi / 有线 / 离线状态", "\uE701",
            s.StatusBarShowNetwork, v => SettingsService.Update(x => x.StatusBarShowNetwork = v), _topControlsPanel));
        _topControlRows.Add(AddToggle("输入法", "显示中/EN；读取不到时隐藏", "\uE765",
            s.StatusBarShowIme, v => SettingsService.Update(x => x.StatusBarShowIme = v), _topControlsPanel));
        _topControlRows.Add(AddToggle("通知", "有新通知的应用数，点击打开通知中心", "\uEA8F",
            s.StatusBarShowNotifications, v => SettingsService.Update(x => x.StatusBarShowNotifications = v), _topControlsPanel));
        _topControlRows.Add(AddToggle("专注模式", "开启后通知段变免打扰（只影响本程序，不改系统勿扰）", "\uE7ED",
            s.StatusBarFocusMode, v => SettingsService.Update(x => x.StatusBarFocusMode = v), _topControlsPanel));
        _topControlRows.Add(AddToggle("托盘图标", "在顶栏显示托盘图标；点击直接打开 Windows 系统托盘（Win+B）", "\uE8B7",
            s.StatusBarShowTray, v => SettingsService.Update(x => x.StatusBarShowTray = v), _topControlsPanel));
        _topControlRows.Add(AddToggle("控制中心入口", "Wi-Fi / 蓝牙 / 夜间模式 / 静音 / 音量 / 亮度", "\uE890",
            s.StatusBarShowControlCenter, v => SettingsService.Update(x => x.StatusBarShowControlCenter = v), _topControlsPanel));
        _topControlRows.Add(AddToggle("资源监视", "CPU / 内存，2 秒刷新；点击打开任务管理器", "\uE9D9",
            s.StatusBarShowResources, v => SettingsService.Update(x => x.StatusBarShowResources = v), _topControlsPanel));
    }

    private void ToggleTopControls()
    {
        _topControlsExpanded = !_topControlsExpanded;
        if (_topControlsPanel is not null)
            _topControlsPanel.Visibility = _topControlsExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (_topControlsLabel is not null) _topControlsLabel.Text = _topControlsExpanded ? "收起" : "展开";
        if (_topControlsChevron is not null)
            _topControlsChevron.Glyph = _topControlsExpanded ? "\uE70E" : "\uE70D";
    }

    private void AddSection(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 1, Margin = new Thickness(2, 14, 2, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 10.5,
            Opacity = 0.5,
            CharacterSpacing = 140
        });
        Rows.Children.Add(panel);
    }

    private void AddComboCapture()
    {
        _comboText = new TextBlock
        {
            Text = FormatCombo(SettingsService.Current.DockSummonCombo),
            FontSize = 12.5,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        };
        _comboButton = new Button
        {
            Content = "录制",
            FontSize = 11.5,
            MinHeight = 30,
            Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _comboButton.Click += (_, _) => StartComboCapture();

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(_comboText);
        panel.Children.Add(_comboButton);

        AddRow("呼出组合键", "长按 1 秒呼出底栏；启动器页面显示时切换底栏/页面的手柄焦点，再次长按切回", "\uE7A7",
            panel, StartComboCapture);
    }

    /// <summary>点「录制」或在该行按 A：先走 5 秒倒计时，倒计时结束后才开始吃手柄输入。</summary>
    private void StartComboCapture()
    {
        if (_comboPhase != 0)
        {
            CancelComboCapture();
            return;
        }

        _comboPhase = 1;
        _comboSecondsLeft = 5;
        _capturedCombo = GamepadButtons.None;
        if (_comboText is not null) _comboText.Text = $"{_comboSecondsLeft} 秒后开始录制…";
        if (_comboButton is not null) _comboButton.Content = "取消";

        _comboTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _comboTimer.Tick -= OnComboTick;
        _comboTimer.Tick += OnComboTick;
        _comboTimer.Start();
    }

    private void OnComboTick(object? sender, object e)
    {
        _comboSecondsLeft--;
        if (_comboPhase == 1)
        {
            if (_comboSecondsLeft > 0)
            {
                if (_comboText is not null) _comboText.Text = $"{_comboSecondsLeft} 秒后开始录制…";
                return;
            }
            // 倒计时结束：正式录制，再给 10 秒窗口，避免一直卡在录制态
            _comboPhase = 2;
            _comboSecondsLeft = 10;
            if (_comboText is not null) _comboText.Text = "请按手柄组合键…";
            return;
        }

        if (_comboPhase == 2 && _comboSecondsLeft <= 0)
        {
            FinishComboCapture();   // 超时：没按任何键，保持原组合键
        }
    }

    private void CancelComboCapture()
    {
        _comboTimer?.Stop();
        _comboPhase = 0;
        _comboSecondsLeft = 0;
        FinishComboCapture();
    }

    private void FinishComboCapture()
    {
        _comboTimer?.Stop();
        _comboPhase = 0;
        _comboSecondsLeft = 0;
        if (_comboButton is not null) _comboButton.Content = "录制";
        if (_comboText is not null) _comboText.Text = FormatCombo(SettingsService.Current.DockSummonCombo);
    }

    private void AddLink(string title, string subtitle, string glyph, Type page)
    {
        var button = new Button
        {
            Content = "修改",
            FontSize = 11.5,
            MinHeight = 30,
            Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => App.Main?.NavigateSettings(page);
        AddRow(title, subtitle, glyph, button, () => App.Main?.NavigateSettings(page));
    }

    private static string FormatCombo(GamepadButtons combo)
    {
        if (combo == GamepadButtons.None) return "未设置";
        var names = new List<string>();
        if (combo.HasFlag(GamepadButtons.LeftThumb)) names.Add("LS");
        if (combo.HasFlag(GamepadButtons.RightThumb)) names.Add("RS");
        if (combo.HasFlag(GamepadButtons.LeftTrigger)) names.Add("LT");
        if (combo.HasFlag(GamepadButtons.RightTrigger)) names.Add("RT");
        if (combo.HasFlag(GamepadButtons.LeftShoulder)) names.Add("LB");
        if (combo.HasFlag(GamepadButtons.RightShoulder)) names.Add("RB");
        if (combo.HasFlag(GamepadButtons.Menu)) names.Add("Start");
        if (combo.HasFlag(GamepadButtons.View)) names.Add("Select");
        if (combo.HasFlag(GamepadButtons.A)) names.Add("A");
        if (combo.HasFlag(GamepadButtons.B)) names.Add("B");
        if (combo.HasFlag(GamepadButtons.X)) names.Add("X");
        if (combo.HasFlag(GamepadButtons.Y)) names.Add("Y");
        return names.Count == 0 ? combo.ToString() : string.Join("+", names);
    }

    private void AddLiveSlider(string title, string subtitle, string glyph,
        int min, int max, int initial, Action<int> apply)
    {
        var value = new TextBlock
        {
            Text = Math.Clamp(initial, min, max).ToString(),
            FontSize = 12.5,
            Opacity = 0.75,
            MinWidth = 24,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(initial, min, max),
            StepFrequency = 1,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(slider);
        panel.Children.Add(value);

        slider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            value.Text = v.ToString();
            if (!_ready) return;
            apply(v);
            ApplyNow();
        };

        AddRow(title, subtitle, glyph, panel, () => { });
    }

    private ToggleSwitch AddToggle(string title, string subtitle, string glyph, bool initial,
        Action<bool> apply, Panel? container = null)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = initial,
            Style = (Style)Resources["CleanToggleStyle"],
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            IsHitTestVisible = false,
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Toggled += (_, _) =>
        {
            apply(toggle.IsOn);
            ApplyNow();
        };

        AddRow(title, subtitle, glyph, toggle, () => toggle.IsOn = !toggle.IsOn, container);
        return toggle;
    }

    private void AddCycle(string title, string subtitle, string glyph, string[] labels,
        int initial, Action<int> apply)
    {
        var index = Math.Clamp(initial, 0, labels.Length - 1);
        var value = new TextBlock
        {
            Text = labels[index],
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(value);
        content.Children.Add(new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 10,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center
        });

        var pill = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0x7A, 0xD9, 0xC4)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x7A, 0xD9, 0xC4)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };

        AddRow(title, subtitle, glyph, pill, () =>
        {
            index = (index + 1) % labels.Length;
            value.Text = labels[index];
            apply(index);
            ApplyNow();
        });
    }

    private void AddSlider(string title, string subtitle, string glyph,
        int min, int max, int initial, Action<int> apply)
    {
        // 数值已固定：只展示当前值，不提供可拖动的滑杆
        var value = new TextBlock
        {
            Text = Math.Clamp(initial, min, max).ToString(),
            FontSize = 12.5,
            Opacity = 0.75,
            MinWidth = 24,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        AddRow(title, subtitle, glyph, value, () => { });
    }

    private Button AddRow(string title, string subtitle, string glyph, FrameworkElement control,
        Action activate, Panel? container = null)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        if (!string.IsNullOrEmpty(subtitle))
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11.5,
                Opacity = 0.55,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

        var accent = new Border
        {
            Width = 3,
            Height = 40,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x7A, 0xD9, 0xC4)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconPlate = new Border
        {
            Width = 34,
            Height = 34,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x7A, 0xD9, 0xC4)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x3D, 0x7A, 0xD9, 0xC4)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16 }
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(accent);
        Grid.SetColumn(iconPlate, 1);
        grid.Children.Add(iconPlate);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 3);
        grid.Children.Add(control);

        var row = new Button
        {
            Style = (Style)Resources["SettingRowStyle"],
            BorderBrush = IdleBrush,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x22, 0x7A, 0xD9, 0xC4), Offset = 1 }
                }
            },
            Content = grid
        };
        row.Click += OnRowClicked;

        _rows.Add(row);
        _activate.Add(activate);
        (container ?? Rows).Children.Add(row);
        return row;
    }

    private void OnRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button row) return;
        var index = _rows.IndexOf(row);
        if (index < 0) return;
        _index = index;
        UpdateCursor();
        _activate[index]();
    }

    private void QueueApply()
    {
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    private async void ApplyNow()
    {
        try
        {
            await StatusBarHost.ApplyAsync();
            await DockHost.ApplyAsync();
            if (SettingsService.Current.StartMenuMode && App.Main is { IsShown: true } main) main.ApplySidebarLayout();
            UpdatePreview();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "应用失败：" + ex.Message;
            DebugLog.Write("[顶栏2] 应用设置失败：" + ex.Message);
        }
    }

    private void UpdatePreview()
    {
        var s = SettingsService.Current;
        PreviewBar.Height = s.StatusBarLarge ? 52 : 44;

        switch (Math.Clamp(s.StatusBarBackdropMode, 0, 3))
        {
            case 0:
                PreviewBar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x10, 0x10, 0x14));
                break;
            case 1:
                PreviewBar.Background = new SolidColorBrush(ParseColor(s.StatusBarColor));
                break;
            case 2:
                PreviewBar.Background = new SolidColorBrush(ParseColor(s.StatusBarColor, true));
                break;
            default:
                PreviewBar.Background = new LinearGradientBrush
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

        var textColor = ParseColor(s.StatusBarTextColor);
        if (PreviewBar.Child is Grid g)
        {
            foreach (var inner in g.Children)
                ApplyPreviewForeground(inner, textColor);
        }
    }

    private static void ApplyPreviewForeground(object element, Windows.UI.Color color)
    {
        switch (element)
        {
            case TextBlock t:
                t.Foreground = new SolidColorBrush(color);
                break;
            case FontIcon f:
                f.Foreground = new SolidColorBrush(color);
                break;
            case StackPanel p:
                foreach (var child in p.Children) ApplyPreviewForeground(child, color);
                break;
            case Border b when b.Child is not null:
                ApplyPreviewForeground(b.Child, color);
                break;
        }
    }

    private void UpdateStatus()
    {
        var s = SettingsService.Current;
        Windows.UI.Color color;
        string text;

        if (!s.SystemEnabled)
        {
            text = "未启用";
            color = Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x92);
        }
        else if (StatusBarHost.IsVisible)
        {
            text = "已显示";
            color = Windows.UI.Color.FromArgb(0xFF, 0x6F, 0xE3, 0xA0);
        }
        else
        {
            text = "已开启 · 待显示";
            color = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD1, 0x66);
        }

        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
        PreviewBar.Opacity = s.SystemEnabled ? 1 : 0.45;
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

    private void UpdateCursor()
    {
        // 光标是行描边：撤掉可能残留的系统焦点框，避免行内控件上多出一圈
        FocusRing.HideCurrent(XamlRoot);
        for (var i = 0; i < _rows.Count; i++)
            _rows[i].BorderBrush = i == _index ? FocusBrush : IdleBrush;
        if (_index >= 0 && _index < _rows.Count) _glow?.Show(_rows[_index], 10);
        else _glow?.Hide();
        if (_index < 0 || _index >= _rows.Count) return;

        var row = _rows[_index];
        ContentScroll.UpdateLayout();
        var pos = row.TransformToVisual(ContentScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
        const double margin = 48;
        var target = ContentScroll.VerticalOffset;
        if (pos.Y < margin) target += pos.Y - margin;
        else if (pos.Y + row.ActualHeight > ContentScroll.ViewportHeight - margin)
            target += pos.Y + row.ActualHeight - ContentScroll.ViewportHeight + margin;

        if (_index == 0) target = 0;
        if (_index == _rows.Count - 1) target = ContentScroll.ScrollableHeight;
        target = Math.Clamp(target, 0, ContentScroll.ScrollableHeight);
        ContentScroll.ChangeView(null, target, null, disableAnimation: true);
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready || _rows.Count == 0 || _comboPhase != 0) return;

        if (buttons.HasFlag(GamepadButtons.DPadUp)) Move(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown)) Move(1);
        else if (buttons.HasFlag(GamepadButtons.A))
        {
            if (_index < 0 || _index >= _rows.Count || !_rows[_index].IsEnabled) return;
            _activate[_index]();
        }
    }

    private void Move(int delta)
    {
        var next = _index;
        for (var step = 0; step < _rows.Count; step++)
        {
            next += delta;
            if (next < 0 || next >= _rows.Count) return;
            if (IsRowVisible(_rows[next])) break;
        }
        if (next == _index) return;
        _index = next;
        UpdateCursor();
    }

    private bool IsRowVisible(Button row)
    {
        if (!row.IsEnabled) return false;   // 总开关关闭后，下面的行不参与光标移动
        if (row.Visibility != Visibility.Visible) return false;
        if (!_topControlsExpanded && _topControlsPanel is not null
            && _topControlsPanel.Children.Contains(row)) return false;
        return true;
    }

    public bool IsCapturing => _comboPhase != 0;

    public void CaptureButtons(GamepadButtons pressed)
    {
        if (_comboPhase == 1)
        {
            // 倒计时阶段不记录任何按键（这时手还按在 A 上），只允许 B 取消
            if (pressed.HasFlag(GamepadButtons.B)) CancelComboCapture();
            return;
        }
        if (_comboPhase != 2 || pressed == GamepadButtons.None) return;
        _capturedCombo |= pressed;
        if (_comboText is not null) _comboText.Text = FormatCombo(_capturedCombo);
    }

    public void NotifyAllReleased()
    {
        if (_comboPhase != 2) return;
        if (_capturedCombo != GamepadButtons.None)
        {
            if (_capturedCombo == SettingsService.Current.SummonCombo)
            {
                FloatingToastService.Show("该组合已是启动器呼出键，请换一个", warning: true);
            }
            else
            {
                SettingsService.Update(x => x.DockSummonCombo = _capturedCombo);
                App.Main?.Gamepad.SetDockSummonCombo(_capturedCombo);
                DebugLog.Write($"[系统] 底栏呼出组合键改为 {FormatCombo(_capturedCombo)}");
            }
        }
        FinishComboCapture();
    }
}
