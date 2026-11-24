using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 判断“当前前台”是否为最大化窗口：只有前台最大化时才把顶栏改成不透明。
/// 桌面上即使后面还有最大化的窗口，也保持渐变透明。
/// </summary>
internal static class WindowStateWatcher
{
    public static bool HasVisibleMaximizedWindow()
    {
        var hwnd = GetForegroundWindow();
        return IsUsableMaximized(hwnd);
    }

    private static bool IsUsableMaximized(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) || !IsZoomed(hwnd)) return false;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId) return false;

        var cls = ClassName(hwnd);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow");
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buf = new System.Text.StringBuilder(128);
        var n = GetClassName(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString(0, n) : "";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);
}
