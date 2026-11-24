using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// AppBar 占位封装：向系统注册一条"预留屏幕边缘"的自定义工具栏。
/// 与隐藏任务栏再自绘不同，AppBar 是系统承认的接口，最大化窗口会自动避让，
/// 不需要和 Explorer 抢 Z 序（实测抢不过：全屏窗口存在时任务栏会被系统踢出置顶层）。
/// </summary>
internal static class AppBarHost
{
    public const uint EdgeTop = 1;
    public const uint EdgeBottom = 3;

    /// <summary>注册并申请位置。返回 false 表示系统拒绝（常见于 Explorer 未就绪）。</summary>
    public static bool Register(IntPtr hwnd, uint edge, int size, out RECT reserved)
        => Register(hwnd, edge, size, IntPtr.Zero, out reserved);

    /// <summary>在指定显示器上占位；monitor 为 0 时按窗口所在屏幕自动判断。</summary>
    public static bool Register(IntPtr hwnd, uint edge, int size, IntPtr monitor, out RECT reserved)
    {
        reserved = default;
        if (hwnd == IntPtr.Zero) return false;

        var data = NewData(hwnd);
        if (SHAppBarMessage(ABM_NEW, ref data) == IntPtr.Zero) return false;

        var work = GetWorkArea(hwnd, monitor);
        var probe = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = hwnd,
            uEdge = edge,
            rc = edge == EdgeTop
                ? new RECT { Left = work.Left, Top = work.Top, Right = work.Right, Bottom = work.Top + size }
                : new RECT { Left = work.Left, Top = work.Bottom - size, Right = work.Right, Bottom = work.Bottom }
        };

        var result = SHAppBarMessage(ABM_QUERYPOS, ref probe);
        if (result == IntPtr.Zero) { Remove(hwnd); return false; }

        // 按系统回填的实际边界修正，再正式提交
        if (edge == EdgeTop) probe.rc.Bottom = probe.rc.Top + size;
        else probe.rc.Top = probe.rc.Bottom - size;

        if (SHAppBarMessage(ABM_SETPOS, ref probe) == IntPtr.Zero) { Remove(hwnd); return false; }
        reserved = probe.rc;
        return true;
    }

    public static void Remove(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var data = NewData(hwnd);
        SHAppBarMessage(ABM_REMOVE, ref data);
    }

    /// <summary>不占位时的兜底矩形（贴屏幕边缘，但不改变工作区）。</summary>
    public static RECT EdgeRect(IntPtr hwnd, uint edge, int size)
        => EdgeRect(hwnd, edge, size, IntPtr.Zero);

    public static RECT EdgeRect(IntPtr hwnd, uint edge, int size, IntPtr monitor)
    {
        var work = GetWorkArea(hwnd, monitor);
        return edge == EdgeTop
            ? new RECT { Left = work.Left, Top = work.Top, Right = work.Right, Bottom = work.Top + size }
            : new RECT { Left = work.Left, Top = work.Bottom - size, Right = work.Right, Bottom = work.Bottom };
    }

    private static APPBARDATA NewData(IntPtr hwnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = hwnd
    };

    private static RECT GetWorkArea(IntPtr hwnd, IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info)) return info.rcWork;

        return new RECT
        {
            Left = 0,
            Top = 0,
            Right = GetSystemMetrics(SM_CXSCREEN),
            Bottom = GetSystemMetrics(SM_CYSCREEN)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
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

    /// <summary>与 shellapi.h 的 APPBARDATA 布局一致（注意末尾 lParam 为 LPARAM 宽度）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    private const uint ABM_NEW = 0x00000000;
    private const uint ABM_REMOVE = 0x00000001;
    private const uint ABM_QUERYPOS = 0x00000002;
    private const uint ABM_SETPOS = 0x00000003;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
