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

    /// <summary>磁贴圆角</summary>
    public const double Radius = 6;

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

    /// <summary>无颜色磁贴：显式选"none"，或未指定颜色且随机配色开关关闭（只显示图标或图片）</summary>
    public static bool IsNoColor(LaunchItem item) =>
        string.Equals(item.TileColor, "none", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrEmpty(item.TileColor) && !SettingsService.Current.RandomTileColor);

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

    /// <summary>磁贴强调色：指定色 → 随机色缓存（按 Target+Name 稳定）</summary>
    public static Windows.UI.Color AccentFor(LaunchItem item)
    {
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

    /// <summary>磁贴填充：立体开关开启时用顶部微亮、底部微暗的对角渐变模拟受光；关闭时纯色</summary>
    public static Brush TileFill(Windows.UI.Color accent)
    {
        if (!SettingsService.Current.Tile3D) return new SolidColorBrush(accent);
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0.6, 1),
            GradientStops =
            {
                new GradientStop { Color = Lighten(accent, 0.18), Offset = 0.0 },
                new GradientStop { Color = accent, Offset = 0.45 },
                new GradientStop { Color = Lighten(accent, -0.16), Offset = 1.0 }
            }
        };
    }

    /// <summary>按 amount（-1~1）提亮/压暗颜色</summary>
    public static Windows.UI.Color Lighten(Windows.UI.Color c, double amount)
    {
        static byte Adj(int v) => (byte)Math.Clamp(v, 0, 255);
        return Windows.UI.Color.FromArgb(c.A,
            Adj(c.R + (int)(c.R * amount)), Adj(c.G + (int)(c.G * amount)), Adj(c.B + (int)(c.B * amount)));
    }

    /// <summary>立体边缘：顶部一条半透明白高光、底部一条半透明黑阴影，模拟凸起受光</summary>
    public static void Add3DEdge(Grid host)
    {
        host.Children.Add(new Border
        {
            Height = 2,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(1, 1, 1, 0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            IsHitTestVisible = false
        });
        host.Children.Add(new Border
        {
            Height = 3,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(1, 0, 1, 1),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x4D, 0, 0, 0)),
            CornerRadius = new CornerRadius(0, 0, 5, 5),
            IsHitTestVisible = false
        });
    }

    /// <summary>
    /// 构建标准磁贴（主页/磁贴集副本页共用）：背景图片铺满 或 强调色底+图标+名称，
    /// Win10 磁贴风格；不含选中/标记描边与事件（由调用方附加）。
    /// </summary>
    public static Border BuildStandardTile(
        LaunchItem item, LaunchItem? group, double width, double height, Windows.UI.Color accent, bool noColor)
    {
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
            if (SettingsService.Current.Tile3D)
            {
                var edge = new Grid();
                Add3DEdge(edge);
                coverTile.Child = edge;
            }
            coverTile.Opacity = Opacity;
            return coverTile;
        }

        var grid = new Grid();

        // 图标：居中（组磁贴用应用网格 glyph；成员与原样式一致）
        double iconSize = item.Size == TileSize.Small ? 40 : 48;
        UIElement icon = item.HasIcon
            ? new Image
            {
                Source = item.IconImage,
                Width = iconSize,
                Height = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
            : new FontIcon
            {
                Glyph = item.Glyph,
                FontSize = item.Size == TileSize.Small ? 30 : 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
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

        // 名称：左下角（磁贴标题开关关闭、小磁贴空间不足、或无颜色磁贴时不显示；组成员同样显示）
        if (SettingsService.Current.ShowTileName && !noColor && item.Size != TileSize.Small)
            grid.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 13,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Margin = new Thickness(10, 0, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = width - 20
            });

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Radius),
            Background = noColor ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) : TileFill(accent),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
        };
        if (SettingsService.Current.Tile3D && !noColor) Add3DEdge(grid);
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
        if (SettingsService.Current.Tile3D) Add3DEdge(grid);

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
        if (SettingsService.Current.Tile3D && !noColor) Add3DEdge(grid);
        tile.Opacity = Opacity;
        return (tile, refs);
    }
}
