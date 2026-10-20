using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>
/// 磁贴自适应排布的打包规则：主页 LayoutTiles 与抽屉内容排布共用同一套，
/// 避免两处规则各写一份而漂移。
/// </summary>
public static class Packing
{
    /// <summary>磁贴占用的格数（小=1×1 / 长=2×1 / 大=2×2）</summary>
    public static (int Cols, int Rows) CellsOf(TileSize size) => size switch
    {
        TileSize.Small => (1, 1),
        TileSize.Large => (2, 2),
        _ => (2, 1)
    };

    /// <summary>从 (c,r) 起 w×h 的区域是否尚未被占用</summary>
    public static bool Fits(HashSet<(int, int)> occ, int c, int r, int w, int h)
    {
        for (var dy = 0; dy < h; dy++)
            for (var dx = 0; dx < w; dx++)
                if (occ.Contains((c + dx, r + dy))) return false;
        return true;
    }

    /// <summary>贪心放置：在 cols×rows 的格子里为 w×h 找第一个空位；找到则占用并返回 true</summary>
    public static bool Place(HashSet<(int, int)> occ, int cols, int rows, int w, int h)
    {
        if (w > cols || h > rows) return false;
        for (var r = 0; r + h <= rows; r++)
            for (var c = 0; c + w <= cols; c++)
                if (Fits(occ, c, r, w, h))
                {
                    for (var dy = 0; dy < h; dy++)
                        for (var dx = 0; dx < w; dx++)
                            occ.Add((c + dx, r + dy));
                    return true;
                }
        return false;
    }
}
