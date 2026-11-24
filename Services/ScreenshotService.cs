using System.Drawing;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>全屏截图并放到剪贴板（不落盘）。</summary>
internal static class ScreenshotService
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint CF_BITMAP = 2;

    public static bool CaptureToClipboard()
    {
        try
        {
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (width <= 0 || height <= 0) return false;

            using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

            var handle = bitmap.GetHbitmap();
            if (handle == IntPtr.Zero) return false;

            if (!OpenClipboard(IntPtr.Zero))
            {
                DeleteObject(handle);
                return false;
            }

            try
            {
                EmptyClipboard();
                if (SetClipboardData(CF_BITMAP, handle) == IntPtr.Zero)
                {
                    DeleteObject(handle);
                    return false;
                }
                handle = IntPtr.Zero; // 所有权交给剪贴板
                return true;
            }
            finally
            {
                CloseClipboard();
                if (handle != IntPtr.Zero) DeleteObject(handle);
            }
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
