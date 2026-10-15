using GameLocker.Services;

namespace GameLocker.Models;

/// <summary>侧边栏停靠方向</summary>
public enum DockSide
{
    Left,
    Right
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

    /// <summary>平板边缘手势呼出：从任务栏对侧屏幕边缘向中心滑动唤起窗口（左/右停靠对应左/右边缘，全屏对应上边缘）</summary>
    public bool EdgeGestureSummon { get; set; } = true;

    /// <summary>窗口停靠侧（左/右贴边，侧边栏样式）</summary>
    public DockSide SidebarSide { get; set; } = DockSide.Left;

    /// <summary>侧边栏宽度占屏幕比例（0.2~0.5，最小为当前默认观感，最大为屏幕一半）</summary>
    public double SidebarWidthPercent { get; set; } = 0.38;

    /// <summary>磁贴间距（像素，0~30）</summary>
    public double TileSpacing { get; set; } = 4;

    /// <summary>磁贴立体效果（渐变受光 + 投影）</summary>
    public bool Tile3D { get; set; }

    /// <summary>磁贴透明度（0.3~1.0，1 为完全不透明）</summary>
    public double TileOpacity { get; set; } = 1.0;

    /// <summary>磁贴显示标题（名称）</summary>
    public bool ShowTileName { get; set; } = true;

    /// <summary>磁贴组展开/收起动画（成员磁贴交错淡入浮现 / 淡出收起）</summary>
    public bool GroupExpandAnimation { get; set; } = true;

    /// <summary>随机磁贴颜色：未指定颜色的磁贴随机配色；关闭后未指定颜色的磁贴显示为无颜色</summary>
    public bool RandomTileColor { get; set; } = true;

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

    /// <summary>窗口背景颜色（#RRGGBB 十六进制）</summary>
    public string BackgroundColor { get; set; } = "#202020";

    /// <summary>玻璃（Acrylic 磨砂）效果，可与背景颜色叠加</summary>
    public bool GlassEffect { get; set; } = true;

    /// <summary>窗口不透明度（0.3~1.0，1 为完全不透明）</summary>
    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>平板模式：开启后所有页面隐藏底部手柄按键提示条</summary>
    public bool TabletMode { get; set; }

    /// <summary>呼出组合键（长按 1 秒唤起窗口），默认 LT+RT</summary>
    public GamepadButtons SummonCombo { get; set; } =
        GamepadButtons.LeftTrigger | GamepadButtons.RightTrigger;

    /// <summary>全屏组合键（长按 1 秒切换全屏模式），默认菜单键</summary>
    public GamepadButtons FullscreenCombo { get; set; } = GamepadButtons.Menu;
}
