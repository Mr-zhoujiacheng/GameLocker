using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GameLocker.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GameLocker.Services;

/// <summary>
/// 图标提取/导入服务：
/// - .exe / .ico / .lnk → SHCreateItemFromParsingName 取 IShellItemImageFactory::GetImage
///   得 HICON（失败回退 ExtractIconEx）→ DrawIconEx 渲染到 32bpp DIB →
///   BitmapEncoder 编码 PNG 存 %APPDATA%\GameLocker\icons。
/// - .png → 直接复制。
/// 不使用 System.Drawing（与 WinUI XAML 编译器冲突）。
/// </summary>
public static class IconExtractService
{
    /// <summary>为 target 生成/更新图标文件，返回 icons 目录内的文件名；失败返回 null</summary>
    public static async Task<string?> ExtractAsync(string target)
    {
        try
        {
            // 文件夹也允许：shell 缩略图接口可取到文件夹图标
            if (string.IsNullOrWhiteSpace(target) || (!File.Exists(target) && !Directory.Exists(target))) return null;
            Directory.CreateDirectory(LaunchItem.IconsDir);
            var fileName = Hash(target) + ".png";
            var outPath = Path.Combine(LaunchItem.IconsDir, fileName);

            if (IsImageFile(target))
            {
                // 统一转码为真正的 PNG：BitmapImage 按扩展名选解码器，
                // 若把 jpg/bmp 直接复制成 .png 会导致解码失败、图片不显示
                if (!await TranscodeImageToPngAsync(target, outPath)) return null;
            }
            else if (!await RenderIconToPngAsync(target, outPath))
                return null;

            return fileName;
        }
        catch { return null; }
    }

    /// <summary>把任意位图文件解码后用 PNG 重新编码（保证扩展名与实际格式一致）</summary>
    private static async Task<bool> TranscodeImageToPngAsync(string srcPath, string outPath)
    {
        try
        {
            using var inStream = File.OpenRead(srcPath);
            var decoder = await BitmapDecoder.CreateAsync(inStream.AsRandomAccessStream());
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            // 与 EncodePngAsync 一致：先编码到内存流，再落盘，避免 WinRT 文件流 flush 时序问题
            using var memStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memStream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            using var fileStream = File.Create(outPath);
            memStream.AsStreamForRead().CopyTo(fileStream);
            return true;
        }
        catch { return false; }
    }

    /// <summary>位图类文件（png/jpg/jpeg/bmp/gif）转码为 PNG，其余走图标渲染</summary>
    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
    }

    private static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
        return Convert.ToHexString(bytes);
    }

    // ---- 图标 → PNG（256×256 BGRA 预乘）----

    private static async Task<bool> RenderIconToPngAsync(string path, string outPath)
    {
        const int size = 256;
        var buf = new byte[size * size * 4];

        // 路径 1：Shell 缩略图接口（exe/ico/lnk 通用，可拿到高分辨率帧）
        if (TryGetShellImagePixels(path, size, buf))
        {
            await EncodePngAsync(size, buf, outPath);
            return true;
        }

        // 路径 2：ExtractIconEx 取 HICON，DrawIconEx 绘制到 32bpp DIB
        ExtractIconExW(path, 0, out var hicon, out var small, 1);
        try
        {
            if (hicon == IntPtr.Zero) return false;
            if (small != IntPtr.Zero) DestroyIcon(small);
            if (!TryDrawIconPixels(hicon, size, buf)) return false;
            await EncodePngAsync(size, buf, outPath);
            return true;
        }
        finally
        {
            if (hicon != IntPtr.Zero) DestroyIcon(hicon);
        }
    }

    /// <summary>IShellItemImageFactory::GetImage 返回 32bpp 预乘 BGRA HBITMAP，用 GetDIBits 读像素</summary>
    private static bool TryGetShellImagePixels(string path, int size, byte[] buf)
    {
        IntPtr item = IntPtr.Zero;
        IntPtr hbmp = IntPtr.Zero;
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, oldObj = IntPtr.Zero;
        try
        {
            Guid iidFactory = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidFactory, out item) != 0 || item == IntPtr.Zero)
                return false;

            // IShellItemImageFactory vtbl：IUnknown(0-2) 之后第 3 槽 = GetImage
            var vtbl = Marshal.ReadIntPtr(item);
            var pGetImage = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
            var fn = (GetImageDelegate)Marshal.GetDelegateForFunctionPointer(pGetImage, typeof(GetImageDelegate));
            var sz = new SIZE { cx = size, cy = size };
            if (fn(item, sz, SIIGBF_BIGGERSIZEOK, ref hbmp) != 0 || hbmp == IntPtr.Zero)
                return false;

            screenDc = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screenDc);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // 负值 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            };
            var ptr = Marshal.AllocHGlobal(buf.Length);
            try
            {
                if (GetDIBits(memDc, hbmp, 0, (uint)size, ptr, ref header, DIB_RGB_COLORS) == 0)
                    return false;
                Marshal.Copy(ptr, buf, 0, buf.Length);
                return true;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { return false; }
        finally
        {
            if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            if (item != IntPtr.Zero) Marshal.Release(item);
        }
    }

    /// <summary>DrawIconEx 把 HICON 缩放到 size×size 绘制到 32bpp DIB 并读回像素</summary>
    private static bool TryDrawIconPixels(IntPtr hicon, int size, byte[] buf)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB
        };
        IntPtr hbitmap = CreateDIBSection(memDc, ref header, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            if (hbitmap == IntPtr.Zero || bits == IntPtr.Zero) return false;
            oldObj = SelectObject(memDc, hbitmap);
            DrawIconEx(memDc, 0, 0, hicon, size, size, 0, IntPtr.Zero, DI_NORMAL);
            Marshal.Copy(bits, buf, 0, buf.Length);
            return true;
        }
        catch { return false; }
        finally
        {
            if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>BGRA 预乘像素 → PNG 文件（WinRT BitmapEncoder）</summary>
    private static async Task EncodePngAsync(int size, byte[] buf, string outPath)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)size, (uint)size, 96, 96, buf);
        await encoder.FlushAsync();
        using var fileStream = File.Create(outPath);
        stream.AsStreamForRead().CopyTo(fileStream);
    }

    // ---- P/Invoke ----

    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const int DI_NORMAL = 0x0003;
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;

    private static readonly Guid IID_IShellItemImageFactory =
        new("BC14D7C4-91B5-4d09-BE40-7934B755F11E");

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetImageDelegate(IntPtr factory, SIZE size, uint flags, ref IntPtr phicon);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        ref Guid riid, out IntPtr ppv);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyHeight, int istep, IntPtr hBrush, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint startScan, uint lines,
        IntPtr bits, ref BITMAPINFOHEADER bmi, uint usage);
}
