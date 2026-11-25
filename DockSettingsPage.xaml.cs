using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GameLocker;

/// <summary>
/// 「底栏」设置页：开关与尺寸直接落盘到 settings.json，并立即重建底栏。
/// 版式与「常规 / 顶栏」等设置子页保持一致：统一标题、预览条和渐变设置卡片。
/// </summary>
public sealed partial class DockSettingsPage : Page, ISettingsGamepad
{
    private readonly List<Button> _rows = [];
    private readonly List<Action> _activate = [];

    private int _index;
    private bool _ready;

    private static readonly SolidColorBrush FocusBrush =
        new(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush IdleBrush =
        new(Microsoft.UI.Colors.Transparent);

    public DockSettingsPage()
    {
        InitializeComponent();
        // 设置项行用手绘白框当光标：子控件拿到焦点时不再画系统焦点框
        FocusRing.Attach(this);
        Build();
        Loaded += (_, _) => { _ready = true; UpdatePreview(); UpdateCursor(); };
        Unloaded += (_, _) => _ready = false;
    }

    private void Build()
    {
        var s = SettingsService.Current;

        AddCycle("外观主题", "深浅玻璃 / 纯色 / 渐变 / 高透",
            "\uE790",
            DockThemes.Names,
            Math.Clamp(s.DockTheme, 0, DockThemes.All.Length - 1),
            v => SettingsService.Update(x => x.DockTheme = v));

        AddToggle("悬停放大", "鼠标经过时图标弹簧放大", "\uE962",
            s.DockMagnify,
            v => SettingsService.Update(x => x.DockMagnify = v));

        AddToggle("按压反馈", "按下图标时轻微缩小", "\uE7FC",
            s.DockPressFeedback,
            v => SettingsService.Update(x => x.DockPressFeedback = v));

        AddToggle("任务栏隐藏", "彻底隐藏任务栏并铺满全屏，不能再唤出", "\uE7F4",
            s.DockTaskbarMode == 2,
            v => SettingsService.Update(x => x.DockTaskbarMode = v ? 2 : 0));

        AddToggle("自动隐藏", "鼠标离开后收起，留一条边缘触发带", "\uE8AB",
            s.DockAutoHide,
            v => SettingsService.Update(x => x.DockAutoHide = v));

        AddDockSizeRows(s);
    }

    /// <summary>循环切换型选项：点一下切下一个值。</summary>
    private void AddCycle(string title, string subtitle, string glyph, string[] labels, int initial, Action<int> apply)
    {
        var index = Math.Clamp(initial, 0, labels.Length - 1);
        var value = new TextBlock
        {
            Text = labels[index],
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 10,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(value);
        content.Children.Add(chevron);

        var pill = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0x4C, 0xC2, 0xFF)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x4C, 0xC2, 0xFF)),
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
            ApplyAsync();
        });
    }

    private void AddToggle(string title, string subtitle, string glyph, bool initial, Action<bool> apply)
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
            ApplyAsync();
        };

        AddRow(title, subtitle, glyph, toggle, () =>
        {
            toggle.IsOn = !toggle.IsOn;
        });
    }

    /// <summary>
    /// 图标大小一行：条高不再单独设置，完全由图标大小推导（悬停放大到 1.18× 与运行指示都放得下），
    /// 右侧顺手把推导出来的条高显示出来——调大图标条高自动变高，调小自动变矮。
    /// </summary>
    private void AddDockSizeRows(AppSettings s)
    {
        var iconValue = new TextBlock
        {
            Text = s.DockIconSize.ToString(),
            FontSize = 12.5,
            Opacity = 0.75,
            MinWidth = 24,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var heightHint = new TextBlock
        {
            FontSize = 10.5,
            Opacity = 0.5,
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconSlider = new Slider
        {
            Minimum = AppSettings.DockIconMin,
            Maximum = AppSettings.DockIconMax,
            Value = s.DockIconSize,
            StepFrequency = 1,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 滑杆跟随主题蓝，避免使用系统强调色（可能是橙/紫等），与其它设置页的蓝色控件一致
        var sliderAccent = Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF);
        var sliderAccentHover = Windows.UI.Color.FromArgb(0xFF, 0x7A, 0xD0, 0xFF);
        var sliderAccentPressed = Windows.UI.Color.FromArgb(0xFF, 0xA6, 0xE0, 0xFF);
        var sliderTrack = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var sliderTrackHover = new SolidColorBrush(Windows.UI.Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
        var sliderTrackPressed = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        iconSlider.Resources["SliderThumbBackground"] = new SolidColorBrush(sliderAccent);
        iconSlider.Resources["SliderThumbBackgroundPointerOver"] = new SolidColorBrush(sliderAccentHover);
        iconSlider.Resources["SliderThumbBackgroundPressed"] = new SolidColorBrush(sliderAccentPressed);
        iconSlider.Resources["SliderOuterThumbBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        iconSlider.Resources["SliderThumbBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        iconSlider.Resources["SliderTrackValueFill"] = new SolidColorBrush(sliderAccent);
        iconSlider.Resources["SliderTrackValueFillPointerOver"] = new SolidColorBrush(sliderAccentHover);
        iconSlider.Resources["SliderTrackValueFillPressed"] = new SolidColorBrush(sliderAccentPressed);
        iconSlider.Resources["SliderTrackFill"] = sliderTrack;
        iconSlider.Resources["SliderTrackFillPointerOver"] = sliderTrackHover;
        iconSlider.Resources["SliderTrackFillPressed"] = sliderTrackPressed;

        void RefreshHeightHint(int iconSize)
        {
            var current = SettingsService.Current;
            var extra = AppSettings.DockIndicatorExtra(current.DockShowRunning, current.DockIndicatorStyle,
                current.DockIndicatorSize);
            heightHint.Text = $"条高 {AppSettings.DockBarHeight(iconSize, extra)}";
        }

        iconSlider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            iconValue.Text = v.ToString();
            RefreshHeightHint(v);
            SettingsService.Update(x => x.DockIconSize = v);
            UpdatePreview();
            ApplyAsync();
        };

        RefreshHeightHint(s.DockIconSize);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(iconSlider);
        panel.Children.Add(iconValue);
        panel.Children.Add(heightHint);
        AddRow("图标大小", "条高随图标自动缩放", "\uE740", panel, () => { });
    }

    /// <summary>统一的设置行：左侧强调条 + 图标底盘 + 标题/说明，右侧放开关、循环值或滑杆。</summary>
    private void AddRow(string title, string subtitle, string glyph, FrameworkElement control, Action activate)
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
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x4C, 0xC2, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconPlate = new Border
        {
            Width = 34,
            Height = 34,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x4C, 0xC2, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x3D, 0x4C, 0xC2, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = glyph, FontSize = 16 }
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
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x22, 0x4C, 0xC2, 0xFF), Offset = 1 }
                }
            },
            Content = grid
        };
        row.Click += OnRowClicked;

        _rows.Add(row);
        _activate.Add(activate);
        Rows.Children.Add(row);
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

    private async void ApplyAsync()
    {
        try
        {
            await DockHost.ApplyAsync();
            UpdatePreview();
        }
        catch (Exception ex)
        {
            StatusText.Text = "应用失败：" + ex.Message;
            DebugLog.Write("[底栏] 设置应用失败：" + ex);
        }
    }

    /// <summary>刷新预览条：跟随主题、图标尺寸，并同步右上角的运行状态。</summary>
    private void UpdatePreview()
    {
        var s = SettingsService.Current;
        ApplyPreviewTheme(DockThemes.Get(s.DockTheme));
        ApplyPreviewSize(s.DockIconSize);
        UpdateStatus();
    }

    private void ApplyPreviewTheme(DockTheme theme)
    {
        var bar = new SolidColorBrush(ParseColor(theme.Bar));
        if (string.IsNullOrEmpty(theme.Bar2))
        {
            PreviewBar.Background = bar;
        }
        else
        {
            PreviewBar.Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = ParseColor(theme.Bar), Offset = 0 },
                    new GradientStop { Color = ParseColor(theme.Bar2!), Offset = 1 }
                }
            };
        }
        PreviewBar.BorderBrush = new SolidColorBrush(ParseColor(theme.Highlight));

        var plate = new SolidColorBrush(ParseColor(theme.IconPlate));
        PreviewStartPlate.Background = plate;
        PreviewTile1.Background = plate;
        PreviewTile2.Background = plate;
        PreviewTile3.Background = plate;
        PreviewTile4.Background = plate;
    }

    private void ApplyPreviewSize(int iconSize)
    {
        // 真实底栏 36–64 px，预览按约一半缩放，避免撑破预览条
        var size = Math.Clamp(iconSize * 0.55, 22, 38);
        PreviewStartPlate.Width = size;
        PreviewStartPlate.Height = size;
        PreviewStartIcon.FontSize = size * 0.56;
        foreach (var tile in new[] { PreviewTile1, PreviewTile2, PreviewTile3, PreviewTile4 })
        {
            tile.Width = size;
            tile.Height = size;
        }
    }

    private void UpdateStatus()
    {
        var s = SettingsService.Current;
        string text;
        Windows.UI.Color color;
        double opacity;

        if (!s.SystemEnabled)
        {
            text = "已关闭";
            color = Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x92);
            opacity = 0.42;
        }
        else if (DockHost.IsVisible)
        {
            text = "已显示";
            color = Windows.UI.Color.FromArgb(0xFF, 0x6F, 0xE3, 0xA0);
            opacity = 1;
        }
        else
        {
            text = "已启用";
            color = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD1, 0x66);
            opacity = 0.78;
        }

        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
        PreviewBar.Opacity = opacity;
    }

    private static Windows.UI.Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        try
        {
            return hex.Length switch
            {
                8 => Windows.UI.Color.FromArgb(
                    Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16), Convert.ToByte(hex.Substring(6, 2), 16)),
                6 => Windows.UI.Color.FromArgb(0xFF,
                    Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16)),
                _ => Microsoft.UI.Colors.Transparent
            };
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
        if (_index >= 0 && _index < _rows.Count) _rows[_index].StartBringIntoView();
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready || _rows.Count == 0) return;

        if (buttons.HasFlag(GamepadButtons.DPadUp)) _index = Math.Max(0, _index - 1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown)) _index = Math.Min(_rows.Count - 1, _index + 1);
        else if (buttons.HasFlag(GamepadButtons.A))
        {
            _activate[_index]();
            return;
        }
        UpdateCursor();
    }

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
