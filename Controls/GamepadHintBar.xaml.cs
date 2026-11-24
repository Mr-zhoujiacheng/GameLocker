using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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

    /// <summary>触屏徽章样式：true=可点按的胶囊底座（弹窗内使用），false=极简单行（页面使用）</summary>
    public static readonly DependencyProperty CapsuleChipsProperty = DependencyProperty.Register(
        nameof(CapsuleChips), typeof(bool), typeof(GamepadHintBar),
        new PropertyMetadata(false, (d, e) =>
        {
            var bar = (GamepadHintBar)d;
            bar.SetHints(bar.Hints);
        }));

    public bool CapsuleChips
    {
        get => (bool)GetValue(CapsuleChipsProperty);
        set => SetValue(CapsuleChipsProperty, value);
    }

    /// <summary>高亮键帽：键帽加彩色光晕、胶囊加亮边，说明文字提亮（主页悬浮提示条使用）</summary>
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight), typeof(bool), typeof(GamepadHintBar),
        new PropertyMetadata(false, (d, e) => ((GamepadHintBar)d).SetHints(((GamepadHintBar)d).Hints)));

    public bool Highlight
    {
        get => (bool)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
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

    /// <summary>提示条文字统一用像素字体（与"像素街机"主题、像素猫娘同一套语言；不用于 MDL2 图标）</summary>
    private static readonly FontFamily PixelFont =
        new("ms-appx:///Assets/Fonts/fusion-pixel-10px-proportional-zh_hans.ttf#Fusion Pixel 10px Prop zh-Hans");

    /// <summary>用竖线分隔的多组 "键:说明" 配置提示内容，说明可省略（只显示按键图标）；键名为中文时渲染为文字胶囊徽章</summary>
    public void SetHints(string spec)
    {
        RootPanel.Children.Clear();
        if (TouchMode)
        {
            RootPanel.Spacing = CapsuleChips ? 12 : 7;
            foreach (var part in spec.Split('|'))
            {
                var kv = part.Split(':', 2);
                var key = kv[0].Trim();
                var label = kv.Length > 1 ? kv[1].Trim() : "";
                RootPanel.Children.Add(BuildTouchChip(key, label));
            }
            return;
        }
        RootPanel.Spacing = (Highlight ? 9 : 7) * _scale;
        foreach (var part in spec.Split('|'))
        {
            var kv = part.Split(':', 2);
            var key = kv[0].Trim();
            var label = kv.Length > 1 ? kv[1].Trim() : "";
            RootPanel.Children.Add(BuildBadge(key, _scale, Highlight));
            if (label.Length > 0)
                RootPanel.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = (Highlight ? 12.5 : 11) * _scale,
                    FontFamily = PixelFont,
                    FontWeight = Highlight ? FontWeights.SemiBold : FontWeights.Normal,
                    Opacity = Highlight ? 0.95 : 0.75,
                    VerticalAlignment = VerticalAlignment.Center
                });
        }
    }

    /// <summary>触屏徽章：弹窗用"放大图标 + 下方说明"的可点按胶囊；页面用与主页一致的极简单行</summary>
    private FrameworkElement BuildTouchChip(string key, string label)
    {
        if (CapsuleChips)
            return BuildCapsuleChip(key, label);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(BuildBadge(key, 1.0));
        if (label.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontFamily = PixelFont,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center
            });
        row.Tapped += (_, _) => HintTapped?.Invoke(key);
        return row;
    }

    /// <summary>弹窗内的胶囊徽章：放大的按键图标 + 下方说明，整个区域（含内边距）都是点按热区</summary>
    private FrameworkElement BuildCapsuleChip(string key, string label)
    {
        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(BuildBadge(key, 1.9));
        if (label.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                FontFamily = PixelFont,
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

    /// <summary>按按键标识生成对应徽章：所有按键一律同直径圆形（字母、十字、肩键、摇杆、View/Menu），
    /// "LB/RB" 这类"A 或 B"的组合键拆成并排两个圆；只有中文等非按键文字才退回胶囊。</summary>
    internal static FrameworkElement BuildBadge(string key, double scale = DefaultScale, bool highlight = false)
    {
        var parts = key.ToUpperInvariant()
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return Pill(key, scale: scale, highlight: highlight);
        if (parts.Length == 1) return BuildKeyBadge(parts[0], scale, highlight);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1.5 * scale,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        foreach (var part in parts) row.Children.Add(BuildKeyBadge(part, scale, highlight));
        return row;
    }

    private static FrameworkElement BuildKeyBadge(string key, double scale, bool highlight) => key switch
    {
        "A" => LetterCircle("A", ColorFromRgb(0x10, 0x7C, 0x10), scale, highlight),            // 绿
        "B" => LetterCircle("B", ColorFromRgb(0xD8, 0x3B, 0x01), scale, highlight),            // 红
        "X" => LetterCircle("X", ColorFromRgb(0x00, 0x78, 0xD4), scale, highlight),            // 蓝
        "Y" => LetterCircle("Y", ColorFromRgb(0xFF, 0xB9, 0x00), scale, highlight, true),      // 黄（深字）
        "DPAD" => CrossCircle(scale, highlight),                                              // ✚ 十字方向键
        "VIEW" => GlyphCircle("\uE713", 0.62, scale, highlight),                               // 齿轮（View=设置）
        "MENU" => GlyphCircle("\uE700", 0.62, scale, highlight),                               // ≡ 菜单
        "UD" => GlyphCircle("\u2195", 0.60, scale, highlight),                                 // ↕
        "LR" => GlyphCircle("\u2194", 0.60, scale, highlight),                                 // ↔
        "RS" => StickCircle(scale, highlight),                                                // 右摇杆
        "LB" or "RB" or "L3" or "R3" => WordCircle(key, scale, highlight),                     // 两字母按键盘位
        _ => Pill(key, scale: scale, highlight: highlight)                                     // 中文等文字仍是胶囊
    };

    /// <summary>圆形底座：固定直径、无内边距，内容在正中</summary>
    private static Border BadgeCircle(double scale, bool highlight, Color? fill = null)
    {
        var d = 18 * scale;
        return new Border
        {
            Width = d,
            Height = d,
            CornerRadius = new CornerRadius(d / 2),
            Background = new SolidColorBrush(fill ??
                Color.FromArgb(highlight ? (byte)0x1F : (byte)0x33, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    /// <summary>字母圆章（A/B/X/Y）：字形盒压成 1 em 再居中，视觉上才是正中间</summary>
    private static FrameworkElement LetterCircle(string text, Color fill, double scale, bool highlight,
        bool darkText = false) =>
        Fill(BadgeCircle(scale, highlight, fill), MakeText(text, 11 * scale, highlight,
            darkText ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20) : White, FontWeights.Bold));

    /// <summary>两字母圆章（LB/RB/L3/R3）：字号缩到能塞进圆里，同样居中</summary>
    private static FrameworkElement WordCircle(string text, double scale, bool highlight) =>
        Fill(BadgeCircle(scale, highlight), MakeText(text, 7.4 * scale, highlight, White, FontWeights.Bold));

    /// <summary>图标圆章（View/Menu/上下左右）：MDL2 字形居中</summary>
    private static FrameworkElement GlyphCircle(string glyph, double emRatio, double scale, bool highlight) =>
        Fill(BadgeCircle(scale, highlight), new TextBlock
        {
            Text = glyph,
            FontSize = 18 * scale * emRatio,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Foreground = new SolidColorBrush(White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

    /// <summary>十字键：两条线段画出来的"＋"，几何居中，不受字体基线影响</summary>
    private static FrameworkElement CrossCircle(double scale, bool highlight)
    {
        var circle = BadgeCircle(scale, highlight);
        var d = 18 * scale;
        var arm = d * 0.52;
        var thick = Math.Max(1.2, d * 0.11);
        var cross = new Grid();
        cross.Children.Add(new Rectangle
        {
            Width = arm, Height = thick, RadiusX = thick / 2, RadiusY = thick / 2,
            Fill = new SolidColorBrush(White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        cross.Children.Add(new Rectangle
        {
            Width = thick, Height = arm, RadiusX = thick / 2, RadiusY = thick / 2,
            Fill = new SolidColorBrush(White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return Fill(circle, cross);
    }

    /// <summary>右摇杆：圆环 + 中心点，外框同样是圆章</summary>
    private static FrameworkElement StickCircle(double scale, bool highlight)
    {
        var d = 18 * scale;
        var ringSize = d * 0.62;
        var ring = new Border
        {
            Width = ringSize,
            Height = ringSize,
            CornerRadius = new CornerRadius(ringSize / 2),
            BorderBrush = new SolidColorBrush(White),
            BorderThickness = new Thickness(Math.Max(1, d * 0.075)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                Width = d * 0.22,
                Height = d * 0.22,
                CornerRadius = new CornerRadius(d * 0.11),
                Background = new SolidColorBrush(White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        return Fill(BadgeCircle(scale, highlight), ring);
    }

    /// <summary>把内容塞进圆章：Grid 铺满，内容自带居中对齐</summary>
    private static FrameworkElement Fill(Border circle, FrameworkElement content)
    {
        circle.Child = content;
        return circle;
    }

    /// <summary>圆章里的文字：靠居中对齐 + 一点上移补偿（字体行盒的降部留白会让字看着偏下）</summary>
    private static TextBlock MakeText(string text, double fontSize, bool highlight, Color color,
        Windows.UI.Text.FontWeight weight) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontWeight = weight,
        FontFamily = new FontFamily("Segoe UI"),
        Foreground = new SolidColorBrush(color),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, -fontSize * 0.05, 0, 0)
    };


    private static Color ColorFromRgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    /// <summary>白色（Windows.UI/Microsoft.UI 两个 Colors 类同名，避免歧义直接构造）</summary>
    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>胶囊徽章（中文等非按键文字）：半透明底 + 居中文字</summary>
    private static Border Pill(string content, bool icon = false, double scale = DefaultScale,
        double fontScale = 1, bool highlight = false)
    {
        return new Border
        {
            Padding = new Thickness(5 * scale, 0.5 * scale, 5 * scale, 1.5 * scale),
            CornerRadius = new CornerRadius(9 * scale),
            // 主页提示条（highlight）：极淡底、无描边，靠文字本身醒目
            Background = new SolidColorBrush(Color.FromArgb(highlight ? (byte)0x1F : (byte)0x33, 0xFF, 0xFF, 0xFF)),
            BorderBrush = null,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = content,
                FontSize = (icon ? 11 : 10) * scale * fontScale,
                FontWeight = FontWeights.SemiBold,
                FontFamily = icon ? new FontFamily("Segoe MDL2 Assets") : PixelFont,
                Foreground = new SolidColorBrush(White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }
}
