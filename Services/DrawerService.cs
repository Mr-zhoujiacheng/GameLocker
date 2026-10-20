using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>
/// 磁贴抽屉（WidgetKind.Drawer）的领域逻辑：识别、插入/移入、命名、唯一性守卫与编辑回写。
/// 主页渲染与交互（MainPage）和小部件页/编辑页（WidgetPage / AddItemPage）都收敛到这里，
/// 避免三处各写一份「叫什么名字 / 往哪里加」的判断。
/// 抽屉固定在主页下方独立区域，高度随内容增长，因此不再有容量上限判断。
/// </summary>
public static class DrawerService
{
    /// <summary>是否为磁贴抽屉组件</summary>
    public static bool IsDrawer(LaunchItem it)
        => it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.Drawer;

    /// <summary>主页上第一个抽屉组件（MVP 只允许一个）</summary>
    public static LaunchItem? FindDrawer(List<LaunchItem> items) => items.FirstOrDefault(IsDrawer);

    /// <summary>新建一个抽屉宿主磁贴（自带两个空抽屉，保证标签条可用）</summary>
    public static LaunchItem NewHost(int pageIndex) => new()
    {
        Name = "磁贴抽屉",
        Target = "widget:drawer",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Drawer,
        PageIndex = pageIndex,
        Size = TileSize.Wide,
        Drawers =
        [
            new DrawerPage { Name = "抽屉1", Items = [] },
            new DrawerPage { Name = "抽屉2", Items = [] }
        ]
    };

    /// <summary>确保存在抽屉宿主（首次开启抽屉开关时）：不存在则追加一个。返回是否新建（调用方负责落盘）。</summary>
    public static bool EnsureHost(IList<LaunchItem> items, int pageIndex)
    {
        if (items.Any(IsDrawer)) return false;
        items.Add(NewHost(pageIndex));
        return true;
    }

    /// <summary>抽屉默认名：抽屉1、抽屉2…（取第一个未被占用的序号）</summary>
    public static string NextName(IEnumerable<DrawerPage> drawers)
    {
        var used = new HashSet<string>(drawers.Select(d => d.Name));
        for (var i = 1; ; i++)
        {
            var name = $"抽屉{i}";
            if (!used.Contains(name)) return name;
        }
    }

    /// <summary>顶层与所有抽屉内查找（唯一性守卫：如「音乐控制器只允许一个」）</summary>
    public static bool Anywhere(List<LaunchItem> items, Func<LaunchItem, bool> pred)
    {
        if (items.Any(pred)) return true;
        foreach (var d in items)
            if (IsDrawer(d) && d.Drawers is { } pages && pages.Any(p => p.Items.Any(pred)))
                return true;
        return false;
    }

    /// <summary>
    /// 顶层与所有抽屉内查找，返回命中的磁贴实例（找不到为 null）。
    /// 抽屉内层磁贴不在顶层列表里，编辑回写必须拿到配置对象图里的同一个实例，否则改不动。
    /// </summary>
    public static LaunchItem? FindAnywhere(List<LaunchItem> items, Func<LaunchItem, bool> pred)
    {
        if (items.FirstOrDefault(pred) is { } top) return top;
        foreach (var d in items)
            if (IsDrawer(d) && d.Drawers is { } pages)
                foreach (var p in pages)
                    if (p.Items.FirstOrDefault(pred) is { } inner) return inner;
        return null;
    }

    /// <summary>把磁贴追加进指定抽屉</summary>
    public static void AddTo(LaunchItem owner, int tab, LaunchItem item)
    {
        if (owner.Drawers is null || owner.Drawers.Count == 0) return;
        owner.Drawers[Math.Clamp(tab, 0, owner.Drawers.Count - 1)].Items.Add(item);
    }

    /// <summary>
    /// 放置一个新建磁贴：req=(抽屉组件 Target, 抽屉下标) 为空时直接加在顶层；非空时放进该抽屉。
    /// 找不到组件返回 false 并写 error（不修改 items）。落盘与回主页由调用方负责。
    /// </summary>
    public static bool TryInsert(List<LaunchItem> items, (string Target, int Tab)? req, LaunchItem item, out string? error)
    {
        error = null;
        if (req is null)
        {
            items.Add(item);
            return true;
        }

        var owner = items.FirstOrDefault(x => x.Target == req.Value.Target && IsDrawer(x));
        if (owner?.Drawers is null || owner.Drawers.Count == 0)
        {
            error = "抽屉组件已不存在";
            return false;
        }
        var tab = Math.Clamp(req.Value.Tab, 0, owner.Drawers.Count - 1);
        owner.Drawers[tab].Items.Add(item);
        return true;
    }

    /// <summary>编辑页保存：把编辑中的抽屉列表（含名称）写回组件。列表为空时保持原样（至少保留一个，由编辑页保证）。</summary>
    public static void ApplyEdits(LaunchItem drawerItem, List<DrawerPage> pending)
    {
        if (pending.Count == 0) return;
        drawerItem.Drawers = [.. pending.Select(d => d.Clone())];
    }
}
