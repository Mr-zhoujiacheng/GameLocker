using System.Text.Json.Serialization;
using GameLocker.Services;
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
    Settings,
    /// <summary>天气（显示当前位置当天天气；A/点击刷新，大小与颜色可改）</summary>
    Weather,
    /// <summary>映射（把一个手柄键或键盘键映射到本磁贴；A/点击触发，大小与颜色可改）</summary>
    Mapping,
    /// <summary>修改器（按当前游戏进程自动下载并打开最新的风灵月影修改器；大小与颜色可改）</summary>
    Trainer,
    /// <summary>游戏存档（一键扫描并备份当前正在玩的游戏的存档；可选备份到本地目录或 WebDAV 云端）</summary>
    Backup,
    /// <summary>手柄映射（把若干手柄按键映射成键盘/鼠标/手柄输出；A/点击=开关映射，长按=关闭映射）</summary>
    GamepadMap
}

/// <summary>手柄映射的目标类型（仅 GamepadMapping 使用）</summary>
public enum MappingTargetKind
{
    /// <summary>键盘按键（SendInput 注入到前台程序）</summary>
    Key,
    /// <summary>鼠标按键（SendInput 注入到前台程序）</summary>
    Mouse,
    /// <summary>手柄按键（Windows 无法向其它进程注入 XInput，仅在本启动器内派发）</summary>
    Pad
}

/// <summary>一条手柄映射：源手柄键 → 目标（键盘键 / 鼠标键 / 手柄键）</summary>
public class GamepadMapping
{
    /// <summary>源手柄按键（可为组合，如 LB+RB；None=未设置）</summary>
    public GamepadButtons Source { get; set; } = GamepadButtons.None;

    /// <summary>目标类型</summary>
    public MappingTargetKind TargetKind { get; set; } = MappingTargetKind.Key;

    /// <summary>目标键盘虚拟键码（TargetKind=Key 时使用；0=未设置）</summary>
    public int TargetKey { get; set; }

    /// <summary>目标键盘修饰键标志（TargetKind=Key 时使用；见 KeySendService.Mod*）</summary>
    public int TargetMods { get; set; }

    /// <summary>目标鼠标按键（TargetKind=Mouse 时使用；见 KeySendService.Mouse*）</summary>
    public int TargetMouse { get; set; }

    /// <summary>目标手柄按键（TargetKind=Pad 时使用；仅在本启动器内派发）</summary>
    public GamepadButtons TargetPad { get; set; } = GamepadButtons.None;

    /// <summary>深拷贝一条映射（列表编辑与保存时与源对象隔离）</summary>
    public GamepadMapping Clone() => new()
    {
        Source = Source,
        TargetKind = TargetKind,
        TargetKey = TargetKey,
        TargetMods = TargetMods,
        TargetMouse = TargetMouse,
        TargetPad = TargetPad
    };
}

/// <summary>游戏存档目标（仅 WidgetKind.Backup 使用）</summary>
public enum BackupMode
{
    /// <summary>本地：默认 %APPDATA%\GameLocker\backups（可在编辑页指定其他文件夹）</summary>
    Local,
    /// <summary>云端：通过 WebDAV 上传到网盘（需先在 设置 → 云端备份 填地址/账号/应用密码）</summary>
    Cloud
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

    /// <summary>映射的手柄按键（仅 WidgetKind.Mapping 使用；None=未映射手柄键）</summary>
    public GamepadButtons MappingPad { get; set; } = GamepadButtons.None;

    /// <summary>映射的键盘虚拟键码（仅 WidgetKind.Mapping 使用；0=未映射键盘键）</summary>
    public int MappingKey { get; set; }

    /// <summary>键盘映射的修饰键标志（仅 WidgetKind.Mapping 使用；见 KeySendService.Mod*）</summary>
    public int MappingMods { get; set; }

    /// <summary>游戏存档目标：本地目录 / WebDAV 云端上传（仅 WidgetKind.Backup 使用）</summary>
    public BackupMode BackupMode { get; set; } = BackupMode.Local;

    /// <summary>游戏存档目标文件夹绝对路径（仅 WidgetKind.Backup 且 BackupMode=Local 时生效；
    /// 为空表示用默认目录 %APPDATA%\GameLocker\backups。Cloud 模式下忽略此值）</summary>
    public string? BackupFolder { get; set; }

    /// <summary>游戏存档保留份数（仅 WidgetKind.Backup 使用；默认保留最近 5 份）</summary>
    public int BackupKeep { get; set; } = 5;

    /// <summary>手柄映射列表（仅 WidgetKind.GamepadMap 使用；一条映射=一个手柄键映射到键盘/鼠标/手柄输出）</summary>
    public List<GamepadMapping> GamepadMappings { get; set; } = [];

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
        MappingPad = MappingPad,
        MappingKey = MappingKey,
        MappingMods = MappingMods,
        BackupMode = BackupMode,
        BackupFolder = BackupFolder,
        BackupKeep = BackupKeep,
        GamepadMappings = [.. GamepadMappings.Select(m => m.Clone())],
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
            WidgetKind.Weather => "\uE753", // 云（天气）
            WidgetKind.Mapping => "\uE765", // 键盘（磁贴映射按键）
            WidgetKind.Trainer => "\uE90F", // 扳手（修改器）
            WidgetKind.Backup => "\uE74E", // 保存（游戏存档）
            WidgetKind.GamepadMap => "\uE7FC", // 手柄（手柄映射）
            _ => "\uE8D6"                  // 音乐（音乐控制器）
        },
        _ => "\uE7F8"                  // desktop
    };

    [JsonIgnore]
    public bool HasCover => !string.IsNullOrEmpty(CoverFile) && IsValidImageFile(CoverFilePath);

    /// <summary>映射小组件当前映射的按键显示名（手柄键或键盘键；未设置为"未设置"）</summary>
    [JsonIgnore]
    public string MappingLabel => KeySendService.MappingLabel((int)MappingPad, MappingKey, MappingMods);

    /// <summary>手柄映射条数摘要（如 "3 条映射"；0 条为"未设置映射"）</summary>
    [JsonIgnore]
    public string GamepadMapLabel => GamepadMappings.Count == 0 ? "未设置映射" : $"{GamepadMappings.Count} 条映射";

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
