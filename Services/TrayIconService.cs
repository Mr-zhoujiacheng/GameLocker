using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>仅在通知区及已知托盘溢出面板中查找图标，让应用自行从托盘恢复。</summary>
internal static class TrayIconService
{
    public static bool Click(params string?[] names) => Click(CancellationToken.None, false, names);

    public static bool Click(bool doubleClick, params string?[] names) =>
        Click(CancellationToken.None, doubleClick, names);

    public static bool Click(CancellationToken token, bool doubleClick, params string?[] names) =>
        Click(token, doubleClick, null, null, names);

    public static bool Click(CancellationToken token, bool doubleClick, string? executable,
        IReadOnlySet<int>? pids, params string?[] names)
    {
        token.ThrowIfCancellationRequested();
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => NormalizeName(n!))
                        .Where(n => n.Length >= 2)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
        if (list.Length == 0) return false;
        try
        {
            return ClickCore(list, doubleClick, GetIconIdentities(executable, pids, token), token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            Debug.WriteLine($"托盘图标点击失败 {string.Join("/", list)}: {ex.Message}");
            return false;
        }
    }

    private static bool ClickCore(string[] names, bool doubleClick,
        List<NOTIFYICONIDENTIFIER> identities, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == 0) return false;
        var automation = new CUIAutomation();
        var bar = automation.ElementFromHandle(taskbar);
        if (bar is null) return false;

        var icon = FindByName(automation, NotificationRoots(automation, bar, taskbar, token), names,
            token, out var ambiguous);
        if (ambiguous) return false;
        icon ??= FindByIdentity(automation, NotificationRoots(automation, bar, taskbar, token), identities, token);
        if (icon is not null)
        {
            LaunchService.Diag("    [托盘] 常驻通知区命中图标");
            return Activate(icon, doubleClick, token);
        }

        var overflow = FindOverflowButton(automation, bar, token);
        if (overflow is null)
        {
            LaunchService.Diag("    [托盘] 没找到「显示隐藏的图标」按钮");
            return false;
        }
        if (FindFlyoutElement(automation, token) is not null)
        {
            if (!Activate(overflow, false, token)) return false;
            if (!WaitFlyout(automation, false, 10, token)) return false;
        }
        if (!Activate(overflow, false, token)) return false;
        var panelShown = WaitFlyout(automation, true, 12, token);
        // 面板出现不代表展开动画已经结束，等布局稳定后重新获取图标位置。
        if (panelShown) Wait(400, token);
        var ok = false;
        for (var i = 0; i < 14; i++)
        {
            token.ThrowIfCancellationRequested();
            var roots = OverflowRoots(automation, token);
            icon = FindByName(automation, roots, names, token, out ambiguous);
            if (!ambiguous && icon is null)
                icon = FindByIdentity(automation, roots, identities, token);
            if (ambiguous) break;
            if (icon is not null && Activate(icon, doubleClick, token))
            {
                ok = true;
                break;
            }
            Wait(200, token);
        }
        if (!ok && !ambiguous)
        {
            icon = FindByName(automation, NotificationRoots(automation, bar, taskbar, token), names,
                token, out ambiguous);
            ok = !ambiguous && icon is not null && Activate(icon, doubleClick, token);
        }

