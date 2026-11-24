using System.Runtime.InteropServices;

namespace GameLocker.Services;

internal enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3
}

/// <summary>读取系统任务栏所在边缘与厚度，供系统项跟随任务栏上下位置。</summary>
internal static class TaskbarEdgeService
{
    private const uint ABM_GETTASKBARPOS = 0x00000005;

    public static bool TryGet(out TaskbarEdge edge, out int heightDip)
    {
        edge = TaskbarEdge.Bottom;
        heightDip = 48;

        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;

        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = tray
        };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == IntPtr.Zero) return false;

        edge = data.uEdge switch
        {
            0 => TaskbarEdge.Left,
            1 => TaskbarEdge.Top,
            2 => TaskbarEdge.Right,
            _ => TaskbarEdge.Bottom
        };

        var scale = GetDpiForWindow(tray) / 96.0;
        if (scale <= 0) scale = 1;
        heightDip = Math.Clamp((int)Math.Round((data.rc.Bottom - data.rc.Top) / scale), 24, 80);
        return true;
    }

    public static bool CanUse(out string message)
    {
        message = "";
        if (!TryGet(out var edge, out _)) return true;
        if (edge is TaskbarEdge.Left or TaskbarEdge.Right)
        {
            message = "请先把任务栏位置设置为「顶部」或「底部」";
            return false;
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

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

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
