using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 检测前台是否全屏运行（无边框全屏或独占全屏）。
/// 判定依据：前台窗口可见、未最小化、属于其他进程，且窗口矩形基本盖满所在显示器。
/// 独占全屏下普通置顶窗口无法覆盖，只有无边框全屏能正常显示监控条。
/// </summary>
internal static class FullscreenDetector
{
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    /// <summary>前台是否全屏（桌面、任务栏、本进程窗口、监控悬浮条都不算）</summary>
    public static bool IsForegroundFullscreen() => TryGet(out _, out _, out _);

    /// <summary>
    /// 取前台全屏窗口；返回其句柄、标题与是否带边框。
    /// 带边框（有标题栏/可调边框）但盖满屏幕的窗口通常是“全屏优化”或普通最大化，不算独占全屏。
    /// </summary>
    public static bool TryGet(out IntPtr hwnd, out string title, out bool hasFrame)
    {
        hwnd = IntPtr.Zero;
        title = "";
        hasFrame = true;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsWindowVisible(fg) || IsIconic(fg)) return false;

        GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        var className = cls.ToString();
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "GameLockerMonitorOverlay") return false;

        if (!GetWindowRect(fg, out var rect)) return false;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return false;

        var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;

        var mw = info.rcMonitor.Right - info.rcMonitor.Left;
        var mh = info.rcMonitor.Bottom - info.rcMonitor.Top;
        // 允许 ±2px 的取整误差
        var covers = Math.Abs(rect.Left - info.rcMonitor.Left) <= 2
                     && Math.Abs(rect.Top - info.rcMonitor.Top) <= 2
                     && Math.Abs(w - mw) <= 2 && Math.Abs(h - mh) <= 2;
        if (!covers) return false;

        var style = GetWindowLong(fg, GWL_STYLE);
        hasFrame = (style & WS_CAPTION) != 0 || (style & WS_THICKFRAME) != 0;
        hwnd = fg;
        title = className;
        return true;
    }

    /// <summary>前台是否为独占式全屏（盖满屏幕且无标题栏/边框）</summary>
    public static bool IsExclusiveLike()
    {
        return TryGet(out _, out _, out var hasFrame) && !hasFrame;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
