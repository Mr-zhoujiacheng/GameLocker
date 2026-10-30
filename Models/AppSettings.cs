using GameLocker.Services;

namespace GameLocker.Models;

/// <summary>窗口停靠方式：左/右贴边（侧边栏），或居中悬浮（四周不贴屏幕边）</summary>
public enum DockSide
{
    Left,
    Right,
    Center
}

/// <summary>磁贴主题：彩色（强调色纯色块）/ 渐变（自身颜色斜向亮暗渐变）/
/// 玻璃（磨砂色底）/ 简约（无颜色固定深灰）/ 透明（无底色，全部透明，不可选颜色）</summary>
public enum TileTheme
{
    Colorful,
    Win10,
    Gradient,
    Glass,
    Transparent
}

/// <summary>磁贴圆角样式（与主题独立设置）</summary>
public enum TileCorner
{
    Rounded,
    Square
}

/// <summary>应用设置（JSON 持久化，见 SettingsService）</summary>
public class AppSettings
{
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

    /// <summary>磁贴主题（彩色 / 简约 / 渐变 / 玻璃 / 透明磁贴）</summary>
    public TileTheme TileTheme { get; set; } = TileTheme.Colorful;

    /// <summary>磁贴圆角样式：未设置时按主题默认（彩色=圆角，简约/渐变/玻璃/透明=方形）</summary>
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

    /// <summary>抽屉透明：开启后去掉玻璃渐变底与投影，抽屉与窗口背景完全一致，仅靠加重的描边区分范围</summary>
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

    /// <summary>呼出组合键（长按 1 秒唤起窗口），默认 LT+RT</summary>
    public GamepadButtons SummonCombo { get; set; } =
        GamepadButtons.LeftTrigger | GamepadButtons.RightTrigger;

    /// <summary>全屏组合键（长按 1 秒切换全屏模式），默认菜单键</summary>
    public GamepadButtons FullscreenCombo { get; set; } = GamepadButtons.Menu;

    /// <summary>键盘呼出键（虚拟键码，0=未设置）；默认 F7，按下即刻呼出（无需长按），仅键盘</summary>
    public int KeyboardSummonKey { get; set; } = 0x76;

    /// <summary>键盘呼出键的修饰键标志（Ctrl=1/Shift=2/Alt=4/Win=8，见 KeySendService.Mod*）</summary>
    public int KeyboardSummonMods { get; set; }

    /// <summary>云端备份（WebDAV）：服务器地址，默认坚果云；也可改为群晖 / Nextcloud 等自建网盘地址</summary>
    public string WebDavUrl { get; set; } = "https://dav.jianguoyun.com/dav/";

    /// <summary>云端备份（WebDAV）：登录账号（坚果云为注册邮箱）</summary>
    public string WebDavUser { get; set; } = "";

    /// <summary>云端备份（WebDAV）：云端根目录名（存档位于 /{该目录}/{游戏名}/）</summary>
    public string WebDavRootFolder { get; set; } = "GameLocker";
}
