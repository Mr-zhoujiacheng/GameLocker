using GameLocker.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GameLocker.Services;

/// <summary>
/// 磁贴视觉样式统一出口：尺寸单位、间距、配色、立体渐变/边缘、音乐小组件磁贴构建。
/// 主页磁贴与小组件页预览共用同一套常量与方法，保证外观完全一致。
/// </summary>
public static class TileStyle
{
    /// <summary>磁贴基本单位（1×1 格边长，像素）</summary>
    public const double Unit = 84;

    /// <summary>磁贴圆角（按用户设置；未设置时彩色主题圆角、其余主题直角）</summary>
    public static double Radius => IsSquareRadius ? 0 : 6;

    /// <summary>是否为 Win10 磁贴主题（无颜色、隐藏磁贴颜色设置）</summary>
    public static bool IsWin10Theme => SettingsService.Current.TileTheme == TileTheme.Win10;

    /// <summary>是否为渐变磁贴主题（自身颜色斜向亮暗渐变）</summary>
    public static bool IsGradientTheme => SettingsService.Current.TileTheme == TileTheme.Gradient;

    /// <summary>是否为玻璃磁贴主题（磨砂底 + 底部自身颜色柔光/色条）</summary>
    public static bool IsGlassTheme => SettingsService.Current.TileTheme == TileTheme.Glass;

    /// <summary>是否为透明磁贴主题（所有磁贴无底色，隐藏磁贴颜色设置）</summary>
    public static bool IsTransparentTheme => SettingsService.Current.TileTheme == TileTheme.Transparent;

    /// <summary>是否直角（方形）：显式设置优先，未设置时按主题默认</summary>
    public static bool IsSquareRadius =>
        (SettingsService.Current.TileCornerShape
         ?? (IsWin10Theme || IsGradientTheme || IsGlassTheme || IsTransparentTheme
             ? TileCorner.Square
             : TileCorner.Rounded)) == TileCorner.Square;

    /// <summary>是否隐藏磁贴颜色设置（简约/透明主题固定无色）</summary>
    public static bool HidesTileColor => IsWin10Theme || IsTransparentTheme;

