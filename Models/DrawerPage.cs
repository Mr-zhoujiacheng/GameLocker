namespace GameLocker.Models;

/// <summary>
/// 抽屉：磁贴抽屉组件（<see cref="WidgetKind.Drawer"/>）内的一个标签页 —— 名称 + 该抽屉内的磁贴。
/// 一个组件含多个抽屉，主页上选中组件时 LB/RB 在抽屉间导航，A 进入抽屉取用里面的磁贴。
/// 抽屉内只允许一层：禁止放入磁贴组（Kind=Group）、磁贴抽屉自身、设置磁贴。
/// </summary>
public sealed class DrawerPage
{
    /// <summary>抽屉名称（默认「抽屉1 / 抽屉2…」，可在编辑页修改）</summary>
    public string Name { get; set; } = "";

    /// <summary>该抽屉内的磁贴（有序）</summary>
    public List<LaunchItem> Items { get; set; } = [];

    /// <summary>深拷贝一个抽屉（含内部磁贴），用于编辑/迁移时与源数据隔离</summary>
    public DrawerPage Clone() => new()
    {
        Name = Name,
        Items = [.. Items.Select(i => i.Clone())]
    };
}
