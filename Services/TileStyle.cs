using GameLocker.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace GameLocker.Services;

/// <summary>
/// 磁贴视觉样式统一出口：尺寸单位、间距、配色、立体渐变/边缘、音乐小组件磁贴构建。
/// 主页磁贴与小组件页预览共用同一套常量与方法，保证外观完全一致。
/// </summary>
public static class TileStyle
{
    /// <summary>磁贴基本单位（1×1 格边长，像素）</summary>
    public const double Unit = 84;

    /// <summary>磁贴圆角（按用户设置；未设置时彩色/液态玻璃圆角，金属拉丝微直角，像素街机直角）</summary>
    public static double Radius => IsSquareRadius ? 0 : (IsMetalBrushedTheme ? 3 : 6);

    /// <summary>是否为液态玻璃主题（通透玻璃卡片 + accent 色描边 + 内部高光，圆角）</summary>
    public static bool IsLiquidGlassTheme => SettingsService.Current.TileTheme == TileTheme.NeonCyber;

    /// <summary>是否为金属拉丝主题（横向拉丝纹理 + 工业风 + 上下内阴影，微直角）</summary>
    public static bool IsMetalBrushedTheme => SettingsService.Current.TileTheme == TileTheme.MetalBrushed;

    /// <summary>是否为像素街机主题（硬色块 + 内阴影，直角）</summary>
    public static bool IsPixelArcadeTheme => SettingsService.Current.TileTheme == TileTheme.PixelArcade;

    /// <summary>是否直角（方形）：显式设置优先，未设置时按主题默认
    /// 像素街机=直角，金属拉丝=微直角（仍允许覆盖完全直角），其余=圆角</summary>
    public static bool IsSquareRadius =>
        (SettingsService.Current.TileCornerShape
         ?? (IsPixelArcadeTheme ? TileCorner.Square : TileCorner.Rounded)) == TileCorner.Square;

    /// <summary>是否隐藏磁贴颜色设置（像素街机硬色块底色依赖 accent 但颜色不自由）</summary>
    public static bool HidesTileColor => false;

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
    /// 常驻磁贴始终有色；像素街机硬色块统一按有色处理（保留名称）</summary>
    public static bool IsNoColor(LaunchItem item) =>
        !IsPixelArcadeTheme
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

    /// <summary>磁贴填充：彩色=强调色纯色块；液态玻璃=通透多层半透明（依赖 accent）；金属拉丝=accent 色调暗上下渐变模拟金属板（依赖 accent）；
    /// 像素街机=硬色块上亮下暗（依赖 accent）</summary>
    public static Brush TileFill(Windows.UI.Color accent) =>
        FillFor(SettingsService.Current.TileTheme, accent);

