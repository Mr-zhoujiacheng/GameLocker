namespace GameLocker;

/// <summary>控制页操作对照表：一行 = 手柄操作 → 触屏/鼠标操作 → 实际操作效果。
/// 无等价操作的那一列用 "—" 占位。</summary>
public sealed record ControlRow(string Pad, string Touch, string Effect);

/// <summary>操作对照表的一个场景分组</summary>
public sealed record ControlSection(string Title, ControlRow[] Rows);

/// <summary>本软件全部手柄 ↔ 触控/鼠标操作映射（与主页/编辑模式/设置/全局呼出实现一一对应）</summary>
public static class ControlHelp
{
    public static ControlSection[] Sections { get; } =
    [
        new("主页 / 磁贴集",
        [
            new("方向键", "点按磁贴", "选择磁贴"),
            new("A", "点按磁贴", "启动 · 展开组 · 切图 · 播放/暂停"),
            new("Y", "长按磁贴", "关闭窗口"),
            new("R3", "双击磁贴", "进入编辑"),
            new("L3", "长按设置磁贴", "进入编辑模式"),
            new("X", "拖入文件", "添加新条目"),
            new("LB / RB", "—", "翻页 · 切歌 · 切图"),
            new("View", "点设置磁贴", "进入设置"),
            new("B", "向边缘滑动", "收起窗口到托盘"),
        ]),

        new("编辑模式",
        [
            new("按住 A + 方向键", "拖动磁贴", "换位"),
            new("A", "轻点磁贴", "标记磁贴（青色描边）"),
            new("A（已选 ≥2）", "轻点加号", "组合成组"),
            new("Y", "拖到垃圾桶", "删除磁贴"),
            new("B", "轻点设置磁贴", "退出编辑模式"),
        ]),

        new("设置",
        [
            new("方向键 / A", "点分类 · 点开关", "进入分类 · 调整开关"),
            new("B", "返回按钮 / 向边缘滑", "返回上一级"),
        ]),

        new("关于",
        [
            new("方向键", "点击条目", "选择条目"),
            new("A", "点击条目", "打开 GitHub 仓库 / 加入 QQ 群 / 打赏支持"),
            new("B", "返回按钮 / 向边缘滑", "返回上一级"),
        ]),

        new("全局呼出",
        [
            new("组合键", "—", "按住 1 秒唤出窗口"),
            new("全屏组合键", "—", "按住 1 秒切换全屏"),
            new("—", "托盘图标 / 边缘内滑", "唤出窗口（反向滑 = 收起）"),
        ]),
    ];
}
