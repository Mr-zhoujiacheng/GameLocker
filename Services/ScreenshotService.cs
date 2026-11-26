using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 全屏截图并放到剪贴板（不落盘）。
///
/// 关键点：<c>CopyFromScreen</c>（BitBlt）不会写 alpha 通道，如果截到 32bpp 位图里，
/// 剪贴板里的 CF_DIB 就会带一个 0/255 混杂的 alpha（BI_BITFIELDS），
/// 粘贴到微信/QQ/浏览器这类认 alpha 的程序里就变成"全透明"——看起来像没粘上。
/// 所以这里统一截成 24bpp，并显式写 CF_DIB（BI_RGB 24bpp、自下而上），
/// 兼容性最好；CF_BITMAP 一并写上，给只认位图句柄的老程序兜底。
/// </summary>
internal static class ScreenshotService
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static bool CaptureToClipboard()
    {
        try
        {
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (width <= 0 || height <= 0) return false;

            // 24bpp：不带 alpha，粘到哪里都不会"透明"
            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

            var dib = BuildDib(bitmap);
            var dibHandle = AllocGlobalCopy(dib);
            var bitmapHandle = bitmap.GetHbitmap();

            if (!TryOpenClipboard())
            {
                DebugLog.Write("[截图] 打开剪贴板失败（被其它程序占用）");
                FreeDib(ref dibHandle);
                FreeBitmap(ref bitmapHandle);
                return false;
            }

            try
            {
                EmptyClipboard();

                var dibOk = false;
                if (dibHandle != IntPtr.Zero)
                {
                    dibOk = SetClipboardData(CF_DIB, dibHandle) != IntPtr.Zero;
                    if (dibOk) dibHandle = IntPtr.Zero;   // 所有权交给剪贴板
                }

                var bitmapOk = false;
                if (bitmapHandle != IntPtr.Zero)
                {
                    bitmapOk = SetClipboardData(CF_BITMAP, bitmapHandle) != IntPtr.Zero;
                    if (bitmapOk) bitmapHandle = IntPtr.Zero;
                }

                if (!dibOk && !bitmapOk)
                {
                    DebugLog.Write("[截图] 写入剪贴板失败 err=" + Marshal.GetLastWin32Error());
                    return false;
                }

                DebugLog.Write($"[截图] 已复制到剪贴板 {width}x{height} dib={dibOk} bitmap={bitmapOk}");
                return true;
            }
            finally
            {
                CloseClipboard();
                FreeDib(ref dibHandle);
                FreeBitmap(ref bitmapHandle);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("[截图] 失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>位图 → 剪贴板用的 CF_DIB：BITMAPINFOHEADER + BI_RGB 24bpp + 自下而上的像素行。</summary>
    private static byte[] BuildDib(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var stride = (width * 3 + 3) & ~3;
        var imageSize = stride * height;
        var dib = new byte[40 + imageSize];

        WriteInt32(dib, 0, 40);          // biSize
        WriteInt32(dib, 4, width);       // biWidth
        WriteInt32(dib, 8, height);      // biHeight（正数 = 自下而上）
        WriteInt16(dib, 12, 1);          // biPlanes
        WriteInt16(dib, 14, 24);         // biBitCount
        WriteInt32(dib, 16, 0);          // biCompression = BI_RGB
        WriteInt32(dib, 20, imageSize);  // biSizeImage

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var row = 0; row < height; row++)
            {
                // GDI+ 的位图是自上而下，DIB 要自下而上
                var src = IntPtr.Add(data.Scan0, (height - 1 - row) * data.Stride);
                Marshal.Copy(src, dib, 40 + row * stride, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return dib;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    /// <summary>把字节数组拷进可移动到剪贴板的全局内存块（失败返回 IntPtr.Zero）。</summary>
    private static IntPtr AllocGlobalCopy(byte[] data)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            return IntPtr.Zero;
        }
        try
        {
            Marshal.Copy(data, 0, target, data.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }
        return handle;
    }

    /// <summary>剪贴板经常被其它程序短暂占用，重试一小会儿再失败。</summary>
    private static bool TryOpenClipboard()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static void FreeDib(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        GlobalFree(handle);
        handle = IntPtr.Zero;
    }

    private static void FreeBitmap(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        DeleteObject(handle);
        handle = IntPtr.Zero;
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
