namespace GameLocker;

/// <summary>基本操作对照表：一行 = 手柄操作 → 触屏/鼠标操作 → 实际操作效果。
/// 无等价操作的那一列用 "—" 占位。</summary>
public sealed record ControlRow(string Pad, string Touch, string Effect);

/// <summary>控制页的操作说明（简化版）：只保留各页面通用的基本操作，不细分到每个页面</summary>
public static class ControlHelp
{
    public static ControlRow[] Basic { get; } =
    [
        new("方向键 / 左摇杆", "点按条目", "移动光标 · 选择磁贴"),
        new("A", "点按条目", "确认 · 启动"),
        new("B", "返回按钮 / 下滑", "返回上一级 · 收起窗口"),
        new("X", "拖入文件", "添加新条目"),
        new("Y", "长按条目", "删除 · 关闭"),
        new("LB / RB", "—", "翻页 · 切歌 · 切图"),
        new("LT / RT", "—", "切换副页"),
        new("L3 / R3", "长按设置磁贴", "进入编辑模式"),
        new("View", "点设置磁贴", "打开设置"),
        new("呼出组合键", "托盘图标 / 边缘内滑", "按住 1 秒呼出窗口"),
    ];
}
