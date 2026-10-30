using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace GameLocker.Services;

/// <summary>
/// 托盘图标唤醒：用 UI Automation 在任务栏通知区按名字找到托盘图标并模拟左键点击，
/// 让软件自己把窗口从托盘恢复出来。
/// 相比外部 ShowWindow 强显，只有软件自己恢复才会同步 Chromium/Electron 内核的可见状态
/// （强显后内部仍认为窗口是隐藏的，画面不刷新、输入也不响应）。
/// </summary>
internal static class TrayIconService
{
    /// <summary>按名称点击托盘图标（图标藏在溢出面板时先展开）。可传多个候选名
    /// （磁贴名、exe 名、窗口标题），托盘提示文字常与磁贴名对不上。返回是否点到了图标。</summary>
    public static bool Click(params string?[] names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n!.Trim())
                        .Where(n => n.Length >= 2)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
        if (list.Length == 0) return false;
        try
        {
            return ClickCore(list);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"托盘图标点击失败 {string.Join("/", list)}: {ex.Message}");
            return false;
        }
    }

    private static bool ClickCore(string[] names)
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == 0) return false;

        var automation = new CUIAutomation();
        var bar = automation.ElementFromHandle(taskbar);
        if (bar is null) return false;

        // 1) 常驻区直接能找到（图标显示在任务栏上）
        var icon = FindByName(automation, bar, names);
        if (icon is not null)
        {
            LaunchService.Diag("    [托盘] 常驻区命中图标");
            return Activate(icon);
        }

        var overflow = FindOverflowButton(automation, bar);
        if (overflow is null)
        {
            LaunchService.Diag("    [托盘] 没找到「显示隐藏的图标」按钮");
            return false;
        }

        // 2) 溢出面板若是展开状态（上次没收起/用户开着），UIA 里可能是个空壳，
        //    直接搜往往搜不到图标。先点一次收起、再点开，拿到一块完整的"新面板"。
        //    这里不用固定 sleep：面板开关有动画，固定等待会"有时点开、有时又点关"
        if (FindFlyoutElement(automation) is not null)
        {
            LaunchService.Diag("    [托盘] 溢出面板已展开，先收起再重新展开");
            Activate(overflow);
            WaitFlyout(automation, visible: false, 10);
        }
        if (!Activate(overflow))
        {
            LaunchService.Diag("    [托盘] 溢出按钮没点开");
            return false;
        }
        var panelShown = WaitFlyout(automation, visible: true, 12);

        // 3) 面板的 UIA 树是逐步挂上来的：每轮都重新取一次窗口/元素再找图标
        IUIAutomationElement? flyout = null;
        for (var i = 0; i < 14; i++)   // 最多约 2.8 秒
        {
            var found = FindIconInOverflows(automation, names, out var root);
            if (found is not null)
            {
                icon = found;
                flyout = root;
                break;
            }
            flyout ??= root;
            Thread.Sleep(200);
        }

        var where = icon is not null ? "溢出面板" : "";
        if (icon is null)
            icon = FindByName(automation, bar, names);   // 部分系统的溢出图标挂在任务栏子树下
        if (icon is not null && where.Length == 0) where = "任务栏子树";
        if (icon is null)
        {
            // 最后在全桌面范围找一次：个别系统版本把托盘按钮挂在别的窗口下
            var desktop = automation.ElementFromHandle(GetDesktopWindow());
            if (desktop is not null) icon = FindByName(automation, desktop, names);
            if (icon is not null) where = "全桌面";
        }
        if (icon is null)
        {
            LaunchService.Diag("    [托盘] 溢出窗口=" + OverflowWindowNames());
            if (flyout is not null)
                LaunchService.Diag("    [托盘] 面板内按钮=" + FlyoutButtonNames(automation, flyout));
        }

        LaunchService.Diag($"    [托盘] 面板={panelShown} 图标={(icon is not null)} 位置={where}");
        var ok = icon is not null && Activate(icon);
        if (panelShown || FindFlyoutElement(automation) is not null)
            CloseOverflow(automation, overflow);
        LaunchService.Diag($"    [托盘] 点击结果={ok}");
        return ok;
    }

    /// <summary>等溢出面板出现/消失（UIA 里的开关有动画，固定 sleep 不可靠）。返回最终状态是否达成</summary>
    private static bool WaitFlyout(IUIAutomation automation, bool visible, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            var exists = FindFlyoutElement(automation) is not null;
            if (exists == visible) return true;
            Thread.Sleep(150);
        }
        return visible == (FindFlyoutElement(automation) is not null);
    }

    /// <summary>在所有溢出面板窗口里按名字找图标；返回命中的元素，root 为命中的面板元素</summary>
    private static IUIAutomationElement? FindIconInOverflows(IUIAutomation automation, string[] names,
        out IUIAutomationElement? root)
    {
        root = null;
        foreach (var hwnd in FindOverflowWindows())
        {
            IUIAutomationElement? el;
            try { el = automation.ElementFromHandle(hwnd); } catch { continue; }
            if (el is null) continue;
            root ??= el;
            var icon = FindByName(automation, el, names);
            if (icon is not null)
            {
                root = el;
                return icon;
            }
        }
        return null;
    }

    /// <summary>把当前可见的溢出面板窗口类名拼起来写日志（排查"图标找不到"用）</summary>
    private static string OverflowWindowNames()
    {
        var list = new List<string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = ClassName(h);
            if (!cls.Contains("Overflow", StringComparison.OrdinalIgnoreCase)) return true;
            list.Add($"{cls}({WindowTextOf(h)})");
            return true;
        }, IntPtr.Zero);
        return list.Count == 0 ? "无" : string.Join("|", list);
    }

    /// <summary>把溢出面板里的按钮名拼起来写日志（排查"图标找不到"用）</summary>
    private static string FlyoutButtonNames(IUIAutomation automation, IUIAutomationElement flyout)
    {
        try
        {
            var all = flyout.FindAll(TreeScope.TreeScope_Subtree, automation.CreateTrueCondition());
            var names = new List<string>();
            for (var i = 0; i < all.Length && names.Count < 15; i++)
            {
                try
                {
                    var el = all.GetElement(i);
                    if (el is null) continue;
                    var name = el.CurrentName ?? "";
                    if (name.Length == 0) continue;
                    names.Add(name);
                }
                catch { }
            }
            return string.Join("|", names);
        }
        catch { return "?"; }
    }

    /// <summary>触发托盘按钮：优先用 UIA Invoke（不受启动器置顶遮挡影响、不移动鼠标），
    /// 其次 LegacyIAccessible 默认动作，最后退化为模拟鼠标点击</summary>
    private static bool Activate(IUIAutomationElement el)
    {
        try
        {
            var pattern = el.GetCurrentPattern(InvokePatternId);
            if (pattern is IUIAutomationInvokePattern invoke)
            {
                invoke.Invoke();
                return true;
            }
        }
        catch { /* 没有 Invoke 模式：换默认动作 */ }

        try
        {
            var pattern = el.GetCurrentPattern(LegacyPatternId);
            if (pattern is IUIAutomationLegacyIAccessiblePattern legacy)
            {
                legacy.DoDefaultAction();
                return true;
            }
        }
        catch { /* 也不支持默认动作：走鼠标 */ }
        return ClickElement(el);
    }

    /// <summary>点完图标后马上把溢出面板收起来：面板不会自己收起，多等一会它就在屏幕上多停一会</summary>
    private static void CloseOverflow(IUIAutomation automation, IUIAutomationElement overflow)
    {
        Thread.Sleep(120);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (FindFlyoutElement(automation) is null) return;   // 已经收起了
            Activate(overflow);                                  // 再按一次"显示隐藏的图标"即收起
            Thread.Sleep(160);
        }
    }

    /// <summary>在元素子树里找托盘按钮：不强制要求 SystemTray 类名（不同 Windows 版本
    /// 托盘按钮的类名不一致，Win11 上有的只是普通 Button），命中任一候选名且面积最大的那个</summary>
    private static IUIAutomationElement? FindByName(IUIAutomation automation, IUIAutomationElement root,
        string[] names)
    {
        IUIAutomationElementArray all;
        try { all = root.FindAll(TreeScope.TreeScope_Subtree, automation.CreateTrueCondition()); }
        catch { return null; }

        IUIAutomationElement? best = null;
        double bestArea = 0;
        for (var i = 0; i < all.Length; i++)
        {
            IUIAutomationElement el;
            try { el = all.GetElement(i); } catch { continue; }
            if (el is null) continue;

            string className;
            string name;
            int controlType;
            try
            {
                className = el.CurrentClassName ?? "";
                name = el.CurrentName ?? "";
                controlType = el.CurrentControlType;
            }
            catch { continue; }
            if (!names.Any(n => NameMatches(name, n))) continue;

            // 只认真正的托盘按钮：类名 SystemTray* 或按钮控件
            var isButton = className.StartsWith("SystemTray", StringComparison.OrdinalIgnoreCase)
                           || controlType == ButtonControlTypeId;
            if (!isButton) continue;

            var r = el.CurrentBoundingRectangle;
            var area = Math.Max(0, r.right - r.left) * (double)Math.Max(0, r.bottom - r.top);
            if (area <= 0 || area <= bestArea) continue;
            bestArea = area;
            best = el;
        }
        return best;
    }

    /// <summary>任务栏上的"显示隐藏的图标"按钮</summary>
    private static IUIAutomationElement? FindOverflowButton(IUIAutomation automation, IUIAutomationElement bar)
    {
        IUIAutomationElementArray all;
        try { all = bar.FindAll(TreeScope.TreeScope_Subtree, automation.CreateTrueCondition()); }
        catch { return null; }

        for (var i = 0; i < all.Length; i++)
        {
            IUIAutomationElement el;
            try { el = all.GetElement(i); } catch { continue; }
            if (el is null) continue;

            string className;
            string name;
            string automationId;
            try
            {
                className = el.CurrentClassName ?? "";
                name = el.CurrentName ?? "";
                automationId = el.CurrentAutomationId ?? "";
            }
            catch { continue; }
            if (!className.StartsWith("SystemTray", StringComparison.OrdinalIgnoreCase)) continue;
            if (automationId.Contains("Overflow", StringComparison.OrdinalIgnoreCase)
                || name.Contains("隐藏", StringComparison.OrdinalIgnoreCase)
                || name.Contains("hidden icon", StringComparison.OrdinalIgnoreCase)
                || name.Contains("overflow", StringComparison.OrdinalIgnoreCase))
                return el;
        }
        return null;
    }

    /// <summary>当前所有可见的溢出面板窗口（Win11 常常不止一个：外层 XAML 岛 + 内层面板）</summary>
    private static List<IntPtr> FindOverflowWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (!ClassName(h).Contains("Overflow", StringComparison.OrdinalIgnoreCase)) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>任务栏溢出面板（Win11 的 XAML 岛窗口）的 UIA 元素</summary>
    private static IUIAutomationElement? FindFlyoutElement(IUIAutomation automation)
    {
        var all = FindOverflowWindows();
        if (all.Count == 0) return null;
        try { return automation.ElementFromHandle(all[0]); } catch { return null; }
    }

    /// <summary>点击元素中心（先确认该点没被启动器自己的窗口盖住）</summary>
    private static bool ClickElement(IUIAutomationElement el)
    {
        tagRECT r;
        try { r = el.CurrentBoundingRectangle; } catch { return false; }
        var w = r.right - r.left;
        var h = r.bottom - r.top;
        if (w <= 0 || h <= 0) return false;

        var x = r.left + w / 2;
        var y = r.top + h / 2;

        // 启动器置顶时会盖住任务栏/溢出面板：这一点若落回本进程就放弃点击，
        // 交给调用方退化为强显，避免误点自己的界面
        GetWindowThreadProcessId(WindowFromPoint(new POINT(x, y)), out uint pid);
        if ((int)pid == Environment.ProcessId) return false;

        GetCursorPos(out var old);
        SetCursorPos(x, y);
        Thread.Sleep(20);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        Thread.Sleep(20);
        SetCursorPos(old.x, old.y);
        return true;
    }

    private static bool NameMatches(string text, string target)
    {
        static string Norm(string s) => s.Replace(" ", "").Trim().ToLowerInvariant();
        var a = Norm(text);
        var b = Norm(target);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        return a.Length >= 2 && b.Length >= 2 && (a.Contains(b) || b.Contains(a));
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) <= 0 ? "" : sb.ToString();
    }

    private static string WindowTextOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetWindowTextW(hwnd, sb, sb.Capacity) <= 0 ? "" : sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
        public POINT(int x, int y) { this.x = x; this.y = y; }
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    /// <summary>UIA_InvokePatternId</summary>
    private const int InvokePatternId = 10000;
    /// <summary>UIA_LegacyIAccessiblePatternId</summary>
    private const int LegacyPatternId = 10020;
    /// <summary>UIA_ButtonControlTypeId</summary>
    private const int ButtonControlTypeId = 50000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? name);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
}
