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
    /// <summary>为 target 生成/更新图标文件，返回 icons 目录内的文件名；失败返回 null。
    /// 整个提取（Shell 取图 + 编码 PNG）放到后台线程，批量导入时不再阻塞 UI</summary>
    public static Task<string?> ExtractAsync(string target) => Task.Run(() => ExtractCoreAsync(target));

    private static async Task<string?> ExtractCoreAsync(string target)
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
        catch (Exception ex)
        {
            DebugLog.Write($"[图标] 提取失败 {target}：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>目标已有缓存图标时返回其完整路径（不重新提取）；无缓存返回 null</summary>
    public static string? CachedIconPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var path = Path.Combine(LaunchItem.IconsDir, Hash(target) + ".png");
        try { return new FileInfo(path).Length > 0 ? path : null; }
        catch { return null; }
    }

    /// <summary>
    /// 原生/商店应用（shell:AppsFolder\AUMID）：包内应用的 exe 里没有真实图标，
    /// 只有问 Shell 要 AUMID 对应的那张图才是设置/计算器真正的图标。
    /// </summary>
    public static Task<string?> ExtractShellItemAsync(string shellTarget)
        => Task.Run(() => ExtractShellItemCoreAsync(shellTarget));

    /// <summary>商店应用图标的缓存版本：图标处理方式变了就加一版，旧缓存自动失效。</summary>
    private const string ShellIconVersion = "|shell-v2";

    /// <summary>商店应用图标缓存路径（没有缓存返回 null）。</summary>
    public static string? CachedShellIconPath(string shellTarget)
    {
        if (string.IsNullOrWhiteSpace(shellTarget)) return null;
        var path = Path.Combine(LaunchItem.IconsDir, Hash(shellTarget + ShellIconVersion) + ".png");
        try { return new FileInfo(path).Length > 0 ? path : null; }
        catch { return null; }
    }

    private static async Task<string?> ExtractShellItemCoreAsync(string shellTarget)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(shellTarget)) return null;
            Directory.CreateDirectory(LaunchItem.IconsDir);
            var fileName = Hash(shellTarget + ShellIconVersion) + ".png";
            var outPath = Path.Combine(LaunchItem.IconsDir, fileName);

            const int size = 256;
            var buf = new byte[size * size * 4];
            if (!TryGetShellImagePixels(shellTarget, size, buf)) return null;

            // AppsFolder 给的图周围常带一大圈透明留白，不裁掉在底栏里会显得特别小
            TrimCenterPadding(buf, size);
            await EncodePngAsync(size, buf, outPath);
            return fileName;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[图标] 商店应用图标提取失败 {shellTarget}：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>把非透明内容裁出来再等比放大到画布约九成（只用于商店应用图标）。</summary>
    private static void TrimCenterPadding(byte[] buf, int size)
    {
        int minX = size, minY = size, maxX = -1, maxY = -1;
        for (var y = 0; y < size; y++)
        {
            var row = y * size * 4;
            for (var x = 0; x < size; x++)
            {
                if (buf[row + x * 4 + 3] < 8) continue;   // BGRA：第 4 个字节是 alpha
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return;

        var contentWidth = maxX - minX + 1;
        var contentHeight = maxY - minY + 1;
        if (contentWidth >= size * 0.86 && contentHeight >= size * 0.86) return;

        var target = (int)(size * 0.92);
        var scale = Math.Min((double)target / contentWidth, (double)target / contentHeight);
        var newWidth = Math.Max(1, (int)Math.Round(contentWidth * scale));
        var newHeight = Math.Max(1, (int)Math.Round(contentHeight * scale));
        var offsetX = (size - newWidth) / 2;
        var offsetY = (size - newHeight) / 2;

        var dst = new byte[buf.Length];
        for (var y = 0; y < newHeight; y++)
        {
            var srcY = minY + Math.Min(contentHeight - 1, (int)(y / scale));
            for (var x = 0; x < newWidth; x++)
            {
                var srcX = minX + Math.Min(contentWidth - 1, (int)(x / scale));
                var si = (srcY * size + srcX) * 4;
                var di = ((offsetY + y) * size + (offsetX + x)) * 4;
                dst[di] = buf[si];
                dst[di + 1] = buf[si + 1];
                dst[di + 2] = buf[si + 2];
                dst[di + 3] = buf[si + 3];
            }
        }
        System.Buffer.BlockCopy(dst, 0, buf, 0, buf.Length);
    }

    /// <summary>商店应用的 AUMID → 取图标用的 shell 路径（空 AUMID 返回空串）。</summary>
    public static string ShellIconKeyOf(string appId)
        => string.IsNullOrEmpty(appId) || appId.IndexOf('!') < 0 ? "" : "shell:AppsFolder\\" + appId;

    /// <summary>图标缓存版本标记：旧版本生成的 256 图是 Shell 低质量放大，首次启动重渲染一次</summary>
    private const string CacheVersionMarker = ".extract-v2";

    /// <summary>是否已完成新版图标重渲染</summary>
    public static bool CacheUpgraded => File.Exists(Path.Combine(LaunchItem.IconsDir, CacheVersionMarker));

    /// <summary>一次性把已有磁贴的图标按新算法重渲染（哈希文件名不变，IconFile 无需改写）</summary>
    public static async Task<bool> UpgradeCachedIconsAsync(IEnumerable<LaunchItem> roots)
    {
        if (CacheUpgraded) return false;
        try
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots) CollectTargets(root, targets);
            foreach (var target in targets) await ExtractAsync(target);

            Directory.CreateDirectory(LaunchItem.IconsDir);
            await File.WriteAllTextAsync(Path.Combine(LaunchItem.IconsDir, CacheVersionMarker), "v2");
            return true;
        }
        catch { return false; }
    }

    private static void CollectTargets(LaunchItem item, HashSet<string> targets)
    {
        if ((item.Kind == LaunchKind.App || item.Kind == LaunchKind.Folder) && !string.IsNullOrWhiteSpace(item.Target))
            targets.Add(item.Target);
        foreach (var m in item.Members) CollectTargets(m, targets);
        if (item.Drawers is { } drawers)
            foreach (var page in drawers)
                foreach (var it in page.Items)
                    CollectTargets(it, targets);
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

        // 路径 1：直接从 exe/dll/ico 取内嵌图标（原生 256 时 1:1 绘制，最清晰）。
        // 不走 Shell GetImage：它对低分辨率图标会做低质量放大，256 缓存图明显发糊
        if (CanExtractEmbeddedIcon(path) && TryGetEmbeddedIcon(path, size, out var hicon, out var hsmall))
        {
            try
            {
                if (hsmall != IntPtr.Zero) DestroyIcon(hsmall);
                if (TryDrawIconPixels(hicon, size, buf))
                {
                    await EncodePngAsync(size, buf, outPath);
                    return true;
                }
            }
            finally { if (hicon != IntPtr.Zero) DestroyIcon(hicon); }
        }

        // 路径 2：Shell 缩略图接口（.lnk / 文件夹 / 无内嵌图标的文件）
        if (TryGetShellImagePixels(path, size, buf))
        {
            await EncodePngAsync(size, buf, outPath);
            return true;
        }

        // 路径 3：ExtractIconEx 取 HICON，DrawIconEx 绘制到 32bpp DIB
        ExtractIconExW(path, 0, out var fallbackIcon, out var fallbackSmall, 1);
        try
        {
            if (fallbackIcon == IntPtr.Zero)
            {
                DebugLog.Write($"[图标] 三条路径都失败：{path}");
                return false;
            }
            if (fallbackSmall != IntPtr.Zero) DestroyIcon(fallbackSmall);
            if (!TryDrawIconPixels(fallbackIcon, size, buf)) return false;
            await EncodePngAsync(size, buf, outPath);
            return true;
        }
        finally
        {
            if (fallbackIcon != IntPtr.Zero) DestroyIcon(fallbackIcon);
        }
    }

    /// <summary>exe/dll/ico 才有内嵌图标可直接取；.lnk/文件夹仍走 Shell</summary>
    private static bool CanExtractEmbeddedIcon(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".exe" or ".dll" or ".ico";
    }

    /// <summary>PrivateExtractIcons 取指定尺寸的 HICON（按 exe 内嵌图标资源原样返回，不做 Shell 缩放）</summary>
    private static bool TryGetEmbeddedIcon(string path, int size, out IntPtr hicon, out IntPtr hsmall)
    {
        hicon = IntPtr.Zero;
        hsmall = IntPtr.Zero;
        var icons = new IntPtr[1];
        var ids = new int[1];
        try
        {
            if (PrivateExtractIconsW(path, 0, size, size, icons, ids, 1, 0) <= 0 || icons[0] == IntPtr.Zero)
                return false;
            hicon = icons[0];
            return true;
        }
        catch
        {
            if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]);
            return false;
        }
    }

    /// <summary>IShellItemImageFactory::GetImage 返回 32bpp 预乘 BGRA HBITMAP，用 GetDIBits 读像素</summary>
    private static bool TryGetShellImagePixels(string path, int size, byte[] buf)
    {
        // 线程池线程默认不初始化 COM（这里走的是 P/Invoke + 裸 vtable，不会像 RCW 那样自动 CoInitialize），
        // 显式按 MTA 初始化，保证 Shell 的图片工厂可用；只有真的由我们初始化时才反初始化。
        var comOwned = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED) == 0;

        IntPtr item = IntPtr.Zero;
        IntPtr hbmp = IntPtr.Zero;
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, oldObj = IntPtr.Zero;
        try
        {
            Guid iidFactory = IID_IShellItemImageFactory;
            var hrCreate = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidFactory, out item);
            if (hrCreate != 0 || item == IntPtr.Zero)
            {
                DebugLog.Write($"[图标] SHCreateItemFromParsingName hr=0x{hrCreate:X8} {path}");
                return false;
            }

            // IShellItemImageFactory vtbl：IUnknown(0-2) 之后第 3 槽 = GetImage
            var vtbl = Marshal.ReadIntPtr(item);
            var pGetImage = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
            var fn = (GetImageDelegate)Marshal.GetDelegateForFunctionPointer(pGetImage, typeof(GetImageDelegate));
            var sz = new SIZE { cx = size, cy = size };
            var hrImage = fn(item, sz, SIIGBF_BIGGERSIZEOK, ref hbmp);
            if (hrImage != 0 || hbmp == IntPtr.Zero)
            {
                DebugLog.Write($"[图标] GetImage hr=0x{hrImage:X8} hbm={hbmp} {path}");
                return false;
            }

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
        catch (Exception ex)
        {
            DebugLog.Write($"[图标] Shell 取图异常 {path}：{ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (comOwned) CoUninitialize();
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
        new("bcc18b79-ba16-442f-80c4-8a59c30c463b");   // 注意：不是 BC14D7C4-…，写错会一直拿到 E_NOINTERFACE

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIconsW(string lpszFile, int nIconIndex,
        int cxIcon, int cyIcon, IntPtr[] phicon, int[] piconid, int nIcons, int flags);

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

    private const uint COINIT_MULTITHREADED = 0x0;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
