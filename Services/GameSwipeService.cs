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
        if (!SettingsService.Current.SystemEnabled || !GamePresence.InGame) return false;

        var dx = endX - startX;
        var dy = endY - startY;
        var bounds = Monitors.Primary().Bounds;
        var width = bounds.Right - bounds.Left;
        if (dy == 0 || width <= 0 || bounds.Bottom <= bounds.Top) return false;

        // The bottom-right belongs to the status bar only when it is actually at the bottom.
        // Inward gestures start at the edge; outward gestures must end there.
        var atTop = StatusBarHost.IsAtTop;
        var towardBar = atTop ? dy < 0 : dy > 0;
        var barX = towardBar ? endX : startX;
        var barY = towardBar ? endY : startY;
        var barHandled = StatusBarHost.IsEnabled && barX >= bounds.Left + width * RightZoneRatio
            && barX < bounds.Right && (atTop ? AtTop(barY) : AtBottom(barY));

        var dockX = dy < 0 ? startX : endX;
        var dockY = dy < 0 ? startY : endY;
        var dockHandled = !barHandled && DockHost.IsVisible && dockX >= bounds.Left && dockX < bounds.Right && AtBottom(dockY);
        if (!barHandled && !dockHandled) return false;

        if (elapsedMs <= MaxDurationMs && Math.Abs(dy) >= SwipeDistance && Math.Abs(dx) < Math.Abs(dy))
        {
            if (barHandled) Dispatch(towardBar ? TopBarHideRequested : TopBarPeekRequested,
                towardBar ? "顶栏收起" : "顶栏滑出");
            else Dispatch(dy < 0 ? DockSummonRequested : DockHideRequested, dy < 0 ? "底栏呼出" : "底栏收起");
        }
        return true;

        bool AtTop(int y) => y >= bounds.Top && y < bounds.Bottom && y - bounds.Top <= EdgeZone;
        bool AtBottom(int y) => y >= bounds.Top && y < bounds.Bottom && bounds.Bottom - y <= EdgeZone;
    }

    private static void Dispatch(Action? handler, string log)
    {
        if (handler is null) return;
        DebugLog.Write("[手势] " + log);
        try { handler(); }
        catch (Exception ex) { DebugLog.Write("[手势] 处理失败：" + ex.Message); }
    }
}
