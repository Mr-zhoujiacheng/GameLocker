namespace GameLocker.Models;

/// <summary>
/// 底栏外观主题：只改"观感"（底板、边缘高光、图标底盘、明暗），不改布局与尺寸。
/// 颜色统一用 #AARRGGBB，方便同一套渲染直接套到 WinUI 画刷上。
/// 不再有整圈描边——边缘靠 <paramref name="Highlight"/> 的顶部渐隐高光收口。
/// </summary>
/// <param name="Name">设置页里显示的名字</param>
/// <param name="Light">是否用浅色（影响图标底盘与亚克力底色的明暗）</param>
/// <param name="Bar">底板颜色；<paramref name="Bar2"/> 非空时作为渐变终点</param>
/// <param name="Bar2">渐变终点（null 表示纯色）</param>
/// <param name="Highlight">顶部高光色（带 alpha，向下渐隐）</param>
/// <param name="IconPlate">图标底下的圆角方块颜色</param>
public sealed record DockTheme(string Name, bool Light, string Bar, string? Bar2, string Highlight, string IconPlate);

/// <summary>底栏预设主题表。索引就是 <see cref="AppSettings.DockTheme"/> 保存的值。</summary>
public static class DockThemes
{
    public static readonly DockTheme[] All =
    [
        // 0：当前默认，深色磨砂玻璃
        new("深色玻璃", false, "#B3161619", null, "#1CFFFFFF", "#14FFFFFF"),
        // 1：浅色磨砂玻璃（亚克力跟着切成浅色）
        new("浅色玻璃", true, "#D9F3F3F7", null, "#2BFFFFFF", "#12000000"),
        // 2/3：不透明纯色，适合不想要透明干扰的场景
        new("纯色·炭黑", false, "#F2161619", null, "#14FFFFFF", "#14FFFFFF"),
        new("纯色·藏蓝", false, "#F21B2438", null, "#1AFFFFFF", "#16FFFFFF"),
        // 4/5：斜向渐变
        new("渐变·暮紫", false, "#E63A2B5C", "#E6161619", "#26FFFFFF", "#18FFFFFF"),
        new("渐变·海蓝", false, "#E6162B3A", "#E60B1622", "#26FFFFFF", "#18FFFFFF"),
        // 6：只留一层极淡的底，主要靠图标本身
        new("高透·极简", false, "#2E161619", null, "#38FFFFFF", "#10FFFFFF"),
    ];

    public static DockTheme Get(int index) => All[Math.Clamp(index, 0, All.Length - 1)];

    public static string[] Names => All.Select(t => t.Name).ToArray();
}
