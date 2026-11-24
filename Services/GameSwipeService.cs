namespace GameLocker.Services;

/// <summary>
/// 游戏内边缘滑动手势（复用 <see cref="EdgeGestureService"/> 已验证的原始输入触摸/鼠标通道）：
/// ① 顶栏在顶部：右上边缘下滑 = 滑出顶栏 5 秒，再上滑 = 立刻收起；
/// ② 顶栏在底部：右下边缘上滑 = 滑出顶栏，下滑 = 收起；
/// ③ 底栏：屏幕底部边缘上滑 = 呼出底栏并保持，下滑 = 彻底收起。
/// 只在"在游戏里"（<see cref="GamePresence"/>）时生效；右上角/底部那一段由本服务接管，
/// 启动器的边缘呼出不会再跟着触发。
/// </summary>
internal static class GameSwipeService
{
    /// <summary>边缘感应带（物理像素，与 EdgeGestureService 的 EdgeZone 保持一致）。</summary>
    private const int EdgeZone = 70;

    /// <summary>判定为"滑动"的最小位移（物理像素）。</summary>
    private const int SwipeDistance = 70;

    /// <summary>单次手势的最长耗时（毫秒），太慢的拖动不算。</summary>
    private const int MaxDurationMs = 900;

    /// <summary>右侧手势区占屏宽的比例（右上/右下角）。</summary>
    private const double RightZoneRatio = 0.6;

    /// <summary>顶栏滑出（5 秒后自动收起）。</summary>
    public static event Action? TopBarPeekRequested;

    /// <summary>顶栏反向滑动：立刻收起。</summary>
    public static event Action? TopBarHideRequested;

    /// <summary>底栏呼出（开始菜单模式时由订阅方顺带把启动器也叫出来）。</summary>
    public static event Action? DockSummonRequested;

    /// <summary>底栏彻底收起。</summary>
    public static event Action? DockHideRequested;

    /// <summary>
    /// 由 <see cref="EdgeGestureService"/> 在手指/鼠标抬起时调用。
    /// 返回 true 表示这次手势归顶栏/底栏处理（调用方不再执行启动器的呼出/收起/返回）。
    /// </summary>
    public static bool TryHandleGesture(int startX, int startY, int endX, int endY, long elapsedMs)
    {
        if (!GamePresence.InGame) return false;

        // 呼出看起点在不在边缘带，收起看终点在不在边缘带（上滑/下滑的收尾都会贴到边上）
        var startBar = BarZone(startX, startY);
        var endBar = BarZone(endX, endY);
        var startDock = DockZone(startY);
        var endDock = DockZone(endY);

        var barHandled = startBar || endBar;
        var dockHandled = !barHandled && (startDock || endDock);
        if (!barHandled && !dockHandled) return false;

        var dx = endX - startX;
        var dy = endY - startY;
        var vertical = Math.Abs(dy) >= SwipeDistance && Math.Abs(dx) < Math.Abs(dy);
        DebugLog.Write($"[手势] {startX},{startY} -> {endX},{endY} dx={dx} dy={dy} ms={elapsedMs} " +
                       $"zone={(barHandled ? "bar" : "dock")} startEdge={(startBar || startDock)} endEdge={(endBar || endDock)} " +
                       $"vertical={vertical}");

        if (elapsedMs <= MaxDurationMs && vertical)
        {
            if (barHandled)
            {
                // 顶栏在顶部：从顶边往下滑=滑出，往上滑回顶边=收起；在底部时方向相反
                var barAtTop = StatusBarHost.IsAtTop;
                var hide = endBar && !startBar;                      // 手指收在边缘带上 = 收起方向
                var peek = !hide && (barAtTop ? dy > 0 : dy < 0);    // 从边缘往里滑 = 滑出
                Dispatch(peek ? TopBarPeekRequested : TopBarHideRequested,
                    $"顶栏{(peek ? "滑出" : "收起")} atTop={barAtTop}");
            }
            else if (dy < 0)
            {
                Dispatch(DockSummonRequested, "底栏呼出");
            }
            else
            {
                Dispatch(DockHideRequested, "底栏收起");
            }
        }

        // 即使位移不够也吃掉这次手势，避免启动器把它当成呼出/返回
        return true;
    }

    /// <summary>右上/右下角手势区：屏幕顶部或底部的边缘带，且靠右。</summary>
    private static bool BarZone(int x, int y)
    {
        if (!SettingsService.Current.StatusBarEnabled || !StatusBarHost.IsEnabled) return false;
        var bounds = Monitors.Primary().Bounds;
        var width = bounds.Right - bounds.Left;
        if (width <= 0) return false;
        if (x < bounds.Left + width * RightZoneRatio) return false;
        var top = y - bounds.Top <= EdgeZone;
        var bottom = bounds.Bottom - y <= EdgeZone;
        return top || bottom;
    }

    /// <summary>屏幕底部边缘带（底栏手势区）。</summary>
    private static bool DockZone(int y)
    {
        if (!SettingsService.Current.DockEnabled || !DockHost.IsVisible) return false;
        var bounds = Monitors.Primary().Bounds;
        if (bounds.Bottom - bounds.Top <= 0) return false;
        return bounds.Bottom - y <= EdgeZone;
    }

    private static void Dispatch(Action? handler, string log)
    {
        if (handler is null) return;
        DebugLog.Write("[手势] " + log);
        try { handler(); }
        catch (Exception ex) { DebugLog.Write("[手势] 处理失败：" + ex.Message); }
    }
}
