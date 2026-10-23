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
    /// <summary>整体缩放系数（统一缩小徽章与文字）</summary>
    private const double S = 0.85;

    public GamepadHintBar()
    {
        InitializeComponent();
        // 平板模式：每次连接视觉树（含缓存页返回）按设置隐藏提示条；触屏模式的图标本身就是操作入口，必须保留
        Loaded += (_, _) => Visibility = SettingsService.Current.TabletMode && !TouchMode
            ? Visibility.Collapsed : Visibility.Visible;
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
            RootPanel.Spacing = 12;
            foreach (var part in spec.Split('|'))
            {
                var kv = part.Split(':', 2);
                var key = kv[0].Trim();
                var label = kv.Length > 1 ? kv[1].Trim() : "";
                RootPanel.Children.Add(BuildTouchChip(key, label));
            }
            return;
        }
        RootPanel.Spacing = 7 * S;
        foreach (var part in spec.Split('|'))
        {
            var kv = part.Split(':', 2);
            var key = kv[0].Trim();
            var label = kv.Length > 1 ? kv[1].Trim() : "";
            RootPanel.Children.Add(BuildBadge(key));
            if (label.Length > 0)
                RootPanel.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 11 * S,
                    Opacity = 0.75,
                    VerticalAlignment = VerticalAlignment.Center
                });
        }
    }

    /// <summary>触屏徽章：放大的按键图标 + 下方说明，整个区域（含内边距）都是点按热区</summary>
    private FrameworkElement BuildTouchChip(string key, string label)
    {
        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(BuildBadge(key, 1.9));
        if (label.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        var chip = new Border
        {
            // 贴着弹窗底边的两组底角改为直角，让徽章背景融进窗口下边缘
            CornerRadius = new CornerRadius(14, 14, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(10, 6, 10, 2),
            Child = stack
        };
        chip.Tapped += (_, _) => HintTapped?.Invoke(key);
        return chip;
    }

    /// <summary>按按键标识生成对应徽章；无对应按键图标的中文词渲染为文字胶囊</summary>
    private static FrameworkElement BuildBadge(string key, double scale = S) => key.ToUpperInvariant() switch
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
    private static Border Circle(string text, Color fill, double scale = S, bool darkText = false)
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
    private static Border Pill(string content, bool icon = false, double scale = S)
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
