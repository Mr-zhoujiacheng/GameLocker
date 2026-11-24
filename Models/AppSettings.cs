using System.Text.Json;
using System.Text.Json.Serialization;
using GameLocker.Services;

namespace GameLocker.Models;

/// <summary>窗口停靠方式：左/右贴边（侧边栏），或居中悬浮（四周不贴屏幕边）</summary>
public enum DockSide
{
    Left,
    Right,
    Center
}

/// <summary>磁贴主题：彩色（强调色纯色块，圆角）/ 液态玻璃（通透玻璃卡片 + accent 描边，圆角）/
/// 金属拉丝（横向拉丝纹理 + 工业风，微直角）/ 像素街机（硬色块 + 内阴影凸起，直角）</summary>
public enum TileTheme
{
    Colorful,
    NeonCyber,
    MetalBrushed,
    PixelArcade
}

/// <summary>磁贴主题兼容读取：旧配置里已删除的主题值（纯黑/全息/亚克力/极光/蒸汽波/线框/拟物）
/// 自动回退为彩色，避免整份设置 JSON 因未知枚举值反序列化失败而被重置。</summary>
public sealed class TileThemeCompatConverter : JsonConverter<TileTheme>
{
    public override TileTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
        && Enum.TryParse(reader.GetString(), out TileTheme value)
            ? value
            : TileTheme.Colorful;