    /// <summary>Win10 磁贴的固定底色（不随磁贴颜色设置变化）</summary>
    public static readonly Windows.UI.Color Win10TileColor =
        Windows.UI.Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B);

    /// <summary>磁贴间距（像素，可在外观设置调节）</summary>
    public static double Gap => Math.Clamp(SettingsService.Current.TileSpacing, 0, 30);

    /// <summary>磁贴默认透明度（0.3~1.0）</summary>
    public static double Opacity => Math.Clamp(SettingsService.Current.TileOpacity, 0.3, 1.0);

    /// <summary>磁贴强调色（Win10 风格纯色块，与深色背景区分）</summary>
    public static readonly Windows.UI.Color[] Accents =
    [
        Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4), // 蓝
        Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x3D, 0x2F), // 红
        Windows.UI.Color.FromArgb(0xFF, 0x10, 0x8A, 0x45), // 绿
        Windows.UI.Color.FromArgb(0xFF, 0x74, 0x4D, 0xA9), // 紫
        Windows.UI.Color.FromArgb(0xFF, 0xC2, 0x3E, 0x1B), // 橙
        Windows.UI.Color.FromArgb(0xFF, 0x00, 0xB2, 0x94), // 青
        Windows.UI.Color.FromArgb(0xFF, 0xB4, 0x00, 0x9E), // 品红
        Windows.UI.Color.FromArgb(0xFF, 0x49, 0x82, 0xD9), // 浅蓝
    ];

    /// <summary>常驻磁贴（设置 / 电源）：固定小尺寸、固定配色，不参与随机配色/"无颜色"，不可编辑与删除</summary>
    public static bool IsFixedTile(LaunchItem item) =>
        item.Kind == LaunchKind.Widget && item.WidgetKind is WidgetKind.Settings or WidgetKind.Power;

    /// <summary>设置磁贴独有灰色（固定，不随随机配色/无颜色设置变化）</summary>
    public static readonly Windows.UI.Color SettingsAccent = Windows.UI.Color.FromArgb(0xFF, 0x4E, 0x55, 0x60);

    /// <summary>电源磁贴独有红色（固定，不随随机配色/无颜色设置变化）</summary>
    public static readonly Windows.UI.Color PowerAccent = Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);

    /// <summary>无颜色磁贴：显式选"none"，或未指定颜色且随机配色开关关闭（只显示图标或图片）；
    /// 常驻磁贴始终有色；Win10 主题统一无颜色（但保留名称）</summary>
    public static bool IsNoColor(LaunchItem item) =>
        !IsWin10Theme
        && !IsFixedTile(item)
        && (string.Equals(item.TileColor, "none", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(item.TileColor) && !SettingsService.Current.RandomTileColor));

    /// <summary>解析 #RRGGBB 磁贴颜色，空/非法返回 null（回退哈希自动配色）</summary>
    public static Windows.UI.Color? ParseTileColor(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#')
        {
            try
            {
                byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                return Windows.UI.Color.FromArgb(0xFF, r, g, b);
            }
            catch { }
        }
        return null;
    }

    /// <summary>未指定颜色磁贴的随机配色缓存（同一磁贴本次运行内保持稳定，避免重排时闪色）</summary>
    private static readonly Dictionary<string, Windows.UI.Color> _randomAccents = [];

    /// <summary>磁贴强调色：常驻磁贴固定色 → 指定色 → 随机色缓存（按 Target+Name 稳定）</summary>
    public static Windows.UI.Color AccentFor(LaunchItem item)
    {
        if (item.Kind == LaunchKind.Widget)
        {
            if (item.WidgetKind == WidgetKind.Settings) return SettingsAccent;
            if (item.WidgetKind == WidgetKind.Power) return PowerAccent;
        }
        var parsed = ParseTileColor(item.TileColor);
        if (parsed != null) return parsed.Value;
        var key = item.Target + "|" + item.Name;
        if (!_randomAccents.TryGetValue(key, out var c))
        {
            c = Accents[Random.Shared.Next(Accents.Length)];
            _randomAccents[key] = c;
        }
        return c;
    }

    /// <summary>磁贴填充：彩色=强调色纯色块；渐变=亮暗斜向渐变；玻璃=深色磨砂底+底部色晕；
    /// Win10=固定深灰；透明=完全透明</summary>
    public static Brush TileFill(Windows.UI.Color accent) =>
        FillFor(SettingsService.Current.TileTheme, accent);

    /// <summary>按指定主题生成填充（主题预览等需要渲染非当前主题时使用）</summary>
    public static Brush FillFor(TileTheme theme, Windows.UI.Color accent)
    {
        if (theme == TileTheme.Transparent)
            return new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        if (theme == TileTheme.Win10) return new SolidColorBrush(Win10TileColor);
        if (theme == TileTheme.Gradient)
            return new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Shade(accent, 0.34), Offset = 0 },
                    new GradientStop { Color = accent, Offset = 0.45 },
                    new GradientStop { Color = Shade(accent, -0.42), Offset = 1 }
                }
            };
        if (theme == TileTheme.Glass)
            // 统一深色磨砂底（白/黑叠加），底部透出磁贴自身颜色
            return new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), Offset = 0.55 },
                    new GradientStop
                    {
                        Color = Windows.UI.Color.FromArgb(0x52, accent.R, accent.G, accent.B),
                        Offset = 1
                    }
                }
            };
        return new SolidColorBrush(accent);
    }

    /// <summary>颜色明暗微调：k&gt;0 向白提亮，k&lt;0 向黑压暗（-1~1）</summary>
    private static Windows.UI.Color Shade(Windows.UI.Color c, double k)
    {
        byte Ch(byte v, byte target) => (byte)Math.Clamp(v + (target - v) * k, 0, 255);
        return Windows.UI.Color.FromArgb(c.A, Ch(c.R, 255), Ch(c.G, 255), Ch(c.B, 255));
    }

    /// <summary>主题对应的磁贴外框：渐变略粗、更亮，透明无边框，其余 1px 淡白</summary>
    public static Windows.UI.Color TileBorderColor =>
        IsTransparentTheme
            ? Windows.UI.Color.FromArgb(0, 0, 0, 0)
            : IsGradientTheme
                ? Windows.UI.Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

    /// <summary>主题对应的磁贴边框粗细</summary>
    public static double TileBorderThickness =>
        IsTransparentTheme ? 0 : IsGradientTheme ? 1.5 : 1;

    /// <summary>
    /// 构建标准磁贴（主页 / 小组件页预览共用）：背景图片铺满 或 强调色底+图标+名称，
    /// Win10 磁贴风格；不含选中/标记描边与事件（由调用方附加）。
    /// </summary>
    public static Border BuildStandardTile(
        LaunchItem item, LaunchItem? group, double width, double height, Windows.UI.Color accent, bool noColor,
        bool forceSmall = false)
    {
        // 抽屉"统一磁贴大小"开启时传 forceSmall：忽略磁贴自身大小，一律按小磁贴渲染
        var small = forceSmall || item.Size == TileSize.Small;
        // 背景图片磁贴：铺满图片，不显示图标与名称（组成员同样保留原样式）
        if (item.HasCover)
        {
            var coverTile = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(Radius),
                Background = new ImageBrush
                {
                    ImageSource = item.CoverImage,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
                },
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };
            coverTile.Opacity = Opacity;
            return coverTile;
        }

        var grid = new Grid();

        // 主题装饰层：先加入（位于图标/名称之下），且不接管输入
        if (!noColor && IsGlassTheme)
        {
            // 玻璃：底部自身颜色柔光 + 3px 顶部色条
            grid.Children.Add(new Border
            {
                Height = Math.Max(20, height * 0.45),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, accent.R, accent.G, accent.B), Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x48, accent.R, accent.G, accent.B), Offset = 1 }
                    }
                },
                IsHitTestVisible = false
            });
            grid.Children.Add(new Border
            {
                Height = 3,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(accent),
                IsHitTestVisible = false
            });
        }
        else if (!noColor && IsGradientTheme)
        {
            // 渐变：顶部白色高光带，强化斜向亮暗过渡的立体感
            grid.Children.Add(new Border
            {
                Height = Math.Max(16, height * 0.32),
                VerticalAlignment = VerticalAlignment.Top,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 1 }
                    }
                },
                IsHitTestVisible = false
            });
        }

        // 名称开关关闭 / 无颜色磁贴时不显示名称；小磁贴从图标下方居中显示标题
        var showName = SettingsService.Current.ShowTileName && !noColor;
        var smallWithName = small && showName;

        // 图标：默认居中；小磁贴上要留出标题位置，图标整体上移
        double iconSize = small ? 40 : 48;
        var iconMargin = smallWithName ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        var iconAlign = smallWithName ? VerticalAlignment.Top : VerticalAlignment.Center;
        UIElement icon = item.HasIcon
            ? new Image
            {
                Source = item.IconImage,
                Width = iconSize,
                Height = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = iconAlign,
                Margin = iconMargin
            }
            : new FontIcon
            {
                Glyph = item.Glyph,
                FontSize = small ? 30 : 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = iconAlign,
                Margin = iconMargin
            };
        grid.Children.Add(icon);

        // 组磁贴：右上角成员数量角标
        if (item.Kind == LaunchKind.Group && group is null)
            grid.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                Child = new TextBlock
                {
                    Text = item.Members.Count.ToString(),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                IsHitTestVisible = false
            });

        // 名称：小磁贴底部居中（字号略小），其余磁贴左下角；组成员同样显示
        if (showName)
            grid.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = small ? 11 : 13,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Margin = small ? new Thickness(6, 0, 6, 6) : new Thickness(10, 0, 8, 8),
                HorizontalAlignment = small ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                TextAlignment = small ? Microsoft.UI.Xaml.TextAlignment.Center : Microsoft.UI.Xaml.TextAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - (small ? 12 : 20)
            });

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(TileBorderColor),
            BorderThickness = new Thickness(TileBorderThickness),
            Child = grid,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
        };
        tile.Opacity = Opacity;
        return tile;
    }

    /// <summary>图片组磁贴的可刷新元素引用（轮播换图时只换 Source，不重建磁贴，保留选中框）</summary>
    public sealed class PhotoTileRefs
    {
        public Border Tile = null!;           // 磁贴外壳（淡入动画作用于此，连圆角一起裁切）
        public Image Img = null!;             // 当前图片
        public TextBlock Placeholder = null!; // 无图占位提示
    }

    /// <summary>
    /// 构建图片组小组件磁贴：外壳与普通磁贴一致（圆角/描边/透明度/立体边缘），
    /// 内容区铺满当前轮播图片（居中裁切 UniformToFill）；无图时显示占位提示。
    /// </summary>
    public static (Border Tile, PhotoTileRefs Refs) BuildPhotoTile(
        LaunchItem item, double width, double height, ImageSource? current, string placeholder)
    {
        var refs = new PhotoTileRefs
        {
            Img = new Image
            {
                Source = current,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Visibility = current != null ? Visibility.Visible : Visibility.Collapsed
            },
            Placeholder = new TextBlock
            {
                Text = placeholder,
                FontSize = 13,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = width - 20,
                IsHitTestVisible = false,
                Visibility = current != null ? Visibility.Collapsed : Visibility.Visible
            }
        };
        var grid = new Grid();
        grid.Children.Add(refs.Img);
        grid.Children.Add(refs.Placeholder);

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>音乐小组件磁贴的可刷新元素引用（主页按播放状态实时刷新）</summary>
    public sealed class MusicTileRefs
    {
        public Border CoverBox = null!;     // 封面（圆角 Border+ImageBrush，无封面隐藏）
        public FontIcon PlayGlyph = null!;  // 播放按钮图标
        public TextBlock Title = null!;     // 当前曲目名
    }

    /// <summary>控制按钮：透明圆形底 + 白色字形；act 为 null 时禁用（小组件页预览用）</summary>
    private static Button MakeCtrlButton(string glyph, double fontSize, Action? act, out FontIcon icon)
    {
        icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = fontSize,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        };
        var b = new Button
        {
            Content = icon,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16)
        };
        if (act != null) b.Click += (_, _) => act();
        else b.IsEnabled = false;
        return b;
    }

    /// <summary>
    /// 构建音乐控制器小组件磁贴：外壳（圆角/描边/填充/透明度/立体边缘）与普通磁贴完全一致。
    /// 纵向三段：顶部曲目名、中间封面、底部 上一首/播放暂停/下一首 按钮（均水平居中，按钮可鼠标点击）。
    /// onPrev/onPlay/onNext 为 null 时按钮禁用（小组件页预览）。
    /// </summary>
    public static (Border Tile, MusicTileRefs Refs) BuildMusicTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor,
        Action? onPrev, Action? onPlay, Action? onNext)
    {
        var grid = new Grid();
        var refs = new MusicTileRefs();
        // 三段行：标题(Auto) / 封面(*) / 控件(Auto)
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        double coverSize = Math.Min(height * 0.46, width * 0.6);

        // 顶部：曲目名（水平居中，单行省略）
        refs.Title = new TextBlock
        {
            Text = "",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.92,
            Margin = new Thickness(12, 10, 12, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = width - 24,
            IsHitTestVisible = false
        };
        Grid.SetRow(refs.Title, 0);
        grid.Children.Add(refs.Title);

        // 中间：封面（水平/垂直居中，圆角方块，无封面时隐藏）
        refs.CoverBox = new Border
        {
            Width = coverSize,
            Height = coverSize,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Grid.SetRow(refs.CoverBox, 1);
        grid.Children.Add(refs.CoverBox);

        // 底部：控制按钮行（上一首 / 播放暂停 / 下一首，水平居中）
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        controls.Children.Add(MakeCtrlButton("\uE892", 16, onPrev, out _)); // previous
        controls.Children.Add(MakeCtrlButton("\uE768", 18, onPlay, out refs.PlayGlyph)); // play
        controls.Children.Add(MakeCtrlButton("\uE893", 16, onNext, out _)); // next
        Grid.SetRow(controls, 2);
        grid.Children.Add(controls);

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        return (tile, refs);
    }

    /// <summary>天气小组件磁贴的可刷新元素引用（刷新时只改文字，不重建磁贴，保留选中框）</summary>
    public sealed class WeatherTileRefs
    {
        public FontIcon Icon = null!;    // 天气图标
        public TextBlock Temp = null!;   // 当前温度（大字）
        public TextBlock City = null!;   // 城市
        public TextBlock Cond = null!;   // 天气描述
        public TextBlock Extra = null!;  // 大磁贴补充行（最高/最低、湿度/风；小/长磁贴不显示）
    }

    /// <summary>
    /// 构建天气小组件磁贴：外壳（圆角/描边/填充/透明度/立体边缘）与普通磁贴完全一致，内容按尺寸分档——
    /// 小(1×1)=图标+温度；长(2×1)=图标+温度/城市/描述；大(2×2)=全部 + 最高最低/湿度风。
    /// 文本先填占位，由 FillWeatherTile 写入数据。
    /// </summary>
    public static (Border Tile, WeatherTileRefs Refs) BuildWeatherTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var refs = new WeatherTileRefs
        {
            Icon = new FontIcon { Glyph = "\uE753", Foreground = white, IsHitTestVisible = false },
            Temp = new TextBlock
            {
                Text = "--°",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                IsHitTestVisible = false
            },
            City = new TextBlock
            {
                Text = "定位中…",
                FontSize = 12,
                Opacity = 0.8,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            },
            Cond = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Opacity = 0.9,
                Foreground = white,
                IsHitTestVisible = false
            },
            Extra = new TextBlock
            {
                Text = "",
                FontSize = 11.5,
                Opacity = 0.75,
                Foreground = white,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            }
        };

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            // 小：上下排布，只保留图标与温度
            refs.Icon.FontSize = 26;
            refs.Temp.FontSize = 21;
            refs.City.Visibility = Visibility.Collapsed;
            refs.Cond.Visibility = Visibility.Collapsed;

            var stack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(refs.Icon);
            stack.Children.Add(refs.Temp);
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            // 长：左图标 + 右文字列（温度/城市/描述）
            refs.Icon.FontSize = 34;
            refs.Temp.FontSize = 25;
            refs.City.MaxWidth = width - 96;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var col = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(refs.Temp);
            col.Children.Add(refs.City);
            col.Children.Add(refs.Cond);
            row.Children.Add(refs.Icon);
            row.Children.Add(col);
            grid.Children.Add(row);
        }
        else
        {
            // 大：居中竖排，图标+温度一行，下面城市/描述/最高最低湿度风
            refs.Icon.FontSize = 40;
            refs.Temp.FontSize = 33;
            refs.City.MaxWidth = width - 28;
            refs.Cond.MaxWidth = width - 28;

            var head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            head.Children.Add(refs.Icon);
            refs.Temp.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(refs.Temp);

            var stack = new StackPanel
            {
                Spacing = 5,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            refs.City.HorizontalAlignment = HorizontalAlignment.Center;
            refs.Cond.HorizontalAlignment = HorizontalAlignment.Center;
            refs.Extra.Visibility = Visibility.Visible;
            stack.Children.Add(head);
            stack.Children.Add(refs.City);
            stack.Children.Add(refs.Cond);
            stack.Children.Add(refs.Extra);
            grid.Children.Add(stack);
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        return (tile, refs);
    }

    /// <summary>把当前天气写入磁贴引用：无数据时显示"定位中…"/"天气获取失败"，有数据时填温度与描述</summary>
    public static void FillWeatherTile(WeatherTileRefs refs)
    {
        var w = WeatherService.Current;
        if (w is null)
        {
            refs.Icon.Glyph = "\uE753";
            refs.Temp.Text = "--°";
            refs.City.Text = WeatherService.Loading ? "定位中…" : "天气获取失败";
            refs.Cond.Text = WeatherService.Loading ? "" : "按 A 重试";
            refs.Extra.Text = "";
            return;
        }

        refs.Icon.Glyph = w.Glyph;
        refs.Temp.Text = (int)Math.Round(w.Temp) + "°";
        refs.City.Text = string.IsNullOrEmpty(w.City) ? "当前位置" : w.City;
        refs.Cond.Text = w.Text;
        refs.Extra.Text = $"最高 {(int)Math.Round(w.TempMax)}° / 最低 {(int)Math.Round(w.TempMin)}°"
                        + $"\n湿度 {w.Humidity}%　风 {(int)Math.Round(w.Wind)} km/h";
    }

    /// <summary>
    /// 构建映射小组件磁贴：外壳（圆角/描边/填充/透明度/立体边缘）与普通磁贴完全一致，
    /// 内容按尺寸分档——小(1×1)=手柄图标+按键名；长(2×1)=图标 + 按键名（居中横排）；
    /// 大(2×2)=图标 + 按键名 + 提示"按 A 触发"。内容仅随编辑变化，无需刷新引用。
    /// </summary>
    public static Border BuildMappingTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        // 标题：自定义标题（显示名称）非空时覆盖显示，留空则显示映射的按键名
        var label = string.IsNullOrWhiteSpace(item.Name) ? item.MappingLabel : item.Name;

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            // 小：图标上下按键名
            var stack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = "\uE765",
                FontSize = 24,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 12,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            // 长：左图标 + 右按键名（居中横排）
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new FontIcon
            {
                Glyph = "\uE765",
                FontSize = 30,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 96,
                IsHitTestVisible = false
            });
            grid.Children.Add(row);
        }
        else
        {
            // 大：居中竖排，图标 + 按键名 + 提示
            var stack = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = "\uE765",
                FontSize = 38,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = "按 A 触发",
                FontSize = 12,
                Opacity = 0.75,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        return tile;
    }

    /// <summary>
    /// 构建修改器小组件磁贴：外壳（圆角/描边/填充/透明度/立体边缘）与普通磁贴完全一致，
    /// 内容按尺寸分档——小(1×1)=扳手图标+名称；长(2×1)=图标 + 名称（居中横排）；
    /// 大(2×2)=图标 + 名称 + 提示"按 A 打开修改器"。内容仅随编辑变化，无需刷新引用。
    /// </summary>
    public static Border BuildTrainerTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var label = string.IsNullOrWhiteSpace(item.Name) ? "修改器" : item.Name;
        const string glyph = "\uE90F"; // 扳手（Repair）

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            // 小：图标上下名称
            var stack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 24,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 12,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            // 长：左图标 + 右名称（居中横排）
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 30,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 96,
                IsHitTestVisible = false
            });
            grid.Children.Add(row);
        }
        else
        {
            // 大：居中竖排，图标 + 名称 + 提示
            var stack = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 38,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = "按 A 打开修改器",
                FontSize = 12,
                Opacity = 0.75,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        return tile;
    }

    /// <summary>
    /// 构建 OptiScaler 小组件磁贴：外壳（圆角/描边/填充/透明度/立体边缘）与普通磁贴完全一致，
    /// 内容按尺寸分档——小(1×1)=胶片图标+名称；长(2×1)=图标 + 名称（居中横排）；
    /// 大(2×2)=图标 + 名称 + 提示"A 呼出菜单 · Y 注入"。内容仅随编辑变化，无需刷新引用。
    /// </summary>
    public static Border BuildOptiScalerTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var label = string.IsNullOrWhiteSpace(item.Name) ? "OptiScaler" : item.Name;
        const string glyph = "\uE8C8"; // 层叠帧

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            var stack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 24,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 12,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 30,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 96,
                IsHitTestVisible = false
            });
            grid.Children.Add(row);
        }
        else
        {
            var stack = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 38,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = "A 呼出菜单 · Y 注入",
                FontSize = 12,
                Opacity = 0.75,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        return tile;
    }

    /// <summary>
    /// 构建游戏存档小组件磁贴：外壳（圆角/描边/填充/透明度/立体边缘）与普通磁贴完全一致，
    /// 内容按尺寸分档——小(1×1)=保存图标+名称；长(2×1)=图标 + 名称（居中横排）；
    /// 大(2×2)=图标 + 名称 + 提示"按 A 备份 · Y 同步"。内容仅随编辑变化，无需刷新引用。
    /// </summary>
    public static Border BuildBackupTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var label = string.IsNullOrWhiteSpace(item.Name) ? "游戏存档" : item.Name;
        const string glyph = "\uE74E"; // 保存（Save）

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            // 小：图标上下名称
            var stack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 24,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 12,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            // 长：左图标 + 右名称（居中横排）
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 30,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 96,
                IsHitTestVisible = false
            });
            grid.Children.Add(row);
        }
        else
        {
            // 大：居中竖排，图标 + 名称 + 提示
            var stack = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 38,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = "按 A 备份 · Y 同步",
                FontSize = 12,
                Opacity = 0.75,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        return tile;
    }

    /// <summary>手柄映射磁贴的可刷新元素引用（开关状态变化时只改开关外观，不重建磁贴）</summary>
    public sealed class GamepadMapTileRefs
    {
        public Border Tile = null!;               // 磁贴外壳
        public Border Track = null!;              // 开关轨道
        public Microsoft.UI.Xaml.Shapes.Ellipse Knob = null!; // 开关滑块
    }

    /// <summary>构建"开关"图标：轨道（Border）+ 滑块（Ellipse）。外观由 FillGamepadMapState 写入。</summary>
    private static Grid BuildSwitchIcon(double trackWidth, double trackHeight, double knobSize,
        out Border track, out Microsoft.UI.Xaml.Shapes.Ellipse knob)
    {
        track = new Border
        {
            Width = trackWidth,
            Height = trackHeight,
            CornerRadius = new CornerRadius(trackHeight / 2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            IsHitTestVisible = false
        };
        knob = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = knobSize,
            Height = knobSize,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
            IsHitTestVisible = false
        };
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        host.Children.Add(track);
        host.Children.Add(knob);
        return host;
    }

    /// <summary>
    /// 构建手柄映射小组件磁贴：外壳（圆角/描边/填充/透明度/立体边缘）与普通磁贴完全一致，
    /// 内容按尺寸分档——小(1×1)=手柄图标+开关图标；长(2×1)=图标 + 名称 + 开关图标（居中横排）；
    /// 大(2×2)=图标 + 名称 + 开关图标 + 提示"A:开关映射 · 长按:关闭"。状态由 FillGamepadMapState 写入。
    /// </summary>
    public static (Border Tile, GamepadMapTileRefs Refs) BuildGamepadMapTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var label = string.IsNullOrWhiteSpace(item.Name) ? "手柄映射" : item.Name;
        const string glyph = "\uE7FC"; // 手柄（Gamepad）

        var refs = new GamepadMapTileRefs();

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            // 小：图标上下开关
            var stack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 24,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(BuildSwitchIcon(26, 14, 8, out refs.Track, out refs.Knob));
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            // 长：左图标 + 右（名称 + 开关）
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 30,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            var col = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 96,
                IsHitTestVisible = false
            });
            col.Children.Add(BuildSwitchIcon(30, 16, 9, out refs.Track, out refs.Knob));
            row.Children.Add(col);
            grid.Children.Add(row);
        }
        else
        {
            // 大：居中竖排，图标 + 名称 + 开关 + 提示
            var stack = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 38,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            });
            stack.Children.Add(BuildSwitchIcon(34, 18, 10, out refs.Track, out refs.Knob));
            stack.Children.Add(new TextBlock
            {
                Text = "A:开关映射 · 长按:关闭",
                FontSize = 12,
                Opacity = 0.75,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>把当前开关状态写入磁贴引用：开=绿色轨道+滑块靠右，关=半透明白轨道+滑块靠左。</summary>
    public static void FillGamepadMapState(GamepadMapTileRefs refs, LaunchItem item)
    {
        var on = GamepadRemapService.IsEnabled(item);
        refs.Track.Background = new SolidColorBrush(on
            ? Windows.UI.Color.FromArgb(0xFF, 0x66, 0xE0, 0x8A)    // 绿：已开启
            : Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)); // 半透明白：已关闭
        refs.Knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        refs.Knob.Margin = on ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
    }

    /// <summary>性能监控磁贴的可刷新元素引用（开关或读数变化时只改文字，不重建磁贴）</summary>
    public sealed class MonitorTileRefs
    {
        public Border Tile = null!;               // 磁贴外壳
        public Microsoft.UI.Xaml.Shapes.Ellipse Dot = null!; // 状态圆点
        public TextBlock Status = null!;          // 运行状态文字
        public TextBlock Summary = null!;         // 读数摘要（长/大尺寸才有）
    }

    /// <summary>
    /// 构建性能监控小组件磁贴：外壳与普通磁贴一致，内容按尺寸分档——
    /// 小(1×1)=图标+状态点；长(2×1)=图标+名称+状态；大(2×2)=图标+名称+状态+读数摘要。
    /// 状态与摘要由 FillMonitorState 写入。
    /// </summary>
    public static (Border Tile, MonitorTileRefs Refs) BuildMonitorTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var label = string.IsNullOrWhiteSpace(item.Name) ? "性能监控" : item.Name;
        const string glyph = "\uE9D9"; // 诊断（性能监控）

        var refs = new MonitorTileRefs();

        Grid StatusRow(MonitorTileRefs refs, double fontSize)
        {
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            refs.Dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            refs.Status = new TextBlock
            {
                Text = "已停止",
                FontSize = fontSize,
                Opacity = 0.85,
                Foreground = white,
                IsHitTestVisible = false
            };
            stack.Children.Add(refs.Dot);
            stack.Children.Add(refs.Status);
            host.Children.Add(stack);
            return host;
        }

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            refs.Summary = new TextBlock { Visibility = Visibility.Collapsed };
            var stack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 24,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(StatusRow(refs, 10));
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 30,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            var col = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 96,
                IsHitTestVisible = false
            });
            col.Children.Add(StatusRow(refs, 12));
            row.Children.Add(col);
            grid.Children.Add(row);
            refs.Summary = new TextBlock { Visibility = Visibility.Collapsed };
        }
        else
        {
            var stack = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 38,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            });
            stack.Children.Add(StatusRow(refs, 13));
            refs.Summary = new TextBlock
            {
                Text = "A:开启置顶监控条",
                FontSize = 12,
                Opacity = 0.75,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            };
            stack.Children.Add(refs.Summary);
            grid.Children.Add(stack);
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>把监控开关状态与读数摘要写入磁贴引用</summary>
    public static void FillMonitorState(MonitorTileRefs refs, LaunchItem item)
    {
        var running = MonitorService.IsRunning;
        refs.Dot.Fill = new SolidColorBrush(running
            ? Windows.UI.Color.FromArgb(0xFF, 0x66, 0xE0, 0x8A)    // 绿：监控中
            : Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)); // 灰白：已停止
        refs.Status.Text = running ? "监控中" : "已停止";
        if (refs.Summary.Visibility == Visibility.Collapsed) return;

        if (!running)
        {
            refs.Summary.Text = "A:开启置顶监控条";
            return;
        }
        var s = MonitorService.Current;
        var metrics = item.MonitorMetrics;
        var parts = new List<string>();
        if ((metrics & MonitorMetric.Fps) != 0)
            parts.Add(s.Fps > 0 ? $"{s.Fps:0} FPS" : "FPS —");
        if ((metrics & MonitorMetric.CpuUsage) != 0)
            parts.Add($"CPU {s.CpuUsage:0}%");
        if ((metrics & MonitorMetric.GpuUsage) != 0)
            parts.Add(double.IsNaN(s.GpuUsage) ? "GPU —" : $"GPU {s.GpuUsage:0}%");
        if ((metrics & MonitorMetric.Memory) != 0)
            parts.Add($"内存 {s.MemPercent:0}%");
        refs.Summary.Text = parts.Count > 0 ? string.Join(" · ", parts) : "监控中";
    }

    /// <summary>小黄鸭磁贴的可刷新元素引用（安装/运行状态变化时只改文字与圆点，不重建磁贴）</summary>
    public sealed class LosslessTileRefs
    {
        public Border Tile = null!;               // 磁贴外壳
        public Microsoft.UI.Xaml.Shapes.Ellipse Dot = null!; // 状态圆点
        public TextBlock Status = null!;          // 安装/运行状态文字
        public TextBlock Summary = null!;         // 操作摘要（长/大尺寸才有）
    }

    /// <summary>
    /// 构建小黄鸭联动小组件磁贴：外壳与普通磁贴一致，内容按尺寸分档——
    /// 小(1×1)=图标+状态；长(2×1)=图标+名称+状态；大(2×2)=图标+名称+状态+操作摘要。
    /// 状态与摘要由 FillLosslessState 写入。
    /// </summary>
    public static (Border Tile, LosslessTileRefs Refs) BuildLosslessTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var label = string.IsNullOrWhiteSpace(item.Name) ? "小黄鸭" : item.Name;
        const string glyph = "\uE945"; // 闪电（补帧）

        var refs = new LosslessTileRefs();

        Grid StatusRow(LosslessTileRefs refs, double fontSize)
        {
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            refs.Dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            refs.Status = new TextBlock
            {
                Text = "未安装",
                FontSize = fontSize,
                Opacity = 0.85,
                Foreground = white,
                IsHitTestVisible = false
            };
            stack.Children.Add(refs.Dot);
            stack.Children.Add(refs.Status);
            host.Children.Add(stack);
            return host;
        }

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            refs.Summary = new TextBlock { Visibility = Visibility.Collapsed };
            var stack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 24,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(StatusRow(refs, 10));
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 30,
                Foreground = white,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            var col = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 96,
                IsHitTestVisible = false
            });
            col.Children.Add(StatusRow(refs, 12));
            row.Children.Add(col);
            grid.Children.Add(row);
            refs.Summary = new TextBlock { Visibility = Visibility.Collapsed };
        }
        else
        {
            var stack = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 38,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            });
            stack.Children.Add(StatusRow(refs, 13));
            refs.Summary = new TextBlock
            {
                Text = "A:一键补帧",
                FontSize = 12,
                Opacity = 0.75,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            };
            stack.Children.Add(refs.Summary);
            grid.Children.Add(stack);
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>把安装/运行状态、插帧状态与操作摘要写入磁贴引用</summary>
    public static void FillLosslessState(LosslessTileRefs refs, LaunchItem item)
    {
        var installed = LosslessScalingService.IsInstalled(item);
        var running = installed && LosslessScalingService.IsRunning;
        var scaling = running && LosslessScalingService.IsScaling;

        refs.Dot.Fill = new SolidColorBrush(
            !installed ? Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)   // 灰白：未安装
            : scaling ? Windows.UI.Color.FromArgb(0xFF, 0x66, 0xE0, 0x8A)     // 绿：插帧中
            : running ? Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)     // 蓝：运行中未插帧
            : Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xC5, 0x42));             // 黄：已安装未运行
        refs.Status.Text = !installed ? "未安装"
            : scaling ? "插帧中"
            : running ? "运行中"
            : "未运行";

        if (refs.Summary.Visibility == Visibility.Collapsed) return;
        refs.Summary.Text = !installed ? "R3:编辑 · 指定程序"
            : scaling ? "A:关闭插帧"
            : "A:开启插帧";
    }

    /// <summary>前台应用监测磁贴的可刷新元素引用（前台应用或冻结状态变化时只改内容，不重建磁贴）</summary>
    public sealed class ForegroundTileRefs
    {
        public Border Tile = null!;         // 磁贴外壳
        public Image Icon = null!;          // 前台应用图标（有缓存图标时显示）
        public FontIcon Fallback = null!;   // 无图标时的占位字形
        public TextBlock Name = null!;      // 进程名
        public TextBlock State = null!;     // 状态（运行中/已冻结/未检测到）
        public TextBlock Title = null!;     // 窗口标题（仅大磁贴显示）
    }

    /// <summary>
    /// 构建前台应用监测小组件磁贴：外壳（圆角/描边/填充/透明度/立体边缘）与普通磁贴完全一致，内容按尺寸分档——
    /// 小(1×1)=图标+状态；长(2×1)=图标 + 进程名 + 状态；大(2×2)=图标 + 进程名 + 窗口标题 + 状态 + 按键提示。
    /// 内容由 FillForegroundTile 按当前前台应用写入。
    /// </summary>
    public static (Border Tile, ForegroundTileRefs Refs) BuildForegroundTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var refs = new ForegroundTileRefs
        {
            Icon = new Image { IsHitTestVisible = false },
            Fallback = new FontIcon { Glyph = "\uE769", Foreground = white, IsHitTestVisible = false },
            Name = new TextBlock
            {
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            },
            State = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.85,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            },
            Title = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.8,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            }
        };

        // 图标层：图片与占位字形叠放，由 FillForegroundTile 切换可见性
        var iconHost = new Grid();
        iconHost.Children.Add(refs.Icon);
        iconHost.Children.Add(refs.Fallback);

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            // 小：图标 + 状态（进程名放不下）
            refs.Icon.Width = refs.Icon.Height = 40;
            refs.Fallback.FontSize = 26;
            refs.Name.Visibility = Visibility.Collapsed;
            refs.Title.Visibility = Visibility.Collapsed;
            refs.State.FontSize = 11;
            refs.State.HorizontalAlignment = HorizontalAlignment.Center;

            var stack = new StackPanel
            {
                Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(iconHost);
            stack.Children.Add(refs.State);
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            // 长：左图标 + 右（进程名 + 状态）
            refs.Icon.Width = refs.Icon.Height = 34;
            refs.Fallback.FontSize = 30;
            refs.Name.MaxWidth = width - 96;
            refs.State.MaxWidth = width - 96;
            refs.Title.Visibility = Visibility.Collapsed;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var col = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(refs.Name);
            col.Children.Add(refs.State);
            row.Children.Add(iconHost);
            row.Children.Add(col);
            grid.Children.Add(row);
        }
        else
        {
            // 大：居中竖排，图标 + 进程名 + 窗口标题 + 状态 + 提示
            refs.Icon.Width = refs.Icon.Height = 44;
            refs.Fallback.FontSize = 38;
            refs.Name.FontSize = 18;
            refs.State.FontSize = 13;
            refs.Name.HorizontalAlignment = HorizontalAlignment.Center;
            refs.State.HorizontalAlignment = HorizontalAlignment.Center;
            refs.Title.HorizontalAlignment = HorizontalAlignment.Center;
            refs.Name.MaxWidth = width - 28;
            refs.State.MaxWidth = width - 28;
            refs.Title.MaxWidth = width - 28;

            var stack = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(iconHost);
            stack.Children.Add(refs.Name);
            stack.Children.Add(refs.Title);
            stack.Children.Add(refs.State);
            stack.Children.Add(new TextBlock
            {
                Text = "A:冻结/解冻 · Y:结束",
                FontSize = 12,
                Opacity = 0.75,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });
            grid.Children.Add(stack);
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>把当前前台应用写入磁贴引用：进程名 + 窗口标题 + 运行/冻结状态 + 应用图标（无缓存图标时显示占位字形）</summary>
    public static void FillForegroundTile(ForegroundTileRefs refs)
    {
        var app = ForegroundWatcherService.Current;
        if (app is null)
        {
            refs.Name.Text = "未检测到应用";
            refs.Title.Text = "";
            refs.State.Text = "暂无前台应用";
            refs.Icon.Source = null;
            refs.Icon.Visibility = Visibility.Collapsed;
            refs.Fallback.Visibility = Visibility.Visible;
            return;
        }

        refs.Name.Text = app.ProcessName + ".exe";
        refs.Title.Text = app.Title;
        refs.State.Text = ForegroundWatcherService.IsFrozen ? "已冻结" : "运行中";

        var iconPath = app.ExePath is null ? null : IconExtractService.CachedIconPath(app.ExePath);
        if (iconPath is not null)
        {
            try
            {
                refs.Icon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                refs.Icon.Visibility = Visibility.Visible;
                refs.Fallback.Visibility = Visibility.Collapsed;
                return;
            }
            catch { }
        }
        refs.Icon.Source = null;
        refs.Icon.Visibility = Visibility.Collapsed;
        refs.Fallback.Visibility = Visibility.Visible;
    }

    /// <summary>系统小组件磁贴的可刷新元素引用（右摇杆切换面板项 / 音量亮度射频变化时只改内容，不重建磁贴）</summary>
    public sealed class SystemTileRefs
    {
        public Border Tile = null!;       // 磁贴外壳
        public FontIcon Icon = null!;     // 当前面板项图标
        public TextBlock Name = null!;    // 面板项名称（Win 设置 / 音量 / 亮度 …）
        public TextBlock Reading = null!; // 当前读数（百分比 / 已开启 / 按 A 打开）
        public TextBlock Hint = null!;    // 按键提示（当前尺寸不显示，保留引用）
        public Grid SlideRow = null!;     // 内容整体（切换面板项时左右滑动 + 淡入的动画载体）
    }

    /// <summary>
    /// 构建系统小组件磁贴：固定"长"尺寸（2×1），左图标 + 右（名称 + 读数）。
    /// 内容装在一个可整体平移的容器里，切换面板项时由 MainPage 播左右滑入动画。
    /// 内容由 FillSystemTile 按当前面板项写入。
    /// </summary>
    public static (Border Tile, SystemTileRefs Refs) BuildSystemTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var refs = new SystemTileRefs
        {
            Icon = new FontIcon { Glyph = "\uE713", FontSize = 30, Foreground = white, IsHitTestVisible = false },
            Name = new TextBlock
            {
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            },
            Reading = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.85,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            },
            Hint = new TextBlock
            {
                Text = "←→:切换 · A:开关 · LB/RB:调整",
                FontSize = 12,
                Opacity = 0.75,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            }
        };

        var grid = new Grid();
        // 长：左图标 + 右（名称 + 读数）
        refs.Icon.FontSize = 30;
        refs.Name.MaxWidth = width - 96;
        refs.Reading.MaxWidth = width - 96;
        refs.Hint.Visibility = Visibility.Collapsed;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var col = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(refs.Name);
        col.Children.Add(refs.Reading);
        row.Children.Add(refs.Icon);
        row.Children.Add(col);

        // 切换动画载体：整体平移 + 淡入（不直接动磁贴本身，免得和按压/拖动反馈的变换打架）
        var slide = new Grid
        {
            RenderTransform = new TranslateTransform(),
            IsHitTestVisible = false
        };
        slide.Children.Add(row);
        refs.SlideRow = slide;
        grid.Children.Add(slide);

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>把系统小组件当前停留的面板项写入磁贴引用：图标 + 名称 + 读数（音量/亮度百分比、射频开关或连接状态）</summary>
    public static void FillSystemTile(SystemTileRefs refs, LaunchItem item)
    {
        // 编辑里可能把当前项去掉了：回落到第一个启用项，避免磁贴停在不可切换的项上
        var panel = SystemPanelService.FromOption(
            SystemPanelService.NormalizeOption(item.SystemOptionMask, item.SystemOption));
        refs.Icon.Glyph = SystemPanelService.Glyph(panel);
        refs.Name.Text = SystemPanelService.Label(panel);
        refs.Reading.Text = SystemPanelService.Reading(panel);
    }
}