    /// <summary>按指定主题生成填充（主题预览等需要渲染非当前主题时使用）</summary>
    public static Brush FillFor(TileTheme theme, Windows.UI.Color accent)
    {
        if (theme == TileTheme.NeonCyber)
        {
            // 液态玻璃：多层半透明叠加，顶部亮白过渡 → 中段淡 accent → 底部微沉，accent 色做边框
            var mid = Shade(accent, 0.5);
            var dark = Shade(accent, 0.65);
            return new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0.3, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x18, mid.R, mid.G, mid.B), Offset = 0.5 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x20, dark.R, dark.G, dark.B), Offset = 1 }
                }
            };
        }
        if (theme == TileTheme.MetalBrushed)
        {
            // 金属拉丝：accent 色调暗后做上下渐变模拟金属板（上受光→中平→下背光）
            var light = Shade(accent, 0.15);
            var mid = Shade(accent, 0.35);
            var dark = Shade(accent, -0.35);
            return new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = light, Offset = 0 },
                    new GradientStop { Color = mid, Offset = 0.5 },
                    new GradientStop { Color = dark, Offset = 1 }
                }
            };
        }
        if (theme == TileTheme.PixelArcade)
        {
            // 像素街机：硬色块 + 上亮下暗渐变模拟凸起按键
            var light = Shade(accent, 0.25);
            var dark = Shade(accent, -0.35);
            return new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = light, Offset = 0 },
                    new GradientStop { Color = accent, Offset = 0.5 },
                    new GradientStop { Color = dark, Offset = 1 }
                }
            };
        }
        // 彩色：纯色块
        return new SolidColorBrush(accent);
    }

    /// <summary>颜色明暗微调：k&gt;0 向白提亮，k&lt;0 向黑压暗（-1~1）</summary>
    private static Windows.UI.Color Shade(Windows.UI.Color c, double k)
    {
        byte Ch(byte v, byte target) => (byte)Math.Clamp(v + (target - v) * k, 0, 255);
        return Windows.UI.Color.FromArgb(c.A, Ch(c.R, 255), Ch(c.G, 255), Ch(c.B, 255));
    }

    /// <summary>主题对应的磁贴外框颜色：液态玻璃=强调色（彩色描边），像素街机=accent 压暗（硬边框），
    /// 金属拉丝 / 彩色=淡白</summary>
    public static Windows.UI.Color TileBorderColorFor(Windows.UI.Color accent)
    {
        if (IsLiquidGlassTheme) return accent;
        if (IsPixelArcadeTheme) return Shade(accent, -0.55);
        return Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>主题对应的磁贴边框粗细（液态玻璃 1.5，像素街机 2，其余 1）</summary>
    public static double TileBorderThickness =>
        IsPixelArcadeTheme ? 2 : IsLiquidGlassTheme ? 1.5 : 1;

    /// <summary>磁贴默认强调色（用于没有 accent 参数的图片类磁贴）</summary>
    private static readonly Windows.UI.Color _defaultAccent = Accents[0];

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
                BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
                BorderThickness = new Thickness(TileBorderThickness)
            };
            coverTile.Opacity = Opacity;
            return coverTile;
        }

        var grid = new Grid();

        // 主题装饰层：先加入（位于图标/名称之下），且不接管输入
        if (!noColor && IsLiquidGlassTheme)
        {
            // 液态玻璃：顶部亮白高光带 + 左下角落光斑（强化玻璃通透感）
            grid.Children.Add(new Border
            {
                Height = Math.Max(18, height * 0.35),
                VerticalAlignment = VerticalAlignment.Top,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 1 }
                    }
                },
                IsHitTestVisible = false
            });
        }
        else if (!noColor && IsMetalBrushedTheme)
        {
            // 金属拉丝：顶部受光高光 + 上下内阴影模拟金属板凸起 + 横向拉丝纹理
            // 顶部高光带
            grid.Children.Add(new Border
            {
                Height = Math.Max(14, height * 0.3),
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
        else if (!noColor && IsPixelArcadeTheme)
        {
            // 像素街机：底部暗色条 + 右下角黄色指示灯
            grid.Children.Add(new Border
            {
                Height = Math.Max(6, height * 0.08),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0, 0, 0)),
                IsHitTestVisible = false
            });
            grid.Children.Add(new Border
            {
                Width = 8, Height = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD6, 0x00)),
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
        var isClash = item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.Clash;
        // 文件传输：图标在左、名称在右的横排布局
        var rowLayout = item.Kind == LaunchKind.Widget
            && item.WidgetKind == WidgetKind.FileTransfer
            && showName;
        if (rowLayout)
        {
            FrameworkElement rowIcon = item.HasIcon
                ? new Image { Source = item.IconImage, Width = small ? 26 : 32, Height = small ? 26 : 32 }
                : new FontIcon { Glyph = item.Glyph, FontSize = small ? 24 : 30 };
            rowIcon.VerticalAlignment = VerticalAlignment.Center;
            var rowText = new TextBlock
            {
                Text = item.Name,
                FontSize = small ? 12 : 15,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - (small ? 58 : 78)
            };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = small ? 8 : 12,
                Margin = new Thickness(small ? 10 : 16, 0, 10, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            row.Children.Add(rowIcon);
            row.Children.Add(rowText);
            grid.Children.Add(row);
        }
        else
        {
            UIElement icon;
            if (item.HasIcon)
                icon = new Image
                {
                    Source = item.IconImage,
                    Width = iconSize,
                    Height = iconSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = iconAlign,
                    Margin = iconMargin
                };
            else if (isClash)
            {
                // 小猫咪用白色线条图标（矢量，与磁贴图标同色）
                var cat = MakeCatIcon(small ? 34 : 46, new SolidColorBrush(Microsoft.UI.Colors.White));
                cat.VerticalAlignment = iconAlign;
                cat.Margin = iconMargin;
                icon = cat;
            }
            else
                icon = new FontIcon
                {
                    Glyph = item.Glyph,
                    FontSize = small ? 30 : 40,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = iconAlign,
                    Margin = iconMargin
                };
            grid.Children.Add(icon);
        }

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
        // 文件传输已在上面用"图标左 + 名称右"渲染，这里不再叠加底部名称
        if (showName && !rowLayout)
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(_defaultAccent)),
            BorderThickness = new Thickness(TileBorderThickness),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>壁纸小组件磁贴的可刷新元素引用（轮播换图时只换 Source，不重建磁贴，保留选中框）</summary>
    public sealed class WallpaperTileRefs
    {
        public Border Tile = null!;           // 磁贴外壳（淡入动画作用于此，连圆角一起裁切）
        public Image Img = null!;             // 当前壁纸
        public TextBlock Placeholder = null!; // 无图占位提示
        public Border InfoBar = null!;        // 选中时显示的信息条（标题 + 壁纸信息）
        public TextBlock InfoTitle = null!;   // 信息条标题（固定「壁纸」）
        public TextBlock InfoText = null!;    // 信息条详情（来源 · 第几张 · 本地缓存数）
    }

    /// <summary>
    /// 构建壁纸小组件磁贴：外壳与普通磁贴一致（圆角/描边/透明度），内容区铺满当前壁纸（居中裁切），
    /// 不显示名称角标；无图时显示占位提示。
    /// </summary>
    public static (Border Tile, WallpaperTileRefs Refs) BuildWallpaperTile(
        double width, double height, ImageSource? current, string placeholder)
    {
        var refs = new WallpaperTileRefs
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

        // 信息条：仅主页选中该磁贴时显示（标题 + 来源/序号/缓存数），选中移开即隐藏
        refs.InfoTitle = new TextBlock
        {
            Text = "壁纸",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        };
        refs.InfoText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Opacity = 0.78,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        refs.InfoBar = new Border
        {
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0, 0, 0), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0xD9, 0, 0, 0), Offset = 1 }
                }
            },
            Child = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    refs.InfoTitle,
                    refs.InfoText
                }
            }
        };
        grid.Children.Add(refs.InfoBar);

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(TileBorderColorFor(_defaultAccent)),
            BorderThickness = new Thickness(TileBorderThickness),
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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
                Glyph = "\uE71B",
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
                Glyph = "\uE71B",
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
                Glyph = "\uE71B",
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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
                Text = "按 A 备份 · Y 恢复",
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>下载模式磁贴引用：状态行与摘要行（小/长/大三种尺寸按需显示）</summary>
    public sealed class DownloadModeTileRefs
    {
        public Border Tile = null!;
        public TextBlock Status = null!;
        public TextBlock Summary = null!;
    }

    /// <summary>
    /// 构建下载模式小组件磁贴：小=图标+状态；长=图标+名称+状态；大=再补一行"完成后：睡眠/休眠/关机"。
    /// 状态文字由 FillDownloadState 写入（未进入时隐藏，进入后显示剩余时间）。
    /// </summary>
    public static (Border Tile, DownloadModeTileRefs Refs) BuildDownloadTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var label = string.IsNullOrWhiteSpace(item.Name) ? "下载模式" : item.Name;
        const string glyph = "\uE896"; // 下载箭头

        var refs = new DownloadModeTileRefs();
        var grid = new Grid();

        TextBlock Status(double fontSize) => new()
        {
            Text = "",
            Visibility = Visibility.Collapsed,
            FontSize = fontSize,
            Opacity = 0.85,
            Foreground = white,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };

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
            refs.Status = Status(10);
            refs.Status.MaxWidth = width - 12;
            stack.Children.Add(refs.Status);
            grid.Children.Add(stack);
        }
        else if (item.Size == TileSize.Wide)
        {
            refs.Summary = new TextBlock { Visibility = Visibility.Collapsed };
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
            refs.Status = Status(12);
            refs.Status.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(refs.Status);
            row.Children.Add(col);
            grid.Children.Add(row);
        }
        else
        {
            var stack = new StackPanel
            {
                Spacing = 5,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 36,
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
            refs.Status = Status(13);
            refs.Status.MaxWidth = width - 28;
            stack.Children.Add(refs.Status);
            refs.Summary = new TextBlock
            {
                Text = "A:设置并进入下载模式",
                FontSize = 12,
                Opacity = 0.72,
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>把下载模式状态写入磁贴：未进入时只显示标题（状态行隐藏），进入后显示剩余 h:mm:ss
    /// 与完成后动作</summary>
    public static void FillDownloadState(DownloadModeTileRefs refs, LaunchItem item)
    {
        if (!DownloadModeService.IsActive)
        {
            refs.Status.Visibility = Visibility.Collapsed;
            refs.Status.Text = "";
            if (refs.Summary.Visibility != Visibility.Collapsed) refs.Summary.Text = "A:设置并进入下载模式";
            return;
        }

        refs.Status.Visibility = Visibility.Visible;
        var remain = DownloadModeService.Remaining;
        if (remain <= TimeSpan.Zero)
        {
            refs.Status.Text = "已完成";
        }
        else
        {
            refs.Status.Text = remain.TotalHours >= 1
                ? $"剩余 {(int)remain.TotalHours}:{remain.Minutes:D2}:{remain.Seconds:D2}"
                : $"剩余 {remain.Minutes}:{remain.Seconds:D2}";
        }

        if (refs.Summary.Visibility != Visibility.Collapsed)
        {
            refs.Summary.Text = "完成后：" + DownloadModeService.AfterAction switch
            {
                PowerAction.Sleep => "睡眠",
                PowerAction.Hibernate => "休眠",
                PowerAction.Shutdown => "关机",
                _ => "无操作"
            };
        }
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
            parts.Add(double.IsNaN(s.CpuUsage) ? "CPU —" : $"CPU {s.CpuUsage:0}%");
        if ((metrics & MonitorMetric.GpuUsage) != 0)
            parts.Add(double.IsNaN(s.GpuUsage) ? "GPU —" : $"GPU {s.GpuUsage:0}%");
        if ((metrics & MonitorMetric.Memory) != 0)
            parts.Add(double.IsNaN(s.MemPercent) ? "内存 —" : $"内存 {s.MemPercent:0}%");
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
        // 小黄鸭图标：简约线条风格（白描边），与界面整体一致
        static Viewbox Duck(double size)
        {
            var pen = new SolidColorBrush(Microsoft.UI.Colors.White);

            Microsoft.UI.Xaml.Shapes.Path Line(string data) => new()
            {
                Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
                Stroke = pen,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };

            var eye = new Ellipse
            {
                Width = 1.5,
                Height = 1.5,
                Fill = pen
            };
            Canvas.SetLeft(eye, 6.25);
            Canvas.SetTop(eye, 5.65);

            var canvas = new Canvas { Width = 24, Height = 24 };
            // 身体：胸部 → 腹部 → 上翘的尾巴 → 背部，一笔闭合（朝左的橡皮鸭）
            canvas.Children.Add(Line("M5.4,9.9 C3.2,11.0 2.4,13.2 2.6,15.2 C2.9,18.4 5.8,20.6 9.8,20.6 C13.4,20.6 16.6,19.3 18.9,16.9 C20.3,15.5 21.6,13.8 21.7,12.0 C21.8,10.6 20.7,9.8 19.6,10.2 C17.8,10.8 15.2,10.6 12.8,10.5 C10.4,10.4 7.6,10.0 5.4,9.9 Z"));
            // 翅膀
            canvas.Children.Add(Line("M7.8,12.8 C10.0,12.4 12.6,13.0 14.4,14.4 C15.7,15.5 16.3,16.6 15.9,17.4 C15.5,18.1 14.3,18.0 13.3,17.5 C11.3,16.5 9.2,14.7 7.8,12.8 Z"));
            // 头部（下缘开口，与身体自然衔接）
            canvas.Children.Add(Line("M5.7,9.8 A3.4,3.4 0 1,1 10.7,9.8"));
            // 嘴
            canvas.Children.Add(Line("M5.2,6.2 L2.4,6.8 L5.2,7.4"));
            canvas.Children.Add(eye);

            return new Viewbox
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Child = canvas
            };
        }

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
            stack.Children.Add(Duck(34));
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
            row.Children.Add(Duck(42));
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
            stack.Children.Add(Duck(54));
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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

    /// <summary>屏幕翻译磁贴的可刷新元素引用（开关状态变化时只改文字/状态点，不重建磁贴）</summary>
    public sealed class TranslateTileRefs
    {
        public Border Tile = null!;
        public Microsoft.UI.Xaml.Shapes.Ellipse Dot = null!;
        public TextBlock Status = null!;
        public TextBlock Summary = null!;
    }

    /// <summary>
    /// 屏幕翻译自绘图标：一个显示器外框 + 底部两行字幕条（白色描边/填充，按传入尺寸等比缩放）。
    /// 比字体图标更贴合"屏幕 + 译文"，小尺寸下也更清晰。
    /// </summary>
    private static FrameworkElement ScreenTranslateIcon(double size)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var stroke = Math.Max(1.2, size / 22);
        var canvas = new Canvas
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var screen = new Border
        {
            Width = size,
            Height = size * 0.66,
            CornerRadius = new CornerRadius(size * 0.12),
            BorderBrush = white,
            BorderThickness = new Thickness(stroke),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
        };
        Canvas.SetLeft(screen, 0);
        Canvas.SetTop(screen, 0);
        canvas.Children.Add(screen);

        // 字幕条：两条白色圆角横线，居中排在"屏幕"下半部分
        double barHeight = Math.Max(2, size / 16);
        Rectangle Bar(double widthRatio) => new()
        {
            Width = size * widthRatio,
            Height = barHeight,
            RadiusX = barHeight / 2,
            RadiusY = barHeight / 2,
            Fill = white,
            Opacity = 0.95
        };

        var line1 = Bar(0.56);
        Canvas.SetLeft(line1, size * 0.22);
        Canvas.SetTop(line1, size * 0.38);
        canvas.Children.Add(line1);

        var line2 = Bar(0.36);
        Canvas.SetLeft(line2, size * 0.32);
        Canvas.SetTop(line2, size * 0.52);
        canvas.Children.Add(line2);

        // 底座：细支架 + 底座横线
        var stand = new Rectangle
        {
            Width = size * 0.26,
            Height = Math.Max(1.2, size / 30),
            Fill = white,
            Opacity = 0.9
        };
        Canvas.SetLeft(stand, size * 0.37);
        Canvas.SetTop(stand, size * 0.71);
        canvas.Children.Add(stand);

        var foot = new Rectangle
        {
            Width = size * 0.52,
            Height = Math.Max(1.2, size / 30),
            RadiusX = size / 60,
            RadiusY = size / 60,
            Fill = white,
            Opacity = 0.9
        };
        Canvas.SetLeft(foot, size * 0.24);
        Canvas.SetTop(foot, size * 0.85);
        canvas.Children.Add(foot);

        return canvas;
    }

    /// <summary>构建屏幕翻译小组件磁贴：小=图标+状态，长=图标+名称+状态，大=再加操作摘要</summary>
    public static (Border Tile, TranslateTileRefs Refs) BuildScreenTranslateTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var label = string.IsNullOrWhiteSpace(item.Name) ? "屏幕翻译" : item.Name;
        var refs = new TranslateTileRefs();

        Grid StatusRow(double fontSize)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
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
                Text = "已关闭",
                FontSize = fontSize,
                Opacity = 0.85,
                Foreground = white,
                IsHitTestVisible = false
            };
            stack.Children.Add(refs.Dot);
            stack.Children.Add(refs.Status);
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            host.Children.Add(stack);
            return host;
        }

        var grid = new Grid();
        if (item.Size == TileSize.Small)
        {
            var stack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(ScreenTranslateIcon(24));
            stack.Children.Add(StatusRow(10));
            grid.Children.Add(stack);
            refs.Summary = new TextBlock { Visibility = Visibility.Collapsed };
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
            row.Children.Add(ScreenTranslateIcon(30));
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
            col.Children.Add(StatusRow(12));
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
            stack.Children.Add(ScreenTranslateIcon(38));
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
            stack.Children.Add(StatusRow(13));
            refs.Summary = new TextBlock
            {
                Text = "A:开启翻译",
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>把"翻译中/已关闭"与目标语言写入屏幕翻译磁贴</summary>
    public static void FillScreenTranslateState(TranslateTileRefs refs, LaunchItem item)
    {
        var on = ScreenTranslateService.IsRunning;
        refs.Dot.Fill = new SolidColorBrush(on
            ? Windows.UI.Color.FromArgb(0xFF, 0x66, 0xE0, 0x8A)   // 绿：翻译中
            : Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)); // 灰白：已关闭
        refs.Status.Text = on ? "翻译中" : "已关闭";

        if (refs.Summary.Visibility == Visibility.Collapsed) return;
        var target = item.TranslateTarget switch { "en" => "英文", "ja" => "日文", _ => "中文" };
        refs.Summary.Text = on ? $"A:关闭翻译 · 译为{target}" : $"A:开启翻译 · 译为{target}";
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
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

    // ==================== 性能控制 ====================

    /// <summary>性能磁贴上的快捷项（磁贴一次显示一项，右摇杆←→在已开启项间切换）</summary>
    public enum PerformanceQuickItem
    {
        /// <summary>电源模式（LB/RB 选择档位）</summary>
        PowerMode = 0,
        /// <summary>全局锁帧（LB/RB 按 5 帧步进调整）</summary>
        FrameLimit = 1,
        /// <summary>功耗控制（LB/RB 按 1W 调整持续功耗，其余两项自动跟随）</summary>
        PowerLimit = 2,
        /// <summary>CPU 频率（LB/RB 按 100MHz 调整最高频率）</summary>
        CpuFrequency = 3,
        /// <summary>CPU 睿频（LB/RB 切换开关）</summary>
        CpuBoost = 4
    }

    /// <summary>该磁贴已开启的快捷项（固定顺序：电源模式 → 全局锁帧 → 功耗控制 → CPU 频率 → CPU 睿频）；都没开启时为空</summary>
    public static PerformanceQuickItem[] EnabledQuickItems(LaunchItem item)
    {
        var list = new List<PerformanceQuickItem>(5);
        if (item.PerformancePowerMode) list.Add(PerformanceQuickItem.PowerMode);
        if (item.PerformanceFrameLimit) list.Add(PerformanceQuickItem.FrameLimit);
        if (item.PerformancePowerLimit) list.Add(PerformanceQuickItem.PowerLimit);
        if (item.PerformanceCpuFrequency) list.Add(PerformanceQuickItem.CpuFrequency);
        if (item.PerformanceCpuBoost) list.Add(PerformanceQuickItem.CpuBoost);
        return [.. list];
    }

    /// <summary>磁贴当前显示的快捷项：记录项已关闭时回落到第一个启用项；都未开启时按电源模式只读展示</summary>
    public static PerformanceQuickItem CurrentQuickItem(LaunchItem item)
    {
        var enabled = EnabledQuickItems(item);
        if (enabled.Length == 0) return PerformanceQuickItem.PowerMode;
        var option = (PerformanceQuickItem)item.PerformanceOption;
        return enabled.Contains(option) ? option : enabled[0];
    }

    /// <summary>性能控制磁贴的可刷新元素引用（右摇杆切换查看项 / 读数变化时只改内容，不重建磁贴）</summary>
    public sealed class PerformanceTileRefs
    {
        public Border Tile = null!;       // 磁贴外壳
        public FontIcon Icon = null!;     // 当前查看项图标
        public TextBlock Name = null!;    // 查看项名称（电源模式 / 全局锁帧）
        public TextBlock Reading = null!; // 当前读数（档位名 / 锁帧 FPS）
        public Grid SlideRow = null!;     // 内容整体（切换查看项时左右滑动 + 淡入的动画载体）
        public TextBlock Hint = null!;    // 底部状态（当前生效 / 待应用）
        public Microsoft.UI.Xaml.Shapes.Path ModeIcon = null!; // 当前快捷项的图形
        /// <summary>限帧后端的品牌标识（NVIDIA / AMD / intel / RTSS），与 ModeIcon 互斥显示</summary>
        public TextBlock Brand = null!;
        /// <summary>电源模式待应用档位（0=最佳能效 1=平衡 2=最佳性能；-1=未选择）</summary>
        public int PendingPower = -1;
        /// <summary>全局锁帧待应用值（FPS；-1=未选择）</summary>
        public int PendingFrame = -1;
        /// <summary>持续功耗待应用值（W；-1=未选择）</summary>
        public int PendingPowerLimit = -1;
        /// <summary>CPU 最高频率待应用值（MHz；-1=未选择，0=不限制）</summary>
        public int PendingCpuMhz = -1;
        /// <summary>CPU 睿频待应用状态（-1=未选择，0=关闭，2=开启）</summary>
        public int PendingBoost = -1;
        /// <summary>睿频开关胶囊（仅睿频快捷项显示，代替数值读数）</summary>
        public Border BoostPill = null!;
        public Border BoostDot = null!;
    }

    /// <summary>三档电源模式的显示文案、强调色与图形（与性能面板卡片一致：叶片/滑杆/闪电）</summary>
    private static readonly (string Label, Windows.UI.Color Color, string Data, bool Stroke)[] PerfPowerModes =
    [
        ("最佳能效", Windows.UI.Color.FromArgb(0xFF, 0x66, 0xE0, 0x8A),
            "M20,4 C10.5,4 4.5,9.5 4.5,16.5 C4.5,17.7 4.7,18.7 5,19.5 C12.2,19.5 20,14 20,4 Z", false),
        ("平衡", Windows.UI.Color.FromArgb(0xFF, 0x7A, 0xB8, 0xFF),
            "M3.5,8.5 H20.5 M3.5,15.5 H20.5 " +
            "M9,6.2 A2.3,2.3 0 1,1 9,10.8 A2.3,2.3 0 1,1 9,6.2 Z " +
            "M15,13.2 A2.3,2.3 0 1,1 15,17.8 A2.3,2.3 0 1,1 15,13.2 Z", true),
        ("最佳性能", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB3, 0x66),
            "M13.4,2 L5.6,13.4 H11.2 L10.4,21.8 L18.2,10.4 H12.6 Z", false)
    ];

    /// <summary>
    /// 构建性能控制小组件磁贴：固定"大"尺寸（2×2），垂直展示标题、大读数与底部设置提示。
    /// 内容装在一个可整体平移的容器里，切换查看项时由 MainPage 播左右滑入动画。
    /// 内容由 FillPerformanceTile 按当前查看项写入。
    /// </summary>
    public static (Border Tile, PerformanceTileRefs Refs) BuildPerformanceTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var refs = new PerformanceTileRefs
        {
            Icon = new FontIcon
            {
                Glyph = "\uE850",
                FontSize = 20,
                Foreground = white,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed // 标题只留文字
            },
            Name = new TextBlock
            {
                FontSize = 19,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = white,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            },
            Reading = new TextBlock
            {
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0),
                IsHitTestVisible = false
            }
        };

        var grid = new Grid { Padding = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        refs.Name.MaxWidth = width - 64;
        refs.Reading.MaxWidth = width - 32;

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.Children.Add(refs.Icon);
        title.Children.Add(refs.Name);

        // 标题固定不动；只有下方档位内容在 LB/RB 切换时左右滑动
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(title);

        var slide = new Grid
        {
            RenderTransform = new TranslateTransform(),
            IsHitTestVisible = false
        };
        refs.ModeIcon = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = 34,
            Height = 34,
            Margin = new Thickness(0),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        refs.Brand = new TextBlock
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = white,
            CharacterSpacing = 20,
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        // 图标槽：固定 44 高，ModeIcon 与品牌文字都在这里居中；切换查看项/读数变化时图标不再上下串位
        var iconSlot = new Grid { Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        iconSlot.Children.Add(refs.ModeIcon);
        iconSlot.Children.Add(refs.Brand);

        refs.BoostDot = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = white,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 0, 0)
        };
        refs.BoostPill = new Border
        {
            Width = 48,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Child = refs.BoostDot,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        // 数值槽：固定 44 高、内容顶端对齐；读数与睿频胶囊二选一，同一位置、与上方图标固定间距
        var valueSlot = new Grid { Height = 44 };
        valueSlot.Children.Add(refs.Reading);
        valueSlot.Children.Add(refs.BoostPill);
        var body = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        body.Children.Add(iconSlot);
        body.Children.Add(valueSlot);
        slide.Children.Add(body);
        Grid.SetRow(slide, 1);
        content.Children.Add(slide);
        refs.SlideRow = slide;
        grid.Children.Add(content);

        var hint = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Opacity = 0.7,
            Foreground = white,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        refs.Hint = hint;
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>全局锁帧图形：仪表盘弧 + 指针 + 圆心（描边线条，与电源模式三档图形同风格）</summary>
    private static readonly string PerfFrameIconData =
        "M3.6,17.4 A8.6,8.6 0 1,1 20.4,17.4 M12,16.4 L16.4,9.8 " +
        "M12,14.9 A1.7,1.7 0 1,1 12,18.3 A1.7,1.7 0 1,1 12,14.9 Z";

    /// <summary>CPU 频率图形：芯片外框 + 内核 + 引脚（描边线条）</summary>
    private static readonly string PerfCpuIconData =
        "M7,7 H17 V17 H7 Z M10,10 H14 V14 H10 Z " +
        "M10,3.6 V7 M14,3.6 V7 M10,17 V20.4 M14,17 V20.4 " +
        "M3.6,10 H7 M3.6,14 H7 M17,10 H20.4 M17,14 H20.4";

    /// <summary>CPU 睿频图形：闪电（描边线条）</summary>
    private static readonly string PerfBoostIconData =
        "M13.6,2.4 L5.9,13.4 H11.2 L10.4,21.6 L18.1,10.6 H12.8 Z";

    /// <summary>把当前快捷项写入性能控制磁贴：开启快捷项时显示待应用值，否则只显示当前生效值</summary>
    public static void FillPerformanceTile(PerformanceTileRefs refs, LaunchItem item)
    {
        var quick = CurrentQuickItem(item);
        refs.Reading.Visibility = Visibility.Visible;
        refs.BoostPill.Visibility = Visibility.Collapsed;
        // 图标与数值都由固定槽位居中排布，不在此处做位移
        refs.ModeIcon.Width = 34;
        refs.ModeIcon.Height = 34;
        if (quick == PerformanceQuickItem.CpuBoost && item.PerformanceCpuBoost)
        {
            FillCpuBoostTile(refs);
            return;
        }
        if (quick == PerformanceQuickItem.CpuFrequency && item.PerformanceCpuFrequency)
        {
            FillCpuFrequencyTile(refs);
            return;
        }
        if (quick == PerformanceQuickItem.PowerLimit && item.PerformancePowerLimit)
        {
            FillPowerLimitTile(refs);
            return;
        }
        if (quick == PerformanceQuickItem.FrameLimit && item.PerformanceFrameLimit)
        {
            FillFrameLimitTile(refs);
            return;
        }

        refs.Icon.Glyph = "\uE850"; // 电池（电源模式）
        refs.Name.Text = "电源模式";

        var power = PerformanceService.Cached;
        var applied = power.Available ? (int)power.Level : -1;
        if (!item.PerformancePowerMode)
        {
            // 未开启快捷项：磁贴只显示当前模式，不带图形
            refs.ModeIcon.Visibility = Visibility.Collapsed;
            refs.Brand.Visibility = Visibility.Collapsed;
            refs.Reading.Text = power.Available ? power.Display : "—";
            refs.Hint.Text = "";
            return;
        }

        // 已开启：大号图形 + 档位名；待应用档位与当前生效档位在底部标注
        var selected = refs.PendingPower >= 0 ? refs.PendingPower : applied;
        if (selected < 0) selected = 1;
        var (label, color, data, stroke) = PerfPowerModes[selected];
        var brush = new SolidColorBrush(color);
        refs.ModeIcon.Visibility = Visibility.Visible;
        refs.ModeIcon.Opacity = 1;
        refs.Brand.Visibility = Visibility.Collapsed;
        refs.ModeIcon.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);
        refs.ModeIcon.Fill = brush;
        refs.ModeIcon.Stroke = stroke ? brush : null;
        refs.ModeIcon.StrokeThickness = stroke ? 1.8 : 0;
        refs.Reading.Text = label;
        SetQuickHint(refs, selected == applied);
    }

    /// <summary>功耗控制的转速表图标：表盘弧线 + 三根刻度 + 指针 + 中心轴（线条圆头）</summary>
    private const string PerfThrottleIconData =
        "M3.4,15 A8.6,8.6 0 0 1 20.6,15 " +
        "M12,6.4 V8.6 M5.9,8.9 L7.8,10.8 M18.1,8.9 L16.2,10.8 " +
        "M12,15 L17,10.5 " +
        "M12,13.4 A1.6,1.6 0 1,1 12,16.6 A1.6,1.6 0 1,1 12,13.4 Z";

    private static void FillPowerLimitTile(PerformanceTileRefs refs)
    {
        refs.Icon.Glyph = "\uE9D9"; // 速度表（功耗控制）
        refs.Name.Text = "功耗控制";
        refs.Brand.Visibility = Visibility.Collapsed;
        refs.ModeIcon.Visibility = Visibility.Visible;
        refs.ModeIcon.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), PerfThrottleIconData);
        var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB3, 0x66));
        refs.ModeIcon.Fill = null;
        refs.ModeIcon.Stroke = brush;
        refs.ModeIcon.StrokeThickness = 2.2;
        refs.ModeIcon.StrokeStartLineCap = PenLineCap.Round;
        refs.ModeIcon.StrokeEndLineCap = PenLineCap.Round;
        refs.ModeIcon.StrokeLineJoin = PenLineJoin.Round;

        var state = CpuPowerService.Cached;
        refs.Reading.Inlines.Clear();
        if (!state.Enabled)
        {
            // 尚未授权：磁贴提示按 A 启用（A 会拉起管理员授权助手）
            refs.ModeIcon.Opacity = 0.5;
            refs.Reading.Inlines.Add(new Run { Text = "未启用", FontSize = 20 });
            refs.Hint.Text = "按 A 启用";
            refs.Hint.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB3, 0x66));
            return;
        }
        var writable = state.Supported && state.Limits.Length > 0 && state.Limits[0].Writable;
        if (!writable)
        {
            refs.ModeIcon.Opacity = 0.5;
            refs.Reading.Inlines.Add(new Run { Text = "不可用", FontSize = 20 });
            refs.Hint.Text = "";
            return;
        }

        refs.ModeIcon.Opacity = 1;
        var applied = (int)Math.Round(state.Limits[0].Watts);
        var selected = refs.PendingPowerLimit >= 0 ? refs.PendingPowerLimit : applied;
        refs.Reading.Inlines.Add(new Run
        {
            Text = selected.ToString(),
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        });
        refs.Reading.Inlines.Add(new Run
        {
            Text = " W",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))
        });
        SetQuickHint(refs, selected == applied);
    }

    /// <summary>限帧后端品牌标识（文字、品牌色、磁贴用字号）：NVIDIA / AMD / intel / RTSS；未知返回空文字
    /// （磁贴回退仪表图形，性能面板也不显示标识）</summary>
    public static (string Text, Windows.UI.Color Color, double Size) BackendBrand(string? backend) => backend switch
    {
        "NVIDIA 驱动" => ("NVIDIA", Windows.UI.Color.FromArgb(0xFF, 0x76, 0xB9, 0x00), 22.0),
        "AMD ADLX" => ("AMD", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 28.0),
        "Intel IGCL" => ("intel", Windows.UI.Color.FromArgb(0xFF, 0x4C, 0x9E, 0xEA), 27.0),
        "RTSS" => ("RTSS", Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23), 25.0),
        _ => ("", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 24.0)
    };

    /// <summary>全局锁帧磁贴：限帧后端品牌标识 + 当前/待应用 FPS</summary>
    private static void FillFrameLimitTile(PerformanceTileRefs refs)
    {
        refs.Icon.Glyph = "\uE916"; // 秒表（全局锁帧）
        refs.Name.Text = "全局锁帧";

        var state = FrameLimitService.Cached;
        if (!state.Supported)
        {
            refs.ModeIcon.Visibility = Visibility.Collapsed;
            refs.Brand.Visibility = Visibility.Collapsed;
            refs.Reading.Text = "不可用";
            refs.Hint.Text = "";
            return;
        }

        // 标识随实际生效的限帧后端切换：NVIDIA / AMD / intel / RTSS（未知后端回退仪表图形）
        var (brand, brandColor, brandSize) = BackendBrand(state.Backend);
        if (brand.Length > 0)
        {
            refs.Brand.Text = brand;
            refs.Brand.Foreground = new SolidColorBrush(brandColor);
            refs.Brand.FontSize = brandSize;
            refs.Brand.Visibility = Visibility.Visible;
            refs.ModeIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            refs.Brand.Visibility = Visibility.Collapsed;
            refs.ModeIcon.Visibility = Visibility.Visible;
            refs.ModeIcon.Opacity = 1;
            refs.ModeIcon.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), PerfFrameIconData);
            refs.ModeIcon.Fill = null;
            refs.ModeIcon.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x7A, 0xD7, 0xFF));
            refs.ModeIcon.StrokeThickness = 1.8;
            refs.ModeIcon.StrokeStartLineCap = PenLineCap.Round;
            refs.ModeIcon.StrokeEndLineCap = PenLineCap.Round;
            refs.ModeIcon.StrokeLineJoin = PenLineJoin.Round;
        }

        var applied = state.Fps;
        var selected = refs.PendingFrame >= 0 ? refs.PendingFrame : applied;
        refs.Reading.Inlines.Clear();
        if (selected <= 0)
        {
            refs.Reading.Inlines.Add(new Run { Text = "关闭", FontSize = 20 });
        }
        else
        {
            // 数值大字，FPS 作单位小字（同一行基线对齐）
            refs.Reading.Inlines.Add(new Run
            {
                Text = selected.ToString(),
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            });
            refs.Reading.Inlines.Add(new Run
            {
                Text = " FPS",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))
            });
        }
        SetQuickHint(refs, selected == applied);
    }

    /// <summary>CPU 频率磁贴：芯片图形 + 最高频率（0=不限制），LB/RB 按 100MHz 调整</summary>
    private static void FillCpuFrequencyTile(PerformanceTileRefs refs)
    {
        refs.Icon.Glyph = "\uE9D9";
        refs.Name.Text = "CPU 频率";
        refs.Brand.Visibility = Visibility.Collapsed;
        refs.ModeIcon.Visibility = Visibility.Visible;
        refs.ModeIcon.Opacity = 1;
        refs.ModeIcon.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), PerfCpuIconData);
        refs.ModeIcon.Fill = null;
        refs.ModeIcon.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x7A, 0xD7, 0xFF));
        refs.ModeIcon.StrokeThickness = 1.8;
        refs.ModeIcon.StrokeStartLineCap = PenLineCap.Round;
        refs.ModeIcon.StrokeEndLineCap = PenLineCap.Round;
        refs.ModeIcon.StrokeLineJoin = PenLineJoin.Round;

        var cpu = PerformanceService.CachedCpu;
        refs.Reading.Inlines.Clear();
        if (!cpu.FrequencySupported)
        {
            refs.ModeIcon.Opacity = 0.5;
            refs.Reading.Inlines.Add(new Run { Text = "不可用", FontSize = 20 });
            refs.Hint.Text = "";
            return;
        }

        var applied = cpu.MaxFrequencyMhz;
        var selected = refs.PendingCpuMhz >= 0 ? refs.PendingCpuMhz : applied;
        if (selected <= 0)
        {
            refs.Reading.Inlines.Add(new Run
            {
                Text = "不限制",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            });
        }
        else
        {
            refs.Reading.Inlines.Add(new Run
            {
                Text = selected.ToString(),
                FontSize = 26,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            });
            refs.Reading.Inlines.Add(new Run
            {
                Text = " MHz",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))
            });
        }
        SetQuickHint(refs, selected == applied);
    }

    /// <summary>CPU 睿频磁贴：向上箭头图形 + 开关胶囊（代替数值），LB/RB 切换</summary>
    private static void FillCpuBoostTile(PerformanceTileRefs refs)
    {
        refs.Icon.Glyph = "\uE9D9";
        refs.Name.Text = "CPU 睿频";
        refs.Brand.Visibility = Visibility.Collapsed;
        refs.ModeIcon.Visibility = Visibility.Visible;
        refs.ModeIcon.Width = 40;
        refs.ModeIcon.Height = 40;
        refs.ModeIcon.Opacity = 1;
        refs.ModeIcon.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), PerfBoostIconData);
        refs.ModeIcon.Fill = null;
        refs.ModeIcon.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x5B));
        refs.ModeIcon.StrokeThickness = 2;
        refs.ModeIcon.StrokeStartLineCap = PenLineCap.Round;
        refs.ModeIcon.StrokeEndLineCap = PenLineCap.Round;
        refs.ModeIcon.StrokeLineJoin = PenLineJoin.Round;

        var cpu = PerformanceService.CachedCpu;
        refs.Reading.Visibility = Visibility.Collapsed;
        refs.BoostPill.Visibility = Visibility.Visible;

        if (!cpu.BoostSupported)
        {
            refs.ModeIcon.Opacity = 0.5;
            refs.BoostPill.Opacity = 0.4;
            refs.BoostPill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            refs.BoostDot.HorizontalAlignment = HorizontalAlignment.Left;
            refs.BoostDot.Margin = new Thickness(3, 0, 0, 0);
            refs.Hint.Text = "";
            return;
        }

        refs.BoostPill.Opacity = 1;
        var appliedOn = cpu.BoostMode != 0;
        var selectedOn = refs.PendingBoost >= 0 ? refs.PendingBoost != 0 : appliedOn;
        refs.BoostPill.Background = new SolidColorBrush(selectedOn
            ? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B)
            : Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        refs.BoostDot.HorizontalAlignment = selectedOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        refs.BoostDot.Margin = selectedOn ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
        SetQuickHint(refs, selectedOn == appliedOn);
    }

    /// <summary>快捷项底部状态：当前生效 / 待应用</summary>
    private static void SetQuickHint(PerformanceTileRefs refs, bool applied)
    {
        refs.Hint.Text = applied ? "当前生效" : "待应用";
        refs.Hint.Foreground = new SolidColorBrush(applied
            ? Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB3, 0x66));
    }

    // ==================== 小猫咪（简易代理）====================

    /// <summary>小猫咪磁贴的可刷新元素引用（连接状态或服务器变化时只改文字与圆点，不重建磁贴）</summary>
    public sealed class ProxyTileRefs
    {
        public Border Tile = null!;                          // 磁贴外壳
        public Microsoft.UI.Xaml.Shapes.Ellipse Dot = null!; // 连接状态圆点
        public TextBlock Status = null!;                     // 已连接/已断开
        public TextBlock Node = null!;                       // 当前服务器（长/大尺寸才有）
        public TextBlock Summary = null!;                    // 按键摘要（仅大尺寸）
    }

    /// <summary>小猫咪线条图标：坐姿剪影描边，与小黄鸭图标同风格、可任意缩放不变形</summary>
    private static Viewbox MakeCatIcon(double size, Brush brush)
    {
        // 小猫咪：简约线条风格（坐姿、双耳、前腿、卷尾），与小黄鸭图标一致
        Microsoft.UI.Xaml.Shapes.Path Line(string data) => new()
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            Stroke = brush,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };

        var canvas = new Canvas { Width = 24, Height = 24 };
        // 头与身体轮廓（左侧为脸/胸，右侧为背，底边为坐姿）
        canvas.Children.Add(Line("M9.4,3.1 C7.6,4.8 5.8,6.8 5.4,8.8 C4.8,11.2 4.7,14.6 5.0,17.2 C5.2,19.2 5.5,20.5 5.9,21.2 L10.8,21.2 C13.8,21.2 16.4,21.0 17.6,19.5 C18.9,17.9 19.4,15.6 19.0,13.6 C18.4,11.3 17.0,9.7 15.4,8.9 C15.0,7.0 14.5,5.2 13.9,3.2 L12.1,5.9 C11.5,5.6 10.9,5.6 10.4,6.0 Z"));
        // 尾巴（贴地向右卷起）
        canvas.Children.Add(Line("M18.9,17.8 C21.0,18.2 22.4,19.4 22.2,20.5 C22.0,21.5 20.6,21.9 19.2,21.3"));
        // 前腿分缝
        canvas.Children.Add(Line("M6.9,14.4 C6.7,17.0 6.7,19.2 6.9,20.9"));

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = canvas,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
    }

    /// <summary>
    /// 构建小猫咪小组件磁贴：外壳与普通磁贴一致，内容按尺寸分档——
    /// 小(1×1)=图标+连接状态；长(2×1)=图标+名称+状态+服务器；大(2×2)=再加班按键摘要。
    /// 状态与服务器由 FillProxyTile 写入。
    /// </summary>
    public static (Border Tile, ProxyTileRefs Refs) BuildProxyTile(
        LaunchItem item, double width, double height, Windows.UI.Color accent, bool noColor)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var label = string.IsNullOrWhiteSpace(item.Name) ? "Clash" : item.Name;

        var refs = new ProxyTileRefs();

        Grid StatusRow(double fontSize)
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
                Text = "已断开",
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

        TextBlock NodeText(double fontSize, double maxWidth) => new()
        {
            Text = "服务器 —",
            FontSize = fontSize,
            Opacity = 0.7,
            Foreground = white,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = maxWidth,
            IsHitTestVisible = false
        };

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
            stack.Children.Add(MakeCatIcon(30, white));
            stack.Children.Add(StatusRow(10));
            refs.Node = new TextBlock { Visibility = Visibility.Collapsed };
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
            row.Children.Add(MakeCatIcon(36, white));
            var col = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
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
            col.Children.Add(StatusRow(12));
            refs.Node = NodeText(11, width - 96);
            refs.Node.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(refs.Node);
            row.Children.Add(col);
            grid.Children.Add(row);
            refs.Summary = new TextBlock { Visibility = Visibility.Collapsed };
        }
        else
        {
            var stack = new StackPanel
            {
                Spacing = 5,
                Margin = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(MakeCatIcon(46, white));
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 28,
                IsHitTestVisible = false
            });
            stack.Children.Add(StatusRow(13));
            refs.Node = NodeText(12, width - 28);
            stack.Children.Add(refs.Node);
            refs.Summary = new TextBlock
            {
                Text = "A:连接 · Y:最快",
                FontSize = 11,
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
            BorderBrush = new SolidColorBrush(TileBorderColorFor(accent)),
            BorderThickness = new Thickness(TileBorderThickness),
            Child = grid
        };
        tile.Opacity = Opacity;
        refs.Tile = tile;
        return (tile, refs);
    }

    /// <summary>把连接状态与当前节点写入磁贴引用</summary>
    public static void FillProxyTile(ProxyTileRefs refs, LaunchItem item)
    {
        var s = ProxyService.Current;

        refs.Dot.Fill = new SolidColorBrush(
            s.Connected ? Windows.UI.Color.FromArgb(0xFF, 0x66, 0xE0, 0x8A)      // 绿：已连接
            : s.Running ? Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)       // 蓝：内核已启动未连代理
            : Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));                 // 灰白：未启动
        refs.Status.Text = s.Connected ? "已连接" : s.Running ? "已启动" : "未连接";

        if (refs.Node.Visibility != Visibility.Collapsed)
            refs.Node.Text = s.Running
                ? (s.Node.Length > 0 ? s.Node : "未选择节点")
                : string.IsNullOrWhiteSpace(item.ProxySubscription) ? "未配置订阅" : "A 开始代理";
        if (refs.Summary.Visibility != Visibility.Collapsed)
            refs.Summary.Text = "A:连接 · Y:最快";
    }
}
