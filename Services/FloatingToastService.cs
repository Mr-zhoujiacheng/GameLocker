using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace GameLocker.Services;

/// <summary>
/// 独立 Win32 分层文字提示。任意线程均可调用，内部会切回主 UI 线程；
/// 窗口常驻不销毁，超时只隐藏，显示期间持续置顶并跟随前台窗口所在显示器。
///
/// 外观：深色玻璃圆角块（12px 圆角 + 1px 淡描边 + 外发光）、左侧强调色竖条、
/// 类型图标 + 15px 半粗文字；显示/隐藏走淡入淡出，默认停留 12 秒。
/// </summary>
public static class FloatingToastService
{
    private const string ClassName = "GameLockerFloatingToast";
    private const string WindowTitle = "GameLocker Toast";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int SW_HIDE = 0;
    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>提示停留时长（从淡入结束算起）。</summary>
    private const double HideSeconds = 12;
    private const int FadeStepMs = 16;
    private const int FadeInStep = 34;    // 约 120ms 淡入
    private const int FadeOutStep = 22;   // 约 180ms 淡出

    private static readonly WndProcDelegate WindowProc = WndProc;
    private static bool _registered;
    private static IntPtr _hwnd;
    private static DispatcherQueue? _queue;
    private static DispatcherQueueTimer? _hideTimer;
    private static DispatcherQueueTimer? _followTimer;
    private static DispatcherQueueTimer? _fadeTimer;
    private static readonly OptiOverlayOrder CaptureOrder = new();
    private static string _text = "";
    private static bool _warning;
    private static IntPtr _monitor;
    private static uint _dpi;

    // 渲染结果（预乘 alpha 的 32bpp 像素）+ 位置 + 当前整体透明度
    private static int[] _pixels = [];
    private static int _width;
    private static int _height;
    private static int _destX;
    private static int _destY;
    private static int _alpha;
    private static int _alphaTarget;
    private static bool _hiding;

