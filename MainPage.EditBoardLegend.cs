using GameLocker.Models;
using GameLocker.Services;

namespace GameLocker;

public sealed partial class MainPage
{
    /// <summary>一行操作提示：按键标识 + 一句话说明。</summary>
    private readonly record struct KeyLegend(string Key, string Text);

    /// <summary>按当前焦点产出"编辑态 / 主页态"两组键位说明。黑板移除后这里是唯一的数据源，
    /// 顶部提示条（MainPage.HomeHints.cs）直接消费这两组列表。</summary>
    private (IReadOnlyList<KeyLegend> Edit, IReadOnlyList<KeyLegend> Home) GetBoardLegend()
    {
        var edit = new List<KeyLegend>();
        var home = new List<KeyLegend>();

        if (_inDrawer || (!_drawerExpanded && _drawerClickSelected >= 0))
        {
            var cards = _drawerOwner?.Drawers;
            if (!_drawerExpanded && _drawerIndex >= (cards?.Count ?? 0))
            {
                edit.Add(new("A", "新建抽屉"));
                edit.Add(new("B", "回主页"));
                home.Add(new("A", "新建抽屉"));
                home.Add(new("B", "收起"));
                return (edit, home);
            }
            if (_inDrawer && FocusedDrawerIsAddTile())
            {
                edit.Add(new("A", "添加磁贴"));
                edit.Add(new("B", "回卡牌"));
                home.Add(new("A", "添加磁贴"));
                home.Add(new("B", "收起"));
                return (edit, home);
            }
            if (_inDrawer && FocusedDrawerItem() is { } inner)
            {
                edit.Add(new("A", _marked.Count > 0 ? "移入抽屉" : "无"));
                edit.Add(new("B", "回卡牌"));
                edit.Add(new("Y", "删除磁贴"));
                edit.Add(new("R3", "编辑磁贴"));
                AddWidgetHomeKeys(home, inner);
                AddShoulderKeys(home, inner, quickFocus: true, inDrawer: true);
                home.Add(new("B", "回卡牌"));
                return (edit, home);
            }
            if (cards is { Count: > 0 })
            {
                edit.Add(new("A", _marked.Count > 0 ? "移入抽屉" : "展开"));
                edit.Add(new("B", _inDrawer ? "回卡牌" : "回主页"));
                edit.Add(new("Y", cards.Count == 1 ? "删除抽屉" : "删除卡"));
                edit.Add(new("R3", "改名"));
                home.Add(new("A", "展开抽屉"));
                home.Add(new("B", "收起"));
                return (edit, home);
            }
        }

        if (_addTile is not null && _selected == _display.Count)
        {
            edit.Add(new("A", "添加磁贴"));
            edit.Add(new("B", "退出编辑"));
            home.Add(new("A", "添加磁贴"));
            home.Add(new("B", "收起"));
            return (edit, home);
        }
        if (_selected < 0 || _selected >= _display.Count)
        {
            edit.Add(new("B", "退出编辑"));
            home.Add(new("B", "收起"));
            return (edit, home);
        }

        var (item, group) = _display[_selected];
        var isGroup = item.Kind == LaunchKind.Group;
        var groupOpen = ReferenceEquals(item, _expanded);
        var fixedTile = TileStyle.IsFixedTile(item);

        // 编辑态：A 抓取/合并，X 标记，Y 删除，R3 进单项编辑，B 退出。
        if (group is not null) edit.Add(new("A", "无"));
        else if (_marked.Count >= 2) edit.Add(new("A", "合并成组"));
        else if (isGroup) edit.Add(new("A", "无"));
        else edit.Add(new("A", _grabbed is null ? "抓取" : "放下"));
        if (group is null && !isGroup && !fixedTile && !DrawerService.IsDrawer(item))
            edit.Add(new("X", _marked.Contains(item) ? "取消标记" : "标记"));
        if (item is not { Kind: LaunchKind.Widget, WidgetKind: WidgetKind.Power } &&
            item is not { Kind: LaunchKind.Widget, WidgetKind: WidgetKind.Settings, SubPageSettings: false })
            edit.Add(new("Y", group is not null ? "移出组" : isGroup ? (groupOpen ? "拆组" : "删除整组") : "删除磁贴"));
        if (!fixedTile) edit.Add(new("R3", "编辑磁贴"));
        edit.Add(new("B", "退出编辑"));

        // 主页态：A 启动/展开，Y 关闭或次要动作，L3 进编辑，B 收起。
        AddHomeKeys(home, item, group, isGroup, groupOpen);
        home.Add(new("L3", "进入编辑"));
        home.Add(new("B", "收起"));
        return (edit, home);
    }

