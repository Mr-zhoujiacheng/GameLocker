using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 从输出目录的 Assets\GameLocker.ico 加载图标句柄（纯 Win32，不依赖 System.Drawing）。
/// 供托盘图标与窗口/任务栏图标共用。
/// </summary>
public static class IconLoader
{
    private const int IMAGE_ICON = 1;
    private const int LR_LOADFROMFILE = 0x0010;
    private const int LR_SHARED = 0x0080;

    public static IntPtr Large => Load(32);
    public static IntPtr Small => Load(16);

    private static IntPtr Load(int size)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "GameLocker.ico");
            if (File.Exists(path))
            {
                var h = LoadImage(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { }
        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    private const int IDI_APPLICATION = 32512;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, int type, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInst, int id);
}
