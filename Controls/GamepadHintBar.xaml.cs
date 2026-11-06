using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using GameLocker.Services;

namespace GameLocker.Controls;

/// <summary>
/// 底部手柄按键提示条：按键用图标徽章表示（A/B/X/Y 为对应颜色的圆形按键，
/// LB/RB/L3/R3 等为控制器上实际印字的胶囊徽章，方向键为十字、View/Menu 为图形），
/// 说明文字缩小、单行居中、背景透明。通过 SetHints("A:启动|X:添加") 一行配置。
/// </summary>
public sealed partial class GamepadHintBar : UserControl
{
    /// <summary>默认整体缩放系数（统一缩小徽章与文字），页面可用 Scale 单独放大</summary>
    private const double DefaultScale = 0.85;

    private double _scale = DefaultScale;

    public GamepadHintBar()
    {
        InitializeComponent();
        // 平板模式：每次连接视觉树（含缓存页返回）按设置隐藏提示条；触屏模式的图标本身就是操作入口，必须保留
        Loaded += (_, _) => Visibility = SettingsService.Current.TabletMode && !TouchMode
            ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>缩放系数：徽章直径与说明文字一起放大/缩小（默认 0.85，主页悬浮提示条用 1.0）</summary>
    public static readonly DependencyProperty BadgeScaleProperty = DependencyProperty.Register(
        nameof(BadgeScale), typeof(double), typeof(GamepadHintBar),
        new PropertyMetadata(DefaultScale, (d, e) =>
        {
            var bar = (GamepadHintBar)d;
            var value = (double)e.NewValue;
            bar._scale = value > 0 ? value : DefaultScale;
            bar.SetHints(bar.Hints);
        }));

    public double BadgeScale
    {
        get => (double)GetValue(BadgeScaleProperty);
        set => SetValue(BadgeScaleProperty, value);
    }

    /// <summary>XAML 声明式配置入口（等价于调用 SetHints）</summary>
    public static readonly DependencyProperty HintsProperty = DependencyProperty.Register(
        nameof(Hints), typeof(string), typeof(GamepadHintBar),
        new PropertyMetadata("", (d, e) => ((GamepadHintBar)d).SetHints((string)e.NewValue)));

    public string Hints
    {
        get => (string)GetValue(HintsProperty);
        set => SetValue(HintsProperty, value);
    }

    /// <summary>触屏模式：图标放大为可点按的徽章，并在平板模式下也保持显示</summary>
    public static readonly DependencyProperty TouchModeProperty = DependencyProperty.Register(
        nameof(TouchMode), typeof(bool), typeof(GamepadHintBar),
        new PropertyMetadata(false, (d, e) =>
        {
            var bar = (GamepadHintBar)d;
            bar.Visibility = SettingsService.Current.TabletMode && !(bool)e.NewValue
                ? Visibility.Collapsed : Visibility.Visible;
            bar.SetHints(bar.Hints);
        }));

    public bool TouchMode
    {
        get => (bool)GetValue(TouchModeProperty);
        set => SetValue(TouchModeProperty, value);
    }

    /// <summary>触屏点按某个图标（参数为按键标识 A/B/X/Y/DPAD/VIEW/MENU/LB/RB/L3/R3），
    /// 弹窗据此执行与手柄按键完全相同的指令</summary>
    public event Action<string>? HintTapped;

    /// <summary>把提示条按键标识转成手柄按键位：DPAD 图标按"下一项"处理（触屏点按即循环选择）</summary>
    public static GamepadButtons ToGamepadButtons(string key) => key.ToUpperInvariant() switch
    {
        "A" => GamepadButtons.A,
        "B" => GamepadButtons.B,
        "X" => GamepadButtons.X,
        "Y" => GamepadButtons.Y,
        "DPAD" => GamepadButtons.DPadDown,
        "VIEW" => GamepadButtons.View,
        "MENU" => GamepadButtons.Menu,
        "LB" => GamepadButtons.LeftShoulder,
        "RB" => GamepadButtons.RightShoulder,
        "L3" => GamepadButtons.LeftThumb,
        "R3" => GamepadButtons.RightThumb,
        _ => GamepadButtons.None
    };

    /// <summary>用竖线分隔的多组 "键:说明" 配置提示内容，说明可省略（只显示按键图标）；键名为中文时渲染为文字胶囊徽章</summary>
    public void SetHints(string spec)
    {
        RootPanel.Children.Clear();
        if (TouchMode)
        {
            // 触屏/浏览页与主页提示条同款排版：图标 + 右侧说明单行排列，不加单独的胶囊底色
            RootPanel.Spacing = 7;
            foreach (var part in spec.Split('|'))
            {
                var kv = part.Split(':', 2);
                var key = kv[0].Trim();
                var label = kv.Length > 1 ? kv[1].Trim() : "";
                RootPanel.Children.Add(BuildTouchChip(key, label));
            }
            return;
        }
        RootPanel.Spacing = 7 * _scale;
        foreach (var part in spec.Split('|'))
        {
            var kv = part.Split(':', 2);
            var key = kv[0].Trim();
            var label = kv.Length > 1 ? kv[1].Trim() : "";
            RootPanel.Children.Add(BuildBadge(key, _scale));
            if (label.Length > 0)
                RootPanel.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 11 * _scale,
                    Opacity = 0.75,
                    VerticalAlignment = VerticalAlignment.Center
                });
        }
    }

    /// <summary>触屏徽章：与主页提示条相同的"图标 + 右侧说明"单行样式，整行可点按</summary>
    private FrameworkElement BuildTouchChip(string key, string label)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        // 图标与主页提示条完全一致（同尺寸徽章、不加触屏放大版）
        row.Children.Add(BuildBadge(key, 1.0));
        if (label.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center
            });
        row.Tapped += (_, _) => HintTapped?.Invoke(key);
        return row;
    }

    /// <summary>按按键标识生成对应徽章；无对应按键图标的中文词渲染为文字胶囊</summary>
    private static FrameworkElement BuildBadge(string key, double scale = DefaultScale) => key.ToUpperInvariant() switch
    {
        "A" => Circle("A", ColorFromRgb(0x10, 0x7C, 0x10), scale),   // 绿
        "B" => Circle("B", ColorFromRgb(0xD8, 0x3B, 0x01), scale),   // 红
        "X" => Circle("X", ColorFromRgb(0x00, 0x78, 0xD4), scale),   // 蓝
        "Y" => Circle("Y", ColorFromRgb(0xFF, 0xB9, 0x00), scale, darkText: true), // 黄
        "DPAD" => Pill("\u271A", scale: scale),                              // ✚ 十字方向键
        "VIEW" => Pill("\uE713", icon: true, scale: scale),                  // 齿轮（View=进入设置）
        "MENU" => Pill("\uE700", icon: true, scale: scale),                  // ≡ 菜单（Menu/Start）
        "L3" => Pill("L3", scale: scale),
        "R3" => Pill("R3", scale: scale),
        "LB" => Pill("LB", scale: scale),
        "RB" => Pill("RB", scale: scale),
        _ => Pill(key, scale: scale)
    };

    private static Color ColorFromRgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    /// <summary>白色（Windows.UI/Microsoft.UI 两个 Colors 类同名，避免歧义直接构造）</summary>
    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>圆形按键徽章（A/B/X/Y）：彩色圆 + 居中字母</summary>
    private static Border Circle(string text, Color fill, double scale = DefaultScale, bool darkText = false)
    {
        var d = 18 * scale;
        return new Border
        {
            Width = d,
            Height = d,
            CornerRadius = new CornerRadius(d / 2),
            Background = new SolidColorBrush(fill),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11 * scale,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(darkText ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20) : White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>胶囊徽章（LB/RB/L3/R3/方向/View/Menu）：半透明底 + 居中文字或图形</summary>
    private static Border Pill(string content, bool icon = false, double scale = DefaultScale)
    {
        return new Border
        {
            Padding = new Thickness(5 * scale, 0.5 * scale, 5 * scale, 1.5 * scale),
            CornerRadius = new CornerRadius(9 * scale),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = content,
                FontSize = (icon ? 11 : 10) * scale,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily(icon ? "Segoe MDL2 Assets" : "Segoe UI"),
                Foreground = new SolidColorBrush(White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }
}
