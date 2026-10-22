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
    /// <summary>磁贴集（已移除该功能，仅保留此枚举值用于旧配置反序列化与数据迁移）</summary>
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
    GamepadMap,
    /// <summary>OptiScaler（检测当前游戏，按 wiki 兼容列表给出适配性，确认后注入 OptiScaler 并重开游戏）</summary>
    OptiScaler,
    /// <summary>磁贴抽屉（主页上一块固定区域，内含多个可命名抽屉作为标签页，收纳磁贴）</summary>
    Drawer,
    /// <summary>回收站（系统回收站快捷入口：A/点击=打开回收站，Y=一键清空回收站）</summary>
    RecycleBin,
    /// <summary>前台应用监测（持续监测当前前台窗口所属进程，换前台窗口即刷新；A=冻结/解冻，Y=强制结束）</summary>
    Foreground,
    /// <summary>系统（一个磁贴收纳系统入口与开关：设置/我的电脑/音量/亮度/WiFi/蓝牙/移动热点/夜间模式；
    /// 右摇杆←→切换，A=打开或开关，LB/RB=调整）</summary>
    System,
    /// <summary>电源（常驻主页，A/点击弹出电源菜单：注销/睡眠/休眠/重启/关机；不可删除/组合，可移动与改颜色大小）</summary>
    Power,
    /// <summary>性能监控（A/点击=开启/关闭置顶悬浮监控条，屏幕上方居中显示帧率/帧延迟/帧波动/CPU/GPU/电池/内存）</summary>
    Monitor,
    /// <summary>文件传输（A/点击=打开局域网传输浮层：搜索同网段设备、连接后互发文件，收到文件需确认）</summary>
    FileTransfer
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

