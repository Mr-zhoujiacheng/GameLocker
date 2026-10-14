using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GameLocker.Models;

/// <summary>快捷项类型</summary>
public enum LaunchKind
{
    /// <summary>本地程序 / 快捷方式 / 文档（经 shell 启动）</summary>
    App,
    /// <summary>网站 URL（交给默认浏览器）</summary>
    Url,
    /// <summary>文件夹（在资源管理器中打开）</summary>
    Folder,
    /// <summary>磁贴组（包含多个成员磁贴，主页按 A 原位展开/收起）</summary>
    Group,
    /// <summary>小组件（WidgetPage 添加，磁贴形态承载实时状态与交互，如音乐控制器）</summary>
    Widget
}

/// <summary>小组件类型</summary>
public enum WidgetKind
{
    /// <summary>音乐控制器（接管系统 SMTC，控制设备当前正在播放的媒体）</summary>
    Music,
    /// <summary>图片组（定向一个文件夹，扫描其中的图片轮番展示）</summary>
    Photos,
    /// <summary>磁贴集（添加时复制当前主页磁贴为快照，打开进入复制出的页面）</summary>
    TileSet,
    /// <summary>设置（常驻主页，点击进入软件设置页；不可删除/组合，可移动与改颜色大小）</summary>
    Settings
}

/// <summary>图片组轮播速度档位</summary>
public enum PhotoSpeed
{
    /// <summary>快：30 秒</summary>
    Fast,
    /// <summary>慢：3 分钟</summary>
    Slow,
    /// <summary>艺术风格：30 分钟</summary>
    Art
}

/// <summary>磁贴尺寸（Win10 磁贴风格）</summary>
public enum TileSize
{
    /// <summary>小（1×1）</summary>
    Small,
    /// <summary>长方形（2×1）</summary>
    Wide,
    /// <summary>大（2×2）</summary>
    Large
}

/// <summary>一个可启动的快捷项</summary>
public class LaunchItem
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public LaunchKind Kind { get; set; } = LaunchKind.App;

    /// <summary>磁贴尺寸（默认长方形）</summary>
    public TileSize Size { get; set; } = TileSize.Wide;

    /// <summary>自定义磁贴颜色（#RRGGBB）；为空表示按名称哈希自动配色</summary>
    public string? TileColor { get; set; }

    /// <summary>自定义图标文件名（icons 目录下的 png），为空表示未设置</summary>
    public string? IconFile { get; set; }

    /// <summary>磁贴背景图片文件名（icons 目录下）；设置后整块磁贴铺满图片，不显示图标与名称</summary>
    public string? CoverFile { get; set; }

    /// <summary>磁贴组成员（仅 Kind=Group 时使用）</summary>
    public List<LaunchItem> Members { get; set; } = [];

    /// <summary>小组件类型（仅 Kind=Widget 时使用）</summary>
    public WidgetKind WidgetKind { get; set; } = WidgetKind.Music;

    /// <summary>图片组定向的文件夹绝对路径（仅 WidgetKind.Photos 使用；为空表示未设置）</summary>
    public string? WidgetFolder { get; set; }

    /// <summary>图片组轮播速度档位（仅 WidgetKind.Photos 使用）</summary>
    public PhotoSpeed SlideshowSpeed { get; set; } = PhotoSpeed.Fast;

    /// <summary>图片组切换是否播放淡入动画（仅 WidgetKind.Photos 使用）</summary>
    public bool PhotoAnimation { get; set; } = true;

    /// <summary>磁贴集快照（仅 WidgetKind.TileSet 使用：添加时复制的当前主页磁贴列表）</summary>
    public List<LaunchItem> Snapshot { get; set; } = [];

    /// <summary>深拷贝一个快捷项（含组成员），用于磁贴集快照/进入副本页时与主页数据隔离</summary>
    public LaunchItem Clone() => new()
    {
        Name = Name,
        Target = Target,
        Kind = Kind,
        Size = Size,
        TileColor = TileColor,
        IconFile = IconFile,
        CoverFile = CoverFile,
        WidgetKind = WidgetKind,
        WidgetFolder = WidgetFolder,
        SlideshowSpeed = SlideshowSpeed,
        PhotoAnimation = PhotoAnimation,
        Members = [.. Members.Select(m => m.Clone())],
        Snapshot = [.. Snapshot.Select(s => s.Clone())]
    };

    /// <summary>图标存放目录（%APPDATA%\GameLocker\icons）</summary>
    public static readonly string IconsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "icons");

    /// <summary>无图标时界面显示的字形（按类型区分）</summary>
    [JsonIgnore]
    public string Glyph => Kind switch
    {
        LaunchKind.Url => "\uE774",    // globe
        LaunchKind.Folder => "\uE8B7", // folder
        LaunchKind.Group => "\uE71D",  // 应用网格（磁贴组）
        LaunchKind.Widget => WidgetKind switch
        {
            WidgetKind.Photos => "\uE8B9", // 图片（图片组，通常被轮播图覆盖）
            WidgetKind.TileSet => "\uE80A",// 集合（磁贴集）
            WidgetKind.Settings => "\uE713",// 齿轮（设置）
            _ => "\uE8D6"                  // 音乐（音乐控制器）
        },
        _ => "\uE7F8"                  // desktop
    };

    [JsonIgnore]
    public bool HasCover => !string.IsNullOrEmpty(CoverFile) && IsValidImageFile(CoverFilePath);

    /// <summary>存在且非空（0 字节的残留文件会导致解码失败、磁贴空白）</summary>
    private static bool IsValidImageFile(string path)
    {
        try { return new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    [JsonIgnore]
    public string CoverFilePath => Path.Combine(IconsDir, CoverFile ?? "");

    /// <summary>ImageBrush.ImageSource 绑定（UI 线程求值，无图片为 null）</summary>
    [JsonIgnore]
    public ImageSource? CoverImage
    {
        get
        {
            if (!HasCover) return null;
            try { return new BitmapImage(new Uri(CoverFilePath)); }
            catch { return null; }
        }
    }

    /// <summary>有背景图片时不显示图标/名称</summary>
    [JsonIgnore]
    public bool ShowIconAndName => !HasCover;

    [JsonIgnore]
    public bool HasIcon => ShowIconAndName && !string.IsNullOrEmpty(IconFile) && File.Exists(IconFilePath);

    [JsonIgnore]
    public string IconFilePath => Path.Combine(IconsDir, IconFile ?? "");

    /// <summary>Image.Source 绑定（UI 线程求值，无图标为 null）</summary>
    [JsonIgnore]
    public ImageSource? IconImage
    {
        get
        {
            if (!HasIcon) return null;
            try { return new BitmapImage(new Uri(IconFilePath)); }
            catch { return null; }
        }
    }

    [JsonIgnore]
    public Visibility IconVisibility => HasIcon ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility GlyphVisibility => HasIcon ? Visibility.Collapsed : Visibility.Visible;
}
