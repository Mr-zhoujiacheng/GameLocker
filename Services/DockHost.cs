using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>
/// 底栏生命周期与占位协调。所有调用必须在 UI 线程。
/// 多显示器：每块目标屏各自持有一个底栏窗口并独立占位（逐屏镜像就是"目标屏 = 全部屏"）。
/// </summary>
internal static class DockHost
{
    /// <summary>一块屏上的底栏实例。</summary>
    private sealed class Bar
    {
        public required DockWindow Window { get; init; }
        public IntPtr Monitor { get; init; }
    }

    private static readonly List<Bar> Bars = [];

    public static bool IsVisible => SettingsService.Current.SystemEnabled && Bars.Count > 0;

    public static async Task InitializeAsync()
    {
        StagingService.ClearOnStartup();
        // 手柄操作底栏（DPad/LB/RB 选择、A 打开、Y 弹出窗口卡片）只订阅一次
        DockGamepad.Initialize();
        await ApplyAsync();
    }

    /// <summary>
    /// 把一次手柄按键交给底栏。多屏时只投给主屏那条（没有主屏就用第一条），
    /// 返回 false 表示没有任何底栏接受这次按键。
    /// </summary>
    public static bool HandleGamepad(GamepadButtons buttons)
    {
        if (!IsVisible) return false;
        var primary = Monitors.Primary().Handle;
        var bar = Bars.FirstOrDefault(b => b.Monitor == primary) ?? Bars[0];
        try
        {
            return bar.Window.HandleGamepad(buttons);
        }
        catch (Exception ex)
        {
            DebugLog.Write("[底栏] 手柄处理失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>撤掉手柄选中框（启动器呼出、底栏重建等时机）。</summary>
    public static void ClearGamepadSelection()
    {
        foreach (var bar in Bars)
        {
            try { bar.Window.ClearGamepadSelection(); } catch { }
        }
    }

    /// <summary>
    /// 手柄呼出组合键：把自动隐藏收起的底栏亮出来；底栏被关掉或还没建时按当前设置补建一次。
    /// 必须在 UI 线程调用。
    /// </summary>
    public static async Task SummonAsync()
    {
        if (App.Main?.IsExiting == true || !SettingsService.Current.SystemEnabled) return;
        if (_gameMode) _revealedInGame = true;
        if (Bars.Count == 0) await ApplyAsync();
        if (!SettingsService.Current.SystemEnabled || (_gameMode && !_revealedInGame)) return;
        foreach (var bar in Bars)
        {
            try
            {
                if (_gameMode) bar.Window.AutoHide = false;
                bar.Window.SetVisible(true);   // 游戏里被整体藏起来时，手势呼出要先放出来
                bar.Window.Reveal();
            }
            catch (Exception ex) { DebugLog.Write("[底栏] 呼出失败：" + ex.Message); }
        }
    }

    /// <summary>游戏内手势下滑：把展开的底栏收回去。必须在 UI 线程调用。</summary>
    public static void CollapseAll()
    {
        foreach (var bar in Bars)
        {
            try { bar.Window.Collapse(); }
            catch (Exception ex) { DebugLog.Write("[底栏] 收起失败：" + ex.Message); }
        }
    }

    /// <summary>
    /// 游戏内手势下滑：底栏彻底隐藏（连收起后那条边线也不留），下次手势再呼出。
    /// 必须在 UI 线程调用。
    /// </summary>
    public static void HideForGame()
    {
        _revealedInGame = false;
        foreach (var bar in Bars)
        {
            try { bar.Window.SetVisible(false); }
            catch (Exception ex) { DebugLog.Write("[底栏] 隐藏失败：" + ex.Message); }
        }
    }

    private static bool _gameMode;
    private static bool _revealedInGame;

    /// <summary>当前是否处在"游戏里"（底栏默认彻底隐藏，靠手势呼出）。</summary>
    public static bool GameMode => _gameMode;

    /// <summary>
    /// 进出游戏：进游戏把底栏整个藏起来（不留边线，避免盖住游戏底部），
    /// 离开游戏再按「自动隐藏」设置恢复——开着就收起，关着就展开。必须在 UI 线程调用。
    /// </summary>
    public static void SetGameMode(bool inGame)
    {
        if (_gameMode == inGame) return;
        _gameMode = inGame;
        _revealedInGame = false;
        foreach (var bar in Bars)
        {
            try
            {
                if (inGame)
                {
                    bar.Window.SetVisible(false);
                }
                else
                {
                    bar.Window.SetVisible(true);
                    bar.Window.AutoHide = SettingsService.Current.DockAutoHide;
                }
            }
            catch (Exception ex) { DebugLog.Write("[底栏] 游戏态切换失败：" + ex.Message); }
        }
        Relayout();
    }

    /// <summary>按当前设置重建底栏（开关、尺寸、图标、目标屏幕）。</summary>
    public static async Task ApplyAsync()
    {
        if (App.Main?.IsExiting == true) return;
        var settings = SettingsService.Current;

        TaskbarControl.Apply(settings.SystemEnabled ? settings.DockTaskbarMode : 0);
        if (!settings.SystemEnabled)
        {
            _revealedInGame = false;
            CloseAll();
            return;
        }

        var targets = ResolveTargets(settings);

        // 关掉不再需要的屏
        for (var i = Bars.Count - 1; i >= 0; i--)
        {
            if (targets.Any(m => m.Handle == Bars[i].Monitor)) continue;
            RemoveBar(Bars[i]);
            Bars.RemoveAt(i);
        }

        // 为新出现的屏建窗口
        foreach (var monitor in targets)
        {
            if (App.Main?.IsExiting == true || !SettingsService.Current.SystemEnabled) return;
            var existing = Bars.FirstOrDefault(b => b.Monitor == monitor.Handle);
            if (existing is not null)
            {
                existing.Window.AutoHide = settings.DockAutoHide && !_revealedInGame;
                await existing.Window.ApplySettingsAsync();
                continue;
            }

            var window = new DockWindow();
            window.LayoutInvalidated = Relayout;
            window.CollapsedChanged = Relayout;
            window.AutoHide = settings.DockAutoHide && !_revealedInGame;

            Bars.Add(new Bar { Window = window, Monitor = monitor.Handle });
            await window.ApplySettingsAsync();
        }

        if (App.Main?.IsExiting == true || !SettingsService.Current.SystemEnabled) return;
        Relayout();
        foreach (var bar in Bars) bar.Window.SetVisible(!_gameMode || _revealedInGame);
    }

    /// <summary>按设置决定要挂在哪几块屏上。</summary>
    private static List<MonitorInfo> ResolveTargets(AppSettings settings)
    {
        var all = Monitors.All();
        DebugLog.Write($"[底栏] 显示器数={all.Count} mode={settings.DockMonitorMode}");
        if (all.Count == 0) return [];

        return settings.DockMonitorMode switch
        {
            // 1 = 跟随鼠标所在屏
            1 => [Monitors.UnderCursor()],
            // 2 = 指定屏
            2 => [all[Math.Clamp(settings.DockMonitorIndex, 0, all.Count - 1)]],
            // 3 = 逐屏镜像
            3 => all,
            // 0 = 主屏
            _ => [all.FirstOrDefault(m => m.Primary, all[0])]
        };
    }

    /// <summary>重新摆放每块屏上的底栏。底栏始终是浮层，不注册 AppBar、不占工作区。</summary>
    public static void Relayout()
    {
        foreach (var bar in Bars)
        {
            var window = bar.Window;
            var height = window.PreferredHeight;
            var hwnd = window.Hwnd;
            if (hwnd == IntPtr.Zero) continue;

            var rect = AppBarHost.EdgeRect(hwnd, AppBarHost.EdgeBottom, height, bar.Monitor);

            var width = window.PreferredWidth;
            // 格子加多了超出屏幕时先贴边，至少不会跑到屏幕外；正常数量下就是按图标数撑开。
            var available = Math.Max(1, rect.Right - rect.Left);
            if (width > available) width = available;
            var x = rect.Left + (rect.Right - rect.Left - width) / 2;
            var scale = window.Scale;
            var topbarAtBottom = TopTaskbarMode.IsActive && TopTaskbarMode.Edge == TaskbarEdge.Bottom;
            // 工作区底边 + 任务栏厚度 = 显示器物理底边
            var monitorBottom = rect.Bottom + (int)Math.Round(TopTaskbarMode.HeightDip * scale);
            var y = window.IsCollapsed
                ? (topbarAtBottom
                    ? monitorBottom - (int)Math.Round(DockWindow.RevealDip * scale)
                    : rect.Bottom - (int)Math.Round(DockWindow.RevealDip * scale))
                : rect.Bottom - height - (int)Math.Round(8 * scale);
            DebugLog.Write($"[底栏] layout rectBottom={rect.Bottom} scale={scale} collapsed={window.IsCollapsed} monitorBottom={monitorBottom} y={y} h={height}");

            window.MoveToAnimated(x, y, width, height);
        }
    }

    /// <summary>下载模式等全屏遮罩期间挂起：隐藏窗口但不销毁。</summary>
    public static void Suspend(bool suspended)
    {
        foreach (var bar in Bars) bar.Window.SetVisible(!suspended && (!_gameMode || _revealedInGame));
        if (!suspended) Relayout();
    }

    public static void Shutdown()
    {
        TaskbarControl.Shutdown();
        CloseAll();
    }

    private static void CloseAll()
    {
        foreach (var bar in Bars) RemoveBar(bar);
        Bars.Clear();
    }

    private static void RemoveBar(Bar bar)
    {
        try
        {
            bar.Window.SetVisible(false);
            // 必须真正 Close：只隐藏的话窗口对象仍然存活，WinUI 会认为还有窗口而让进程退不掉
            bar.Window.Close();
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 关闭底栏窗口失败：" + ex.Message); }
    }
}