/// <summary>性能监控悬浮条在屏幕上的位置（六选一）</summary>
public enum MonitorPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight
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

    /// <summary>所属主页页号（0=主主页；副页从 1 开始。磁贴组按组自身页号归属，成员随组显示）</summary>
    public int PageIndex { get; set; }

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

    /// <summary>系统小组件当前停在哪一项（仅 WidgetKind.System 使用；见 SystemPanel 枚举顺序，0=Win 设置）</summary>
    public int SystemOption { get; set; }

    /// <summary>系统小组件启用项位掩码（仅 WidgetKind.System 使用；bit = 面板项序号，0 = 全部启用）</summary>
    public int SystemOptionMask { get; set; }

    /// <summary>图片组轮播速度档位（仅 WidgetKind.Photos 使用）</summary>
    public PhotoSpeed SlideshowSpeed { get; set; } = PhotoSpeed.Fast;

    /// <summary>图片组切换是否播放淡入动画（仅 WidgetKind.Photos 使用）</summary>
    public bool PhotoAnimation { get; set; } = true;

    /// <summary>磁贴集快照（功能已移除）：仅保留用于旧配置的一次性迁移——把旧磁贴集的快照内容转成一个副页；
    /// 为 null 时不写入 JSON，新配置里不会再出现该字段</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<LaunchItem>? Snapshot { get; set; }

    /// <summary>磁贴抽屉的抽屉列表（仅 WidgetKind.Drawer 使用）；为 null 时不写入 JSON。
    /// 每个抽屉是「名称 + 内含磁贴」，主页选中组件时 LB/RB 在各抽屉间导航。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<DrawerPage>? Drawers { get; set; }

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

    /// <summary>注入用的代理 DLL 文件名（仅 WidgetKind.OptiScaler 使用；空=自动按 wiki 建议，默认 dxgi.dll）</summary>
    public string? OptiDllName { get; set; }

    /// <summary>OptiScaler.ini 的 Dx12Upscaler 项（仅 WidgetKind.OptiScaler 使用；默认 auto）</summary>
    public string OptiDx12Upscaler { get; set; } = "auto";

    /// <summary>OptiScaler.ini 的帧生成开关（仅 WidgetKind.OptiScaler 使用；写 FrameGen 段的 Enabled）</summary>
    public bool OptiFrameGen { get; set; } = true;

    /// <summary>帧生成输入源（写 FrameGen 段的 FGInput；auto/nofg/dlssg/nvngxfg/fsrfg/fsrfg30/xefg/upscaler）</summary>
    public string OptiFgInput { get; set; } = "auto";

    /// <summary>帧生成输出（写 FrameGen 段的 FGOutput；auto/nofg/fsrfg/xefg/dlssg）</summary>
    public string OptiFgOutput { get; set; } = "auto";

    /// <summary>帧生成倍数（写 DLSSG / XeFG 段的 InterpolationCount；auto 或 1=2X … 5=6X）</summary>
    public string OptiFgMultiplier { get; set; } = "auto";

    /// <summary>FSR4 画质档（写 [FSR] 段的 Fsr4Preset；auto 或 0=原生AA … 5=超级性能）</summary>
    public string OptiFsr4Preset { get; set; } = "auto";

    /// <summary>FSR4 附加文件（官方包不含）：auto=不额外注入 / int8=兼容卡 / fp8=RDNA4</summary>
    public string OptiFsr4Files { get; set; } = "auto";

    /// <summary>显式指定的本地 OptiScaler 包路径（目录或 zip/7z/rar）；为空时"本地包"渠道扫描
    /// %APPDATA%\GameLocker\optiscaler\local 文件夹。仅 OptiChannel=local 时使用。</summary>
    public string? OptiLocalPackage { get; set; }

    /// <summary>要注入的 OptiScaler 版本渠道（仅 WidgetKind.OptiScaler 使用）：stable=官方正式版，
    /// beta=社区镜像的最新预览版（OptiScaler-Betas），local=不联网、直接用本地包文件夹里的包。
    /// 三种渠道 ini 与用法一致，只是取包来源不同。</summary>
    public string OptiChannel { get; set; } = "stable";

    /// <summary>选定的 OptiScaler Release 标签（如 v0.9.4；空=自动取该渠道最新版）</summary>
    public string OptiVersionTag { get; set; } = "";

    /// <summary>选定的 FSR4 附加包 Release 标签（空=自动取最新版）</summary>
    public string OptiFsr4VersionTag { get; set; } = "";

    /// <summary>性能监控悬浮条位置（仅 WidgetKind.Monitor 使用）</summary>
    public MonitorPosition MonitorPos { get; set; } = MonitorPosition.TopCenter;

    /// <summary>性能监控悬浮条大小倍率（仅 WidgetKind.Monitor 使用；叠加在自动适配之上，0.6~1.6）</summary>
    public double MonitorScale { get; set; } = 1.0;

    /// <summary>性能监控悬浮条不透明度（仅 WidgetKind.Monitor 使用；0~1，0 表示无外框只剩文字）</summary>
    public double MonitorOpacity { get; set; } = 1.0;

    /// <summary>文件传输的可发送目录（仅 WidgetKind.FileTransfer 使用；空=系统下载文件夹，浮层里列出该目录顶层文件）</summary>
    public string? FileTransferSendFolder { get; set; }

    /// <summary>文件传输的接收目录（仅 WidgetKind.FileTransfer 使用；空=系统下载文件夹，收到的文件直接存这里）</summary>
    public string? FileTransferRecvFolder { get; set; }

    /// <summary>深拷贝一个快捷项（含组成员），用于复制/迁移时与源数据隔离</summary>
    public LaunchItem Clone() => new()
    {
        Name = Name,
        Target = Target,
        Kind = Kind,
        Size = Size,
        PageIndex = PageIndex,
        TileColor = TileColor,
        IconFile = IconFile,
        CoverFile = CoverFile,
        WidgetKind = WidgetKind,
        WidgetFolder = WidgetFolder,
        SystemOption = SystemOption,
        SystemOptionMask = SystemOptionMask,
        SlideshowSpeed = SlideshowSpeed,
        PhotoAnimation = PhotoAnimation,
        MappingPad = MappingPad,
        MappingKey = MappingKey,
        MappingMods = MappingMods,
        BackupMode = BackupMode,
        BackupFolder = BackupFolder,
        BackupKeep = BackupKeep,
        OptiDllName = OptiDllName,
        OptiDx12Upscaler = OptiDx12Upscaler,
        OptiFrameGen = OptiFrameGen,
        OptiFgInput = OptiFgInput,
        OptiFgOutput = OptiFgOutput,
        OptiFgMultiplier = OptiFgMultiplier,
        OptiFsr4Preset = OptiFsr4Preset,
        OptiFsr4Files = OptiFsr4Files,
        OptiLocalPackage = OptiLocalPackage,
        OptiChannel = OptiChannel,
        OptiVersionTag = OptiVersionTag,
        OptiFsr4VersionTag = OptiFsr4VersionTag,
        MonitorPos = MonitorPos,
        MonitorScale = MonitorScale,
        MonitorOpacity = MonitorOpacity,
        GamepadMappings = [.. GamepadMappings.Select(m => m.Clone())],
        Members = [.. Members.Select(m => m.Clone())],
        Drawers = Drawers is null ? null : [.. Drawers.Select(d => d.Clone())]
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
            WidgetKind.OptiScaler => "\uE8C8", // 层叠帧（OptiScaler 帧生成/画质增强）
            WidgetKind.Drawer => "\uE8F1", // 书架（磁贴抽屉）
            WidgetKind.RecycleBin => "\uE74D", // 垃圾桶（回收站）
            WidgetKind.Foreground => "\uE769", // 暂停（前台应用监测：冻结）
            WidgetKind.System => "\uE713", // 设置（系统小组件：默认停在 Win 设置）
            WidgetKind.Power => "\uE7E8",  // 电源（电源菜单）
            WidgetKind.Monitor => "\uE9D9", // 诊断（性能监控）
            WidgetKind.FileTransfer => "\uE895", // 同步（局域网文件互传）
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
