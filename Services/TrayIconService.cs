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
        if (icon is not null) return Activate(icon);

        // 2) 展开"显示隐藏的图标"，在溢出面板里找
        var overflow = FindOverflowButton(automation, bar);
        if (overflow is null || !Activate(overflow)) return false;

        IUIAutomationElement? flyout = null;
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            flyout = FindFlyoutElement(automation);
            if (flyout is null) continue;
            icon = FindByName(automation, flyout, names);
            if (icon is not null) break;
        }
        if (icon is null)
            icon = FindByName(automation, bar, names);   // 部分系统的溢出图标挂在任务栏子树下

        var ok = icon is not null && Activate(icon);
        CloseOverflow(automation, overflow);       // 点图标不会自动收起溢出面板
        return ok;
    }

    /// <summary>触发托盘按钮：优先用 UIA Invoke（不受启动器置顶遮挡影响、不移动鼠标），
    /// 不支持时退化为模拟鼠标点击</summary>
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
        catch { /* 没有 Invoke 模式：走鼠标 */ }
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

    /// <summary>在元素子树里找托盘按钮（优先 SystemTray 类，命中任一候选名且面积最大的那个）</summary>
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
            try { className = el.CurrentClassName ?? ""; name = el.CurrentName ?? ""; }
            catch { continue; }
            if (!className.StartsWith("SystemTray", StringComparison.OrdinalIgnoreCase)) continue;
            if (!names.Any(n => NameMatches(name, n))) continue;

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

    /// <summary>任务栏溢出面板（Win11 的 XAML 岛窗口）的 UIA 元素</summary>
    private static IUIAutomationElement? FindFlyoutElement(IUIAutomation automation)
    {
        IntPtr found = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = ClassName(h);
            if (!cls.Contains("Overflow", StringComparison.OrdinalIgnoreCase)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        if (found == 0) return null;
        try { return automation.ElementFromHandle(found); } catch { return null; }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
}