        token.ThrowIfCancellationRequested();
        if (panelShown || FindFlyoutElement(automation, token) is not null)
            CloseOverflow(automation, overflow, token);
        LaunchService.Diag($"    [托盘] 面板={panelShown} 点击结果={ok}");
        return ok;
    }

    private static List<IUIAutomationElement> NotificationRoots(IUIAutomation automation,
        IUIAutomationElement bar, IntPtr taskbar, CancellationToken token)
    {
        var roots = new List<IUIAutomationElement>();
        var notify = FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero)
        {
            try
            {
                var root = automation.ElementFromHandle(notify);
                if (root is not null) roots.Add(root);
            }
            catch (COMException) { }
        }
        var all = bar.FindAll(TreeScope.TreeScope_Subtree, automation.CreateTrueCondition());
        for (var i = 0; i < all.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var el = all.GetElement(i);
                var cls = el.CurrentClassName ?? "";
                var name = NormalizeName(el.CurrentName ?? "");
                if (cls.StartsWith("SystemTray", StringComparison.OrdinalIgnoreCase)
                    || cls.Equals("TrayNotifyWnd", StringComparison.OrdinalIgnoreCase)
                    || (el.CurrentControlType != ButtonControlTypeId
                        && (name.Equals("通知区域", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("Notification Area", StringComparison.OrdinalIgnoreCase))))
                    roots.Add(el);
            }
            catch (COMException) { }
        }
        return roots;
    }

    private static IUIAutomationElement? FindByName(IUIAutomation automation,
        IEnumerable<IUIAutomationElement> roots, string[] names, CancellationToken token, out bool ambiguous)
    {
        IUIAutomationElement? best = null;
        var bestRank = 0;
        double bestArea = 0;
        var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            IUIAutomationElementArray all;
            try { all = root.FindAll(TreeScope.TreeScope_Subtree, automation.CreateTrueCondition()); }
            catch (COMException) { continue; }
            for (var i = 0; i < all.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var el = all.GetElement(i);
                    if (el.CurrentIsOffscreen != 0) continue;
                    var cls = el.CurrentClassName ?? "";
                    if (el.CurrentControlType != ButtonControlTypeId
                        && !cls.StartsWith("SystemTray", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = NormalizeName(el.CurrentName ?? "");
                    var rank = names.Select(n => MatchRank(name, n)).Max();
                    if (rank == 0 || rank < bestRank) continue;
                    var r = el.CurrentBoundingRectangle;
                    var area = Math.Max(0, r.right - r.left) * (double)Math.Max(0, r.bottom - r.top);
                    if (area <= 0) continue;
                    if (rank > bestRank)
                    {
                        bestRank = rank;
                        bestArea = 0;
                        matchedNames.Clear();
                    }
                    matchedNames.Add(name);
                    if (area > bestArea)
                    {
                        best = el;
                        bestArea = area;
                    }
                }
                catch (COMException) { }
            }
        }
        ambiguous = matchedNames.Count > 1;
        if (ambiguous)
        {
            LaunchService.Diag("    [托盘] 同级最佳匹配存在不同名称，拒绝点击：" + string.Join(" | ", matchedNames));
            return null;
        }
        return best;
    }

    private static List<NOTIFYICONIDENTIFIER> GetIconIdentities(string? executable,
        IReadOnlySet<int>? pids, CancellationToken token)
    {
        var result = new List<NOTIFYICONIDENTIFIER>();
        if (executable is null || pids is null || pids.Count == 0) return result;
        var ids = new HashSet<uint>();
        using var settings = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (settings is null) return result;
        foreach (var name in settings.GetSubKeyNames())
        {
            token.ThrowIfCancellationRequested();
            using var key = settings.OpenSubKey(name);
            if (key?.GetValue("ExecutablePath") is not string registered
                || key.GetValue("UID") is not int uid) continue;
            if (registered.StartsWith('{') && registered.IndexOf('}') is var end && end > 0
                && Guid.TryParse(registered[..(end + 1)], out var folder))
            {
                if (SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out var buffer) != 0) continue;
                try { registered = Marshal.PtrToStringUni(buffer) + registered[(end + 1)..]; }
                finally { Marshal.FreeCoTaskMem(buffer); }
            }
            if (Environment.ExpandEnvironmentVariables(registered).Equals(executable, StringComparison.OrdinalIgnoreCase))
                ids.Add(unchecked((uint)uid));
        }
        EnumWindows((hwnd, _) =>
        {
            if (token.IsCancellationRequested) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid)) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var uid in ids)
                    result.Add(new NOTIFYICONIDENTIFIER
                    {
                        cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = hwnd, uID = uid
                    });
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static IUIAutomationElement? FindByIdentity(IUIAutomation automation,
        IEnumerable<IUIAutomationElement> roots, List<NOTIFYICONIDENTIFIER> identities, CancellationToken token)
    {
        var rectangles = new List<tagRECT>();
        foreach (var identity in identities)
        {
            token.ThrowIfCancellationRequested();
            var id = identity;
            if (Shell_NotifyIconGetRect(ref id, out var rect) == 0
                && rect.right > rect.left && rect.bottom > rect.top) rectangles.Add(rect);
        }
        if (rectangles.Count == 0) return null;
        IUIAutomationElement? match = null;
        var matches = new HashSet<(int, int, int, int)>();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            IUIAutomationElementArray all;
            try { all = root.FindAll(TreeScope.TreeScope_Subtree, automation.CreateTrueCondition()); }
            catch (COMException) { continue; }
            for (var i = 0; i < all.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var el = all.GetElement(i);
                    if (el.CurrentIsOffscreen != 0 || el.CurrentControlType != ButtonControlTypeId
                        || !string.Equals(el.CurrentAutomationId, "NotifyItemIcon", StringComparison.Ordinal)
                        || !(el.CurrentClassName ?? "").StartsWith("SystemTray", StringComparison.OrdinalIgnoreCase)) continue;
                    var r = el.CurrentBoundingRectangle;
                    var x = r.left + (r.right - r.left) / 2;
                    var y = r.top + (r.bottom - r.top) / 2;
                    if (r.right <= r.left || r.bottom <= r.top
                        || !rectangles.Any(s => x >= s.left && x < s.right && y >= s.top && y < s.bottom
                            && (s.left + (s.right - s.left) / 2) >= r.left
                            && (s.left + (s.right - s.left) / 2) < r.right
                            && (s.top + (s.bottom - s.top) / 2) >= r.top
                            && (s.top + (s.bottom - s.top) / 2) < r.bottom)) continue;
                    matches.Add((r.left, r.top, r.right, r.bottom));
                    match = el;
                }
                catch (COMException) { }
            }
        }
        if (matches.Count != 1) return null;
        LaunchService.Diag("    [托盘] 按进程窗口 + 系统图标 ID 核对位置命中");
        return match;
    }

    private static string NormalizeName(string text) => string.Join(" ",
        text.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int MatchRank(string actual, string candidate)
    {
        if (actual.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return 2;
        if (candidate.Length < 2 || actual.Length <= candidate.Length
            || !actual.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) return 0;
        var next = actual[candidate.Length];
        return char.IsWhiteSpace(next) || char.IsPunctuation(next) || next == '|' ? 1 : 0;
    }

    private static IUIAutomationElement? FindOverflowButton(IUIAutomation automation,
        IUIAutomationElement bar, CancellationToken token)
    {
        var all = bar.FindAll(TreeScope.TreeScope_Subtree, automation.CreateTrueCondition());
        for (var i = 0; i < all.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var el = all.GetElement(i);
                if (!(el.CurrentClassName ?? "").StartsWith("SystemTray", StringComparison.OrdinalIgnoreCase)) continue;
                var name = el.CurrentName ?? "";
                var id = el.CurrentAutomationId ?? "";
                if (id.Contains("Overflow", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("隐藏", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("hidden icon", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("overflow", StringComparison.OrdinalIgnoreCase)) return el;
            }
            catch (COMException) { }
        }
        return null;
    }

    private static List<IUIAutomationElement> OverflowRoots(IUIAutomation automation, CancellationToken token)
    {
        var roots = new List<IUIAutomationElement>();
        foreach (var hwnd in FindOverflowWindows(token))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var el = automation.ElementFromHandle(hwnd);
                if (el is not null) roots.Add(el);
            }
            catch (COMException) { }
        }
        return roots;
    }

    private static List<IntPtr> FindOverflowWindows(CancellationToken token)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            // 不从原生回调抛异常；枚举返回后再传播取消。
            if (token.IsCancellationRequested) return false;
            if (!IsWindowVisible(h) || !IsExplorerWindow(h)) return true;
            var cls = ClassName(h);
            if (cls.Equals("TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase)
                || cls.Equals("NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        token.ThrowIfCancellationRequested();
        return list;
    }

    private static IUIAutomationElement? FindFlyoutElement(IUIAutomation automation, CancellationToken token) =>
        OverflowRoots(automation, token).FirstOrDefault();

    private static bool WaitFlyout(IUIAutomation automation, bool visible, int steps, CancellationToken token)
    {
        for (var i = 0; i < steps; i++)
        {
            token.ThrowIfCancellationRequested();
            if ((FindFlyoutElement(automation, token) is not null) == visible) return true;
            Wait(150, token);
        }
        return visible == (FindFlyoutElement(automation, token) is not null);
    }

    private static void CloseOverflow(IUIAutomation automation, IUIAutomationElement overflow, CancellationToken token)
    {
        Wait(120, token);
        for (var i = 0; i < 4; i++)
        {
            token.ThrowIfCancellationRequested();
            if (FindFlyoutElement(automation, token) is null) return;
            if (!Activate(overflow, false, token)) return;
            Wait(160, token);
        }
    }

    private static bool Activate(IUIAutomationElement el, bool doubleClick, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (doubleClick) return ClickElement(el, true, token);
        try
        {
            if (el.GetCurrentPattern(InvokePatternId) is IUIAutomationInvokePattern invoke)
            {
                token.ThrowIfCancellationRequested();
                invoke.Invoke();
                token.ThrowIfCancellationRequested();
                return true;
            }
        }
        catch (COMException) { }
        token.ThrowIfCancellationRequested();
        try
        {
            if (el.GetCurrentPattern(LegacyPatternId) is IUIAutomationLegacyIAccessiblePattern legacy)
            {
                token.ThrowIfCancellationRequested();
                legacy.DoDefaultAction();
                token.ThrowIfCancellationRequested();
                return true;
            }
        }
        catch (COMException) { }
        return ClickElement(el, false, token);
    }

    private static bool ClickElement(IUIAutomationElement el, bool doubleClick, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        tagRECT r;
        try { r = el.CurrentBoundingRectangle; }
        catch (COMException) { return false; }
        if (r.right <= r.left || r.bottom <= r.top) return false;
        var x = r.left + (r.right - r.left) / 2;
        var y = r.top + (r.bottom - r.top) / 2;
        if (!SafeClickPoint(el, x, y) || !GetCursorPos(out var old)) return false;
        token.ThrowIfCancellationRequested();
        if (!SetCursorPos(x, y)) return false;
        try
        {
            Wait(20, token);
            for (var i = 0; i < (doubleClick ? 2 : 1); i++)
            {
                token.ThrowIfCancellationRequested();
                if (!GetCursorPos(out var current) || current.x != x || current.y != y
                    || !SafeClickPoint(el, x, y)) return false;
                token.ThrowIfCancellationRequested();
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                try { Wait(30, token); }
                finally { mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0); }
                if (doubleClick && i == 0) Wait(80, token);
            }
            token.ThrowIfCancellationRequested();
            return true;
        }
        finally { SetCursorPos(old.x, old.y); }
    }

    private static bool SafeClickPoint(IUIAutomationElement el, int x, int y)
    {
        try
        {
            var r = el.CurrentBoundingRectangle;
            return x >= r.left && x < r.right && y >= r.top && y < r.bottom
                && IsExplorerWindow(WindowFromPoint(new POINT(x, y)));
        }
        catch (COMException) { return false; }
    }

    private static bool IsExplorerWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static void Wait(int milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested();
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) <= 0 ? "" : sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out tagRECT rect);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
        public POINT(int x, int y) { this.x = x; this.y = y; }
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const int InvokePatternId = 10000;
    private const int LegacyPatternId = 10020;
    private const int ButtonControlTypeId = 50000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
}
