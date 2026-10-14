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
        // 平板模式：每次连接视觉树（含缓存页返回）按设置隐藏提示条；关闭时恢复
        Loaded += (_, _) => Visibility = SettingsService.Current.TabletMode
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

    /// <summary>用竖线分隔的多组 "键:说明" 配置提示内容，说明可省略（只显示按键图标）；键名为中文时渲染为文字胶囊徽章</summary>
    public void SetHints(string spec)
    {
        RootPanel.Children.Clear();
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

    /// <summary>按按键标识生成对应徽章；无对应按键图标的中文词渲染为文字胶囊</summary>
    private static FrameworkElement BuildBadge(string key) => key.ToUpperInvariant() switch
    {
        "A" => Circle("A", ColorFromRgb(0x10, 0x7C, 0x10)),   // 绿
        "B" => Circle("B", ColorFromRgb(0xD8, 0x3B, 0x01)),   // 红
        "X" => Circle("X", ColorFromRgb(0x00, 0x78, 0xD4)),   // 蓝
        "Y" => Circle("Y", ColorFromRgb(0xFF, 0xB9, 0x00), darkText: true), // 黄
        "DPAD" => Pill("\u271A"),                              // ✚ 十字方向键
        "VIEW" => Pill("\uE713", icon: true),                  // 齿轮（View=进入设置）
        "MENU" => Pill("\uE700", icon: true),                  // ≡ 菜单（Menu/Start）
        "L3" => Pill("L3"),
        "R3" => Pill("R3"),
        "LB" => Pill("LB"),
        "RB" => Pill("RB"),
        _ => Pill(key)
    };

    private static Color ColorFromRgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    /// <summary>白色（Windows.UI/Microsoft.UI 两个 Colors 类同名，避免歧义直接构造）</summary>
    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>圆形按键徽章（A/B/X/Y）：彩色圆 + 居中字母</summary>
    private static Border Circle(string text, Color fill, bool darkText = false)
    {
        var d = 18 * S;
        return new Border
        {
            Width = d,
            Height = d,
            CornerRadius = new CornerRadius(d / 2),
            Background = new SolidColorBrush(fill),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11 * S,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(darkText ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20) : White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>胶囊徽章（LB/RB/L3/R3/方向/View/Menu）：半透明底 + 居中文字或图形</summary>
    private static Border Pill(string content, bool icon = false)
    {
        return new Border
        {
            Padding = new Thickness(5 * S, 0.5 * S, 5 * S, 1.5 * S),
            CornerRadius = new CornerRadius(9 * S),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = content,
                FontSize = (icon ? 11 : 10) * S,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily(icon ? "Segoe MDL2 Assets" : "Segoe UI"),
                Foreground = new SolidColorBrush(White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }
}