    /// <summary>
    /// 在前台窗口所在显示器的工作区顶部居中显示约十二秒；重复调用替换内容并重新计时。
    /// 显示期间每 400ms 重新置顶并检查前台显示器，避免被小黄鸭输出层等置顶窗口盖住。
    /// 空白消息不显示；App 尚未初始化时静默忽略。
    /// </summary>
    public static void Show(string message, bool warning = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var queue = App.Main?.DispatcherQueue;
        if (queue is null) return;
        if (!queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => Show(message, warning));
            return;
        }
        ShowCore(message, warning);
    }

    private static void ShowCore(string message, bool warning)
    {
        try
        {
            EnsureTimers();
            _text = message.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            _warning = warning;
            _hideTimer!.Stop();
            _fadeTimer!.Stop();
            EnsureWindow();
            RenderForForeground();
            _alpha = 0;
            _alphaTarget = 255;
            _hiding = false;
            Present();
            Raise();
            _fadeTimer.Start();
            _hideTimer.Start();
            // 游戏切到前台、小黄鸭输出层出现后仍保持可见，并跟随显示器的变化。
            _followTimer!.Start();
        }
        catch (Exception ex)
        {
            // 提示失败不影响游戏，也不销毁正在显示的旧提示。
            DebugLog.Write("悬浮提示显示失败: " + ex.Message);
        }
    }

    private static void EnsureTimers()
    {
        if (_queue is not null) return;
        var queue = App.Main?.DispatcherQueue;
        if (queue is null) return;
        _queue = queue;
        _hideTimer = queue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(HideSeconds);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += OnHideTick;
        _followTimer = queue.CreateTimer();
        _followTimer.Interval = TimeSpan.FromMilliseconds(400);
        _followTimer.IsRepeating = true;
        _followTimer.Tick += OnFollowTick;
        _fadeTimer = queue.CreateTimer();
        _fadeTimer.Interval = TimeSpan.FromMilliseconds(FadeStepMs);
        _fadeTimer.IsRepeating = true;
        _fadeTimer.Tick += OnFadeTick;
        queue.ShutdownStarting += OnShutdownStarting;
    }

    /// <summary>按当前前台窗口所在显示器重新渲染提示。</summary>
    private static void RenderForForeground()
    {
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // 用显示器自身的 DPI（不是前台窗口的）：前台可能是 DPI 无关的老程序，
        // 拿它的 DPI 会让提示按 100% 渲染，在高缩放屏上显得又小又糊。
        var scale = 1.0;
        if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;
        BuildPlate(info.rcWork, scale);
        _monitor = monitor;
        _dpi = (uint)Math.Round(scale * 96);
    }

    /// <summary>不使用 Activate/SetForegroundWindow；分层窗口的 TRANSPARENT 样式使鼠标穿透。</summary>
    private static void Raise()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!SetWindowPos(_hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static bool UpdateCaptureOrder() =>
        CaptureOrder.Update(_hwnd, LosslessScalingService.ActiveOptiSession?.HostPid ?? 0);

    private static void OnHideTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        // 先淡出，淡到 0 再真正隐藏（见 OnFadeTick）
        _hiding = true;
        _alphaTarget = 0;
        _fadeTimer?.Start();
    }

    private static void OnFadeTick(DispatcherQueueTimer sender, object args)
    {
        if (_alpha == _alphaTarget)
        {
            sender.Stop();
            if (_hiding && _alpha == 0) FinishHide();
            return;
        }

        var step = _hiding ? FadeOutStep : FadeInStep;
        _alpha = _alpha < _alphaTarget
            ? Math.Min(_alphaTarget, _alpha + step)
            : Math.Max(_alphaTarget, _alpha - step);
        try
        {
            Present();
        }
        catch (Exception ex)
        {
            DebugLog.Write("悬浮提示淡入淡出失败: " + ex.Message);
            sender.Stop();
        }

        if (_alpha == _alphaTarget && _hiding && _alpha == 0) FinishHide();
    }

    private static void FinishHide()
    {
        _hiding = false;
        _alphaTarget = 0;
        _followTimer?.Stop();
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
        CaptureOrder.Detach(_hwnd);
    }

    private static void OnFollowTick(DispatcherQueueTimer sender, object args)
    {
        if (_hwnd == IntPtr.Zero)
        {
            sender.Stop();
            return;
        }
        try
        {
            var foreground = GetForegroundWindow();
            var monitor = MonitorFromWindow(foreground, 2 /* MONITOR_DEFAULTTONEAREST */);
            var dpi = GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var monitorDpi, out _) == 0
                ? monitorDpi
                : 96u;
            if (monitor != _monitor || dpi != _dpi) RenderForForeground();
            if (!UpdateCaptureOrder()) Raise();
        }
        catch (Exception ex)
        {
            DebugLog.Write("悬浮提示置顶失败: " + ex.Message);
        }
    }

    private static void OnShutdownStarting(DispatcherQueue sender, DispatcherQueueShutdownStartingEventArgs args)
    {
        foreach (var timer in new[] { _hideTimer, _followTimer, _fadeTimer })
        {
            if (timer is null) continue;
            timer.Stop();
            timer.Tick -= OnHideTick;
            timer.Tick -= OnFollowTick;
            timer.Tick -= OnFadeTick;
        }
        _hideTimer = null;
        _followTimer = null;
        _fadeTimer = null;
        DestroyToast();
        sender.ShutdownStarting -= OnShutdownStarting;
        _queue = null;
    }

    private static void DestroyToast()
    {
        if (_hwnd == IntPtr.Zero) return;
        CaptureOrder.Detach(_hwnd);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private static void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        var instance = GetModuleHandleW(null);
        if (!_registered)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
                hInstance = instance,
                lpszClassName = ClassName
            };
            if (RegisterClassExW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
        _hwnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            ClassName, WindowTitle, WS_POPUP, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>把提示画成一张预乘 alpha 的位图（圆角玻璃块 + 光晕 + 左侧强调条 + 图标 + 文字）。</summary>
    private static void BuildPlate(RECT work, double scale)
    {
        int Scaled(int value) => Math.Max(1, (int)Math.Round(value * scale));

        var workWidth = work.Right - work.Left;
        var workHeight = work.Bottom - work.Top;

        var fontPx = 15f * (float)scale;
        var iconPx = 16f * (float)scale;
        var padX = Scaled(18);
        var padY = Scaled(13);
        var gap = Scaled(10);
        var radius = Scaled(12);
        var accentWidth = Math.Max(2, Scaled(3));
        var accentInset = Scaled(1);

        using var textFont = new Font("Microsoft YaHei UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var iconFont = new Font("Segoe MDL2 Assets", iconPx, FontStyle.Regular, GraphicsUnit.Pixel);
        var glyph = _warning ? "\uE7BA" : "\uE946";   // 警告三角 / 信息
        var accent = _warning ? Color.FromArgb(0xFF, 0xFF, 0xCF, 0x6B) : Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF);

        // 先量一次，算出整块宽度（上限 = 工作区宽度 - 两边留白）。
        // 量宽必须和真正绘制用同一个 StringFormat，否则中文会多算/少算导致被省略号截断。
        using var textFormat = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        float textWidth;
        float textHeight;
        using (var measure = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(measure))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var size = mg.MeasureString(_text, textFont, new SizeF(10000f, 1000f), textFormat);
            textWidth = size.Width;
            textHeight = size.Height;
        }

        var maxWidth = Math.Max(Scaled(160), workWidth - Scaled(32));
        var width = (int)Math.Min(maxWidth,
            Math.Ceiling(textWidth) + padX * 3 + accentWidth + gap + Scaled(20) + Scaled(8));
        var height = Math.Max(Scaled(48), (int)Math.Ceiling(textHeight) + padY * 2);
        if (width <= 0 || height <= 0) return;

        using var plate = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(plate))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var body = new Rectangle(Scaled(4), Scaled(4), width - Scaled(8), height - Scaled(8));
            using var bodyPath = RoundedRect(body, radius);

            // 外发光：几层低透明度、逐层扩大的圆角块，模拟柔和投影
            for (var i = 3; i >= 1; i--)
            {
                var grow = i * Scaled(2);
                using var glowPath = RoundedRect(
                    new Rectangle(body.X - grow, body.Y - grow, body.Width + grow * 2, body.Height + grow * 2),
                    radius + grow);
                using var glowBrush = new SolidBrush(Color.FromArgb(_warning ? 22 : 26, accent));
                g.FillPath(glowBrush, glowPath);
            }

            // 玻璃底 + 淡描边
            using (var fill = new LinearGradientBrush(body,
                       Color.FromArgb(0xF2, 0x22, 0x26, 0x2F), Color.FromArgb(0xEE, 0x17, 0x1A, 0x21), 90f))
                g.FillPath(fill, bodyPath);
            using (var border = new Pen(Color.FromArgb(38, 0xFF, 0xFF, 0xFF), Math.Max(1f, (float)scale)))
                g.DrawPath(border, bodyPath);

            // 左侧强调竖条
            var bar = new Rectangle(body.X + accentInset * 2, body.Y + Scaled(10),
                accentWidth, body.Height - Scaled(20));
            using (var barPath = RoundedRect(bar, accentWidth / 2f))
            using (var barBrush = new SolidBrush(accent))
                g.FillPath(barBrush, barPath);

            // 类型图标
            var iconRect = new RectangleF(body.X + padX + accentWidth, body.Y,
                Scaled(20), body.Height);
            using (var iconBrush = new SolidBrush(accent))
            using (var iconFormat = new StringFormat
                   {
                       Alignment = StringAlignment.Center,
                       LineAlignment = StringAlignment.Center,
                       FormatFlags = StringFormatFlags.NoWrap
                   })
                g.DrawString(glyph, iconFont, iconBrush, iconRect, iconFormat);

            // 正文（单行，超宽省略号；复用上面量宽用的同一个 StringFormat）
            var textLeft = iconRect.Right + gap;
            var textRect = new RectangleF(textLeft, body.Y,
                body.Right - padX - textLeft, body.Height);
            using var textBrush = new SolidBrush(Color.FromArgb(0xFF, 0xF2, 0xF4, 0xF8));
            g.DrawString(_text, textFont, textBrush, textRect, textFormat);
        }

        // 取出像素并预乘 alpha（UpdateLayeredWindow 需要预乘）
        var pixels = new int[width * height];
        var data = plate.LockBits(new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            plate.UnlockBits(data);
        }
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            var a = (p >> 24) & 0xFF;
            if (a == 0)
            {
                pixels[i] = 0;
                continue;
            }
            var r = ((p >> 16) & 0xFF) * a / 255;
            var gg = ((p >> 8) & 0xFF) * a / 255;
            var b = (p & 0xFF) * a / 255;
            pixels[i] = (a << 24) | (r << 16) | (gg << 8) | b;
        }

        _pixels = pixels;
        _width = width;
        _height = height;
        _destX = work.Left + (workWidth - width) / 2;
        _destY = work.Top + Math.Min(Scaled(24), Math.Max(0, workHeight - height));
    }

    private static GraphicsPath RoundedRect(Rectangle rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>按当前整体透明度把位图贴到分层窗口上。</summary>
    private static void Present()
    {
        if (_hwnd == IntPtr.Zero || _pixels.Length == 0 || _width <= 0 || _height <= 0) return;

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception();
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new Win32Exception();
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = _width,
                    biHeight = -_height,
                    biPlanes = 1,
                    biBitCount = 32
                }
            };
            bitmap = CreateDIBSection(memory, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception();
            oldBitmap = SelectObject(memory, bitmap);

            var frame = new int[_pixels.Length];
            if (_alpha >= 255)
            {
                Array.Copy(_pixels, frame, _pixels.Length);
            }
            else
            {
                for (var i = 0; i < frame.Length; i++)
                {
                    var p = _pixels[i];
                    var a = (p >> 24) & 0xFF;
                    if (a == 0)
                    {
                        frame[i] = 0;
                        continue;
                    }
                    // 预乘像素按比例缩放即可保持预乘关系
                    var a2 = a * _alpha / 255;
                    var r = ((p >> 16) & 0xFF) * _alpha / 255;
                    var g = ((p >> 8) & 0xFF) * _alpha / 255;
                    var b = (p & 0xFF) * _alpha / 255;
                    frame[i] = (a2 << 24) | (r << 16) | (g << 8) | b;
                }
            }
            Marshal.Copy(frame, 0, bits, frame.Length);

            var destination = new POINT { X = _destX, Y = _destY };
            var source = new POINT();
            var size = new SIZE { CX = _width, CY = _height };
            var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(_hwnd, screen, ref destination, ref size, memory, ref source,
                    0, ref blend, 2 /* ULW_ALPHA */))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memory, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == 0x0084 /* WM_NCHITTEST */) return new IntPtr(-1 /* HTTRANSPARENT */);
        if (message == 0x0021 /* WM_MOUSEACTIVATE */) return new IntPtr(3 /* MA_NOACTIVATE */);
        if (message == 0x0014 /* WM_ERASEBKGND */) return new IntPtr(1);
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
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
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref POINT destination,
        ref SIZE size, IntPtr sourceDc, ref POINT source, uint colorKey, ref BLENDFUNCTION blend, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
