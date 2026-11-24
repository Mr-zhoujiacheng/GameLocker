using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>一块显示器的信息（坐标均为物理像素）。</summary>
internal readonly record struct MonitorInfo(IntPtr Handle, AppBarHost.RECT Bounds, AppBarHost.RECT Work, bool Primary);

/// <summary>显示器枚举与定位。底栏的多显示器支持靠它选屏。</summary>
internal static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    list.Add(new MonitorInfo(
                        monitor,
                        info.rcMonitor,
                        info.rcWork,
                        (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 枚举显示器失败：" + ex.Message); }
        return list;
    }

    public static MonitorInfo Primary()
    {
        var all = All();
        return all.FirstOrDefault(m => m.Primary, all.Count > 0 ? all[0] : default);
    }

    /// <summary>鼠标当前所在的显示器。</summary>
    public static MonitorInfo UnderCursor()
    {
        if (!GetCursorPos(out var point)) return Primary();
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        return All().FirstOrDefault(m => m.Handle == monitor, Primary());
    }

    /// <summary>按设置选屏：0=主屏，1=跟随鼠标，2=指定序号。</summary>
    public static MonitorInfo Resolve(int mode, int index)
    {
        var all = All();
        if (all.Count == 0) return Primary();

        return mode switch
        {
            1 => UnderCursor(),
            2 => all[Math.Clamp(index, 0, all.Count - 1)],
            _ => all.FirstOrDefault(m => m.Primary, all[0])
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// 只用基础 MONITORINFO（不含设备名）。
    /// 之前用 MONITORINFOEX + ByValTStr 时，结构体未声明 Unicode 会按 ANSI 编组，
    /// cbSize 与系统预期不符 → GetMonitorInfo 失败 → 枚举到 0 块屏。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public AppBarHost.RECT rcMonitor;
        public AppBarHost.RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITORINFOF_PRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
