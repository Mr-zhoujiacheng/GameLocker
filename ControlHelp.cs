namespace GameLocker;

/// <summary>控制页操作对照表：一行 = 一个手柄操作及其触控/鼠标等价操作</summary>
public sealed record ControlRow(string Pad, string Touch);

/// <summary>操作对照表的一个场景分组</summary>
public sealed record ControlSection(string Title, ControlRow[] Rows);

/// <summary>本软件全部手柄 ↔ 触控/鼠标操作映射（与主页/编辑模式/设置/全局呼出实现一一对应）</summary>
public static class ControlHelp
{
    public static ControlSection[] Sections { get; } =
    [
        new("主页 / 磁贴集",
        [
            new("方向键", "点按磁贴选择"),
            new("A", "启动 · 展开组 · 切图 · 播放/暂停"),
            new("Y", "长按磁贴 = 关闭窗口"),
            new("R3", "双击磁贴 = 编辑"),
            new("L3", "长按设置磁贴 = 编辑模式"),
            new("X", "文件拖入窗口 = 添加"),
            new("LB / RB", "翻页 · 上一/下一曲 · 上一/下一张"),
            new("View", "点设置磁贴进入设置"),
            new("B", "隐藏托盘（下滑 = 收起）"),
        ]),

        new("编辑模式",
        [
            new("按住拖动", "拖到空位/磁贴 = 换位"),
            new("轻点", "标记磁贴（青色描边）"),
            new("A / ≥2", "轻点加号 = 组合成组"),
            new("Y", "拖到垃圾桶 = 删除"),
            new("B", "轻点设置磁贴 = 退出"),
        ]),

        new("设置",
        [
            new("方向键 / A", "点分类进入 · 点开关调整"),
            new("B", "返回按钮 · 下滑返回"),
        ]),

        new("关于",
        [
            new("方向键", "点条目选择"),
            new("A", "打开 GitHub 仓库 / 加入 QQ 群 / 打赏支持"),
            new("B", "返回按钮 · 下滑返回"),
        ]),

        new("全局呼出",
        [
            new("组合键", "按住 1 秒唤出窗口"),
            new("全屏组合键", "按住 1 秒切换全屏"),
            new("托盘 / 边缘", "点托盘图标 · 屏幕边缘内滑"),
        ]),
    ];
}