    private void AddHomeKeys(List<KeyLegend> home, LaunchItem item, LaunchItem? group, bool isGroup, bool groupOpen)
    {
        if (group is not null)
        {
            home.Add(new("A", "启动"));
            return;
        }
        if (isGroup)
        {
            home.Add(new("A", groupOpen ? "收起" : "展开"));
            return;
        }
        AddWidgetHomeKeys(home, item);
        AddShoulderKeys(home, item, quickFocus: !_inDrawer, inDrawer: _inDrawer);
    }

    // 只有真正用肩键的组件才补 LB/RB：音乐切歌、图片翻页、系统音量亮度、性能调值。
    private static void AddShoulderKeys(List<KeyLegend> home, LaunchItem item, bool quickFocus, bool inDrawer)
    {
        if (!quickFocus && item.Kind != LaunchKind.Widget) return;
        var label = ShoulderHint(item, quickFocus, inDrawer);
        if (!string.IsNullOrEmpty(label)) home.Add(new("LB/RB", label));
    }

    /// <summary>选中项按 LB/RB 时实际会发生什么——与手柄处理分支保持一致，只说结果不说"上一曲或下一曲"这种长句。</summary>
    private static string? ShoulderHint(LaunchItem item, bool quickFocus, bool inDrawer)
    {
        if (item.Kind != LaunchKind.Widget) return inDrawer ? null : null;
        switch (item.WidgetKind)
        {
            case WidgetKind.Music:
                return "切歌";
            case WidgetKind.Photos:
                return "翻图";
            case WidgetKind.System:
                var panel = SystemPanelService.FromOption(
                    SystemPanelService.NormalizeOption(item.SystemOptionMask, item.SystemOption));
                if (!SystemPanelService.SupportsAdjust(panel)) return null;
                return panel == SystemPanel.Brightness ? "调亮度" : "调音量";
            case WidgetKind.Performance:
                if (!quickFocus) return null;
                return TileStyle.CurrentQuickItem(item) switch
                {
                    TileStyle.PerformanceQuickItem.FrameLimit => "调帧率上限",
                    TileStyle.PerformanceQuickItem.PowerLimit => "调功耗",
                    TileStyle.PerformanceQuickItem.CpuFrequency => "调最高频率",
                    TileStyle.PerformanceQuickItem.CpuBoost => null, // 睿频是开关型，肩键不调
                    _ => item.PerformancePowerMode ? "切档位" : null
                };
            default:
                return null;
        }
    }

    private static void AddWidgetHomeKeys(List<KeyLegend> home, LaunchItem item)
    {
        if (item.Kind != LaunchKind.Widget)
        {
            home.Add(new("A", item.Kind switch
            {
                LaunchKind.App => "启动",
                LaunchKind.Url => "打开网页",
                LaunchKind.Folder => "打开文件夹",
                _ => "启动"
            }));
            if (item.Kind is LaunchKind.App or LaunchKind.Url or LaunchKind.Folder) home.Add(new("Y", "最小化"));
            return;
        }

        var (a, y) = item.WidgetKind switch
        {
            WidgetKind.Music => ("播放/暂停", "切歌"),
            WidgetKind.Photos => ("下一张", "上一张"),
            WidgetKind.Settings => ("设置", "编辑"),
            WidgetKind.Weather => ("刷新", ""),
            WidgetKind.Mapping => ("触发", ""),
            WidgetKind.Trainer => ("打开", "关闭"),
            WidgetKind.Backup => ("备份", "同步"),
            WidgetKind.GamepadMap => ("开关", "关闭"),
            WidgetKind.OptiScaler => ("菜单", "安装"),
            WidgetKind.Drawer => ("展开", ""),
            WidgetKind.RecycleBin => ("打开", "清空"),
            WidgetKind.Foreground => ("冻结", "结束"),
            WidgetKind.System => ("开关项", "提示"),
            WidgetKind.Power => ("电源菜单", ""),
            WidgetKind.Monitor => ("开关", "关闭"),
            WidgetKind.FileTransfer => ("传输", ""),
            WidgetKind.LosslessScaling => ("补帧", "唤醒"),
            WidgetKind.Keyboard => ("键盘", ""),
            WidgetKind.Clash => ("开关", "更新"),
            WidgetKind.Wallpaper => ("壁纸", "收藏"),
            WidgetKind.ScreenTranslate => ("翻译", ""),
            WidgetKind.Performance => ("应用", "性能面板"),
            WidgetKind.DownloadMode => ("设置", ""),
            _ => ("", "")
        };
        if (a.Length > 0) home.Add(new("A", a));
        if (y.Length > 0) home.Add(new("Y", y));
    }
}