    public override void Write(Utf8JsonWriter writer, TileTheme value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>磁贴圆角样式（与主题独立设置）</summary>
public enum TileCorner
{
    Rounded,
    Square
}

/// <summary>云端备份使用的网盘</summary>
public enum CloudProvider
{
    Tianyi,
    Baidu
}

/// <summary>性能控制面板的一套参数，可共用或按供电方式分别保存</summary>
public sealed class PowerProfile
{
    public PowerProfile Copy() => (PowerProfile)MemberwiseClone();
    /// <summary>0=节能 1=平衡 2=高性能；-1=未保存</summary>
    public int PowerMode { get; set; } = -1;

    /// <summary>全局锁帧 FPS；0=关闭；-1=未保存</summary>
    public int FrameLimitFps { get; set; } = -1;

    public bool CpuPowerEnabled { get; set; }
    public int CpuPowerPl1 { get; set; }
    public int CpuPowerPl2 { get; set; }
    public int CpuPowerFppt { get; set; }

    /// <summary>0=关闭 2=开启；-1=未保存</summary>
    public int Boost { get; set; } = -1;

    /// <summary>最高频率 MHz；0=不限制；-1=未保存</summary>
    public int MaxFrequencyMhz { get; set; } = -1;

    public GameCoreMode CoreMode { get; set; } = GameCoreMode.Auto;
    public bool CoreModeCaptured { get; set; }

    /// <summary>全局核心停放目标；0=全部（系统自动）；-1=未保存</summary>
    public int CoreSelection { get; set; } = -1;

}

/// <summary>应用设置（JSON 持久化，见 SettingsService）</summary>
public class AppSettings
{
    public int GameCoreCount { get; set; }
    public GameCoreMode GameCoreMode { get; set; } = GameCoreMode.Auto;

    /// <summary>全局核心停放目标：0=系统自动，其余为物理核心数；不影响游戏 CPU Sets 设置。</summary>
    public int GlobalCoreSelection { get; set; }

    /// <summary>首次接管前的核心停放值：电源方案 GUID → 设置 GUID → [交流, 直流]。</summary>
    public Dictionary<string, Dictionary<string, int[]>> CoreParkingOriginals { get; set; } = new();

    public bool SeparatePowerProfiles { get; set; }
    public PowerProfile SharedProfile { get; set; } = new();
    public string FrameLimitBackend { get; set; } = "Vendor";
    public string RtssDirectory { get; set; } = "";

    /// <summary>插电时的性能面板参数</summary>
    public PowerProfile AcProfile { get; set; } = new();

    /// <summary>电池供电时的性能面板参数</summary>
    public PowerProfile BatteryProfile { get; set; } = new();

    /// <summary>旧配置迁移标记：把全局功耗/核心设置复制到两套性能参数</summary>
    public bool PowerProfilesMigrated { get; set; }

    /// <summary>已保存的 CPU 持续功耗（PL1，W；0=未保存）。重启后由登录计划任务写回硬件</summary>
    public int CpuPowerPl1 { get; set; }

    /// <summary>已保存的 CPU 短时功耗（PL2，W；0=跟随持续功耗）</summary>
    public int CpuPowerPl2 { get; set; }

    /// <summary>已保存的 CPU 瞬时功耗（FPPT，W；0=跟随短时功耗）</summary>
    public int CpuPowerFppt { get; set; }

    /// <summary>保存过功耗目标后视为已接管：每次启动自动按保存值恢复，不再要求点击启用。
    /// 真正的写入由登录计划任务（管理员、无 UAC）完成；调节时才临时拉起助手授权</summary>
    public bool CpuPowerTakeover { get; set; }

    /// <summary>接管前固件允许的持续功耗上限（W；0=未记录）。
    /// 重启后登录任务会先写入限制，若没有这个记录，后续会话会把已写入的限制误当成固件上限而无法再调高</summary>
    public int CpuPowerOriginalPl1 { get; set; }

    /// <summary>接管前固件允许的短时功耗上限（W；0=未记录）</summary>
    public int CpuPowerOriginalPl2 { get; set; }

    /// <summary>首次启动时记录的原始基础电源方案 GUID：掌机上标准“平衡”方案不存在，
    /// 切回平衡时用它恢复（空=尚未记录）</summary>
    public string PowerModeBaseScheme { get; set; } = "";

    /// <summary>开机自动启动（写入 HKCU Run 键）</summary>
    public bool AutoStart { get; set; }

    /// <summary>启动程序后自动隐藏到托盘</summary>
    public bool HideAfterLaunch { get; set; } = true;

    /// <summary>启动时进入全屏</summary>
    public bool StartFullscreen { get; set; }

    /// <summary>弹出/隐藏滑入滑出动画（关闭窗口时按停靠方向滑出）</summary>
    public bool SummonAnimation { get; set; } = true;

    /// <summary>手柄操作音效：磁贴间移动、A 键、B 键的提示音（默认开启）</summary>
    public bool UiSoundEnabled { get; set; } = true;

    /// <summary>接管系统屏幕键盘：系统触屏键盘/屏幕键盘弹出时自动收起，改为弹出本程序键盘</summary>
    public bool TakeOverSystemKeyboard { get; set; } = true;

    /// <summary>平板边缘手势呼出：从任务栏对侧屏幕边缘向中心滑动唤起窗口（左/右停靠对应左/右边缘，全屏对应上边缘）</summary>
    public bool EdgeGestureSummon { get; set; } = true;

    /// <summary>窗口停靠方式（左右贴边，或居中悬浮）；居中时窗口不贴屏幕四边，边缘手势走顶边</summary>
    public DockSide SidebarSide { get; set; } = DockSide.Left;

    /// <summary>侧边栏宽度占屏幕比例（0.38~0.5，最小为允许的最窄观感，最大为屏幕一半；默认 40%）</summary>
    public double SidebarWidthPercent { get; set; } = 0.40;

    /// <summary>磁贴间距（像素，0~30）</summary>
    public double TileSpacing { get; set; } = 4;

    /// <summary>主页总页数（含主主页；副页块在添加方式（X 键）里新增，主页用 LT/RT 循环切换）</summary>
    public int HomePageCount { get; set; } = 1;

    /// <summary>磁贴主题（彩色 / 液态玻璃 / 金属拉丝 / 像素街机）；旧主题值读取时自动回退彩色</summary>
    [JsonConverter(typeof(TileThemeCompatConverter))]
    public TileTheme TileTheme { get; set; } = TileTheme.Colorful;

    /// <summary>磁贴圆角样式：未设置时按主题默认（彩色/液态玻璃=圆角，金属拉丝=微直角，像素街机=直角）</summary>
    public TileCorner? TileCornerShape { get; set; }

    /// <summary>磁贴透明度（0.3~1.0，1 为完全不透明）</summary>
    public double TileOpacity { get; set; } = 1.0;

    /// <summary>磁贴显示标题（名称）</summary>
    public bool ShowTileName { get; set; } = true;

    /// <summary>启动时自动检查 GitHub 最新版本（关于页可关闭）</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>记录运行日志（关于页可开关，默认关闭）：开启后复现问题，再导出日志给开发者排查</summary>
    public bool DiagnosticLog { get; set; }

    /// <summary>磁贴组展开/收起动画（成员磁贴交错淡入浮现 / 淡出收起）</summary>
    public bool GroupExpandAnimation { get; set; } = true;

    /// <summary>随机磁贴颜色：未指定颜色的磁贴随机配色；关闭后未指定颜色的磁贴显示为无颜色</summary>
    public bool RandomTileColor { get; set; } = true;

    /// <summary>磁贴抽屉：主页下方固定的抽屉区域总开关。关闭则抽屉区域整体隐藏（抽屉内容保留，重开即恢复）</summary>
    public bool DrawerEnabled { get; set; } = true;

    /// <summary>抽屉控件色（#RRGGBB；"none"=未指定，回退到抽屉自身的随机强调色）。
    /// 作用于当前标签的淡底与下划线、焦点进入抽屉时的焦点环，以及"+"按钮；面板本身无底色无边框</summary>
    public string DrawerColor { get; set; } = "none";

    /// <summary>卡片透明：开启后主页底部卡牌恢复半透明卡面（旧样式）；关闭为不透明卡面，叠压时后卡被完全遮住</summary>
    public bool DrawerTransparent { get; set; }

    /// <summary>抽屉内磁贴统一大小：开启后抽屉内所有磁贴忽略自身设定的大小，一律按小磁贴（1×1）显示</summary>
    public bool DrawerUniformSmall { get; set; }

    /// <summary>抽屉自动隐藏：开启后抽屉平时收起在屏幕下方之外（带动画向下滑出；面板仍占原布局位置，磁贴区高度不变）。
    /// 手柄右摇杆↑/↓ 呼出与收起；触屏在软件底部上划/下划呼出与收起</summary>
    public bool DrawerAutoHide { get; set; }

    /// <summary>顶栏显示时钟</summary>
    public bool ShowClock { get; set; } = true;

    /// <summary>顶栏显示手柄连接图标</summary>
    public bool ShowGamepadIcon { get; set; } = true;

    /// <summary>顶栏显示电池图标</summary>
    public bool ShowBattery { get; set; } = true;

    /// <summary>顶栏显示无线网络图标</summary>
    public bool ShowWifi { get; set; }

    /// <summary>顶栏显示蓝牙图标</summary>
    public bool ShowBluetooth { get; set; }

    /// <summary>顶栏显示通知图标（点击打开 Windows 通知中心）</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>顶栏显示夜间模式图标（点击切换暖色滤镜）</summary>
    public bool ShowNightLight { get; set; } = true;

    /// <summary>顶栏显示搜索图标（固定在图标组最左侧，点击打开自制搜索浮层）</summary>
    public bool ShowSearch { get; set; } = true;

    /// <summary>
    /// 系统顶栏（设置页「顶栏2」）总开关。
    /// 当前只记录偏好：窗口与各信息段尚未实现，开启后不会创建任何界面。
    /// 开发清单见 docs/topbar2-development-plan.md。
    /// </summary>
    public bool StatusBarEnabled { get; set; }

    /// <summary>顶栏2：AppBar 占位（最大化窗口避让）。关闭则浮在屏幕上、不改变工作区</summary>
    public bool StatusBarReserveSpace { get; set; } = true;

    /// <summary>顶栏2 高度（DIP，24–40）</summary>
    public int StatusBarHeight { get; set; } = 32;

    /// <summary>顶栏2 底盘材质：0=跟随系统亚克力 / 1=半透明 / 2=纯色 / 3=渐变</summary>
    public int StatusBarBackdropMode { get; set; }

    /// <summary>顶栏2 底色（#AARRGGBB；纯色/渐变/半透明使用）</summary>
    public string StatusBarColor { get; set; } = "#E61A1A1E";

    /// <summary>顶栏2 渐变第二色</summary>
    public string StatusBarColor2 { get; set; } = "#E62A2A36";

    /// <summary>顶栏2 文字颜色（#AARRGGBB）</summary>
    public string StatusBarTextColor { get; set; } = "#F2FFFFFF";

    /// <summary>顶栏2 字号（10–18，DIP）</summary>
    public int StatusBarFontSize { get; set; } = 12;

    /// <summary>顶栏2：前台全屏时自动隐藏</summary>
    public bool StatusBarFullscreenHide { get; set; } = true;

    /// <summary>
    /// 顶栏2：顶部任务栏模式。需先把系统任务栏位置设为“顶部”；
    /// 开启后强制隐藏任务栏，顶栏2 按任务栏高度填满顶部预留条，不再注册 AppBar。
    /// </summary>
    public bool StatusBarTaskbarTopMode { get; set; }

    /// <summary>顶栏2：显示时间日期段</summary>
    public bool StatusBarShowClock { get; set; } = true;

    /// <summary>顶栏2：显示电池段</summary>
    public bool StatusBarShowBattery { get; set; } = true;

    /// <summary>顶栏2：显示网络段</summary>
    public bool StatusBarShowNetwork { get; set; } = true;

    /// <summary>顶栏2：显示输入法段</summary>
    public bool StatusBarShowIme { get; set; } = true;

    /// <summary>顶栏2：显示通知段</summary>
    public bool StatusBarShowNotifications { get; set; } = true;

    /// <summary>顶栏2：显示控制中心入口</summary>
    public bool StatusBarShowControlCenter { get; set; } = true;

    /// <summary>顶栏2：控制中心用本程序的弹层（false 时改用 Windows 自带控制中心，Win+A）</summary>
    public bool StatusBarCustomControlCenter { get; set; } = true;

    /// <summary>顶栏2：显示资源监视段（CPU / 内存，2 秒刷新）</summary>
    public bool StatusBarShowResources { get; set; }

    /// <summary>顶栏2：显示电池健康段（设计容量/满充容量，5 分钟刷新）</summary>
    public bool StatusBarShowBatteryHealth { get; set; }

    /// <summary>顶栏2：显示专注模式入口</summary>
    public bool StatusBarShowFocus { get; set; } = true;

    /// <summary>顶栏2：显示托盘入口（已登记的托盘常驻应用 + 系统托盘）</summary>
    public bool StatusBarShowTray { get; set; } = true;

    /// <summary>顶栏主题（对应 DockThemes 下标）；任何主题都保留左右渐变透明。</summary>
    public int StatusBarTheme { get; set; }

    /// <summary>控制中心 8 个控件的布局：上排 4 个 + 更多展开 4 个，分号分隔。</summary>
    public string StatusBarControlCenterLayout { get; set; } = "";

    /// <summary>顶栏2：专注模式（只影响本程序：隐藏通知计数并显示免打扰）</summary>
    public bool StatusBarFocusMode { get; set; }

    /// <summary>顶栏2：显示计时器段</summary>
    public bool StatusBarShowTimer { get; set; }

    /// <summary>顶栏2：计时器时长（分钟，5–120）</summary>
    public int StatusBarTimerMinutes { get; set; } = 25;

    /// <summary>顶栏2 上次打开通知中心的时间（UTC ISO 8601），用于未读计数；与启动器内顶栏分开</summary>
    public string StatusBarNotificationSeenTime { get; set; } = "";

    /// <summary>上次打开通知中心的时间（UTC ISO 8601）：比它晚的通知才亮绿点；持久化，重启后仍能提示未读</summary>
    public string NotificationSeenTime { get; set; } = "";

    /// <summary>窗口背景颜色（#RRGGBB 十六进制）</summary>
    public string BackgroundColor { get; set; } = "#202020";

    /// <summary>玻璃（Acrylic 磨砂）效果，可与背景颜色叠加</summary>
    public bool GlassEffect { get; set; } = true;

    /// <summary>窗口不透明度（0.3~1.0，1 为完全不透明）</summary>
    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>平板模式：开启后所有页面隐藏底部手柄按键提示条</summary>
    public bool TabletMode { get; set; }

    /// <summary>替代 Windows 开始菜单：窗口居中停靠在任务栏上方；
    /// Win 键与任务栏开始按钮改为呼出/收起本启动器（新用户默认开启）</summary>
    public bool StartMenuMode { get; set; } = true;

    /// <summary>呼出组合键（长按 1 秒唤起启动器窗口），默认 LT+RT</summary>
    public GamepadButtons SummonCombo { get; set; } =
        GamepadButtons.LeftTrigger | GamepadButtons.RightTrigger;

    /// <summary>底栏呼出组合键（长按 1 秒亮出底栏），默认 LS+RS；与启动器呼出键分开，互不影响</summary>
    public GamepadButtons DockSummonCombo { get; set; } =
        GamepadButtons.LeftThumb | GamepadButtons.RightThumb;

    /// <summary>
    /// 已经在启动器呼出键从 LT+RT 被误改成 LS+RS 的版本上存过配置：
    /// 加载时一次性把 SummonCombo 还原成 LT+RT、把 LS+RS 挪给 DockSummonCombo。
    /// </summary>
    public bool DockSummonComboMigrated { get; set; }

    /// <summary>全屏组合键（长按 1 秒切换全屏模式），默认 Start+Select</summary>
    public GamepadButtons FullscreenCombo { get; set; } =
        GamepadButtons.Menu | GamepadButtons.View;

    /// <summary>键盘呼出键（虚拟键码，0=未设置）；默认 F7，按下即刻呼出（无需长按），仅键盘</summary>
    public int KeyboardSummonKey { get; set; } = 0x76;

    /// <summary>键盘呼出键的修饰键标志（Ctrl=1/Shift=2/Alt=4/Win=8，见 KeySendService.Mod*）</summary>
    public int KeyboardSummonMods { get; set; }

    /// <summary>天翼云备份根目录（存档位于 /{该目录}/{游戏名}/）</summary>
    public string TianyiRootFolder { get; set; } = "GameLocker";

    /// <summary>百度网盘备份根目录（存档位于 /{该目录}/{游戏名}/）</summary>
    public string BaiduRootFolder { get; set; } = "GameLocker";

    /// <summary>云端备份使用的网盘：天翼云 / 百度网盘</summary>
    public CloudProvider CloudProvider { get; set; } = CloudProvider.Tianyi;

    // ---------------- 底栏 ----------------
    // 字段在一次文件回退中丢失，这里按底栏现有读取点补回默认值。

    /// <summary>底栏总开关</summary>
    public bool DockEnabled { get; set; } = true;

    /// <summary>外观主题下标，见 <see cref="DockThemes"/></summary>
    public int DockTheme { get; set; }

    /// <summary>鼠标经过时图标放大</summary>
    public bool DockMagnify { get; set; } = true;

    /// <summary>按下图标时轻微缩小</summary>
    public bool DockPressFeedback { get; set; } = true;

    // 以下底栏行为已经固定，不再提供设置项；保留属性供旧 settings.json 反序列化并统一返回固定值。

    /// <summary>悬停时用 DWM 缩略图预览窗口（固定开启）</summary>
    public bool DockWindowPreview { get => true; set { } }

    /// <summary>无窗口时允许模拟点击托盘图标唤醒（固定关闭）</summary>
    public bool DockTrayWake { get => false; set { } }

    /// <summary>点击文件夹在底栏展开内容（固定开启）</summary>
    public bool DockFolderExpand { get => true; set { } }

    /// <summary>文件夹展开的弧形角度（固定 90）</summary>
    public int DockArcAngle { get => 90; set { } }

    /// <summary>任务栏隐藏：0 不隐藏 / 2 彻底隐藏（旧值 1 自动隐藏已废弃，加载时归零）</summary>
    public int DockTaskbarMode { get; set; }

    /// <summary>底栏所在屏幕（固定主屏）</summary>
    public int DockMonitorMode { get => 0; set { } }

    /// <summary>指定屏序号（固定 0）</summary>
    public int DockMonitorIndex { get => 0; set { } }

    /// <summary>在已运行应用图标下方显示标记（固定开启）</summary>
    public bool DockShowRunning { get => true; set { } }

    /// <summary>运行指示样式（固定 2 = 横条）</summary>
    public int DockIndicatorStyle { get => 2; set { } }

    /// <summary>运行指示大小（固定 3）</summary>
    public int DockIndicatorSize { get => 3; set { } }

    /// <summary>运行指示颜色（固定绿色）</summary>
    public string DockIndicatorColor { get => "#7BD88F"; set { } }

    /// <summary>鼠标离开后收起，留边缘触发带</summary>
    public bool DockAutoHide { get; set; }

    /// <summary>启动器收起后允许手柄操作底栏（固定开启）</summary>
    public bool DockGamepadEnabled { get => true; set { } }

    /// <summary>图标边长（36–64）。条高由它推导，不再单独保存。</summary>
    public int DockIconSize { get; set; } = 40;

    public const int DockIconMin = 36;
    public const int DockIconMax = 64;

    /// <summary>运行指示在图标下方额外占用的高度（DIP）</summary>
    public static int DockIndicatorExtra(bool showRunning, int style, int size) =>
        showRunning && style > 0 ? Math.Clamp(size, 3, 16) + 1 : 0;

    /// <summary>
    /// 条高 = ceil((图标 + 指示占位) × 1.14) + 内边距（上 6 + 下 2）。
    /// 1.14 是给悬停放大留的余量（放大倍率 1.18 时仍能完整落在条内）。
    /// 上边多留 3 DIP、下边收 1 DIP，条顶抬高 2 DIP，视觉重心不再偏下。
    /// </summary>
    public static int DockBarHeight(int iconSize, int indicatorExtra) =>
        (int)Math.Ceiling((Math.Clamp(iconSize, DockIconMin, DockIconMax) + Math.Max(0, indicatorExtra)) * 1.14) + 8;

    /// <summary>图标尺寸落在合法区间；旧配置里的条高字段已废弃，这里只校正图标。</summary>
    public void NormalizeDockGeometry() =>
        DockIconSize = Math.Clamp(DockIconSize, DockIconMin, DockIconMax);
}
