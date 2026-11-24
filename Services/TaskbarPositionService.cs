using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace GameLocker.Services;

/// <summary>
/// 通过 Windows 设置页把任务栏位置切到顶部/底部。
/// Windows 没有公开 API，这里用 UIA 操作“设置 → 个性化 → 任务栏 → 任务栏位置”。
/// </summary>
internal static class TaskbarPositionService
{
    public static async Task<(bool ok, string message)> SetEdgeAsync(bool top)
    {
        var target = top ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        if (TaskbarEdgeService.TryGet(out var current, out _) && current == target)
            return (true, "");

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:taskbar"));
            await Task.Delay(900);

            var ok = await Task.Run(() => SelectLocation(top));
            if (!ok) return (false, "未找到任务栏位置选项，请手动在 设置 → 个性化 → 任务栏 中切换");

            await Task.Delay(1200);
            if (TaskbarEdgeService.TryGet(out var after, out _) && after == target)
                return (true, "");
            return (false, "任务栏位置未生效，请手动切换");
        }
        catch (Exception ex)
        {
            return (false, "切换任务栏位置失败：" + ex.Message);
        }
    }

    private static bool SelectLocation(bool top)
    {
        var automation = new CUIAutomation();
        var root = automation.GetRootElement();
        IUIAutomationElement? settings = null;

        foreach (var name in new[] { "设置", "Settings" })
        {
            var cond = automation.CreatePropertyCondition(UIA_PropertyIds.UIA_NamePropertyId, name);
            settings = root.FindFirst(TreeScope.TreeScope_Descendants, cond);
            if (settings is not null) break;
        }
        if (settings is null) return false;

        IUIAutomationElement? grid = null;
        for (var i = 0; i < 15 && grid is null; i++)
        {
            var cond = automation.CreatePropertyCondition(
                UIA_PropertyIds.UIA_AutomationIdPropertyId, "LocationGridView");
            grid = settings.FindFirst(TreeScope.TreeScope_Descendants, cond);
            if (grid is null) Thread.Sleep(200);
        }
        if (grid is null) return false;

        var items = grid.FindAll(TreeScope.TreeScope_Children, automation.CreateTrueCondition());
        for (var i = 0; i < items.Length; i++)
        {
            var item = items.GetElement(i);
            var name = item.CurrentName ?? "";
            var match = top
                ? name.Contains("顶部") || name.Contains("Top", StringComparison.OrdinalIgnoreCase)
                : name.Contains("底部") || name.Contains("Bottom", StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            if (item.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId) is IUIAutomationSelectionItemPattern pattern)
            {
                pattern.Select();
                return true;
            }
        }
        return false;
    }
}
