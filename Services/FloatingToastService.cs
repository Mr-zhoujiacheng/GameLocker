using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace GameLocker.Services;

/// <summary>
/// 独立 Win32 分层文字提示。任意线程均可调用，内部会切回主 UI 线程；
/// 窗口常驻不销毁，超时只隐藏，显示期间持续置顶并跟随前台窗口所在显示器。
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
    private static readonly WndProcDelegate WindowProc = WndProc;
    private static bool _registered;
    private static IntPtr _hwnd;
    private static DispatcherQueue? _queue;
    private static DispatcherQueueTimer? _hideTimer;
    private static DispatcherQueueTimer? _followTimer;
    private static string _text = "";
    private static bool _warning;
    private static IntPtr _monitor;
    private static uint _dpi;

    /// <summary>
    /// 在前台窗口所在显示器的工作区顶部居中显示约八秒；重复调用替换内容并重新计时。
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
            _followTimer!.Stop();
            EnsureWindow();
            RenderForForeground();
            Raise();
            _hideTimer.Start();
            // 游戏切到前台、小黄鸭输出层出现后仍保持可见，并跟随显示器的变化。
            _followTimer.Start();
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
        _hideTimer.Interval = TimeSpan.FromSeconds(8);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += OnHideTick;
        _followTimer = queue.CreateTimer();
        _followTimer.Interval = TimeSpan.FromMilliseconds(400);
        _followTimer.IsRepeating = true;
        _followTimer.Tick += OnFollowTick;
        queue.ShutdownStarting += OnShutdownStarting;
    }

    /// <summary>按当前前台窗口所在显示器重新绘制提示内容。</summary>
    private static void RenderForForeground()
    {
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground, 2 /* MONITOR_DEFAULTTONEAREST */);
        var dpi = foreground != IntPtr.Zero ? GetDpiForWindow(foreground) : 96u;
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Render(_text, _warning, info.rcWork, (dpi == 0 ? 96 : dpi) / 96.0);
        _monitor = monitor;
        _dpi = dpi;
    }

    /// <summary>不使用 Activate/SetForegroundWindow；分层窗口的 TRANSPARENT 样式使鼠标穿透。</summary>
    private static void Raise()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!SetWindowPos(_hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void OnHideTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _followTimer?.Stop();
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
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
            var dpi = foreground != IntPtr.Zero ? GetDpiForWindow(foreground) : 96u;
            if (monitor != _monitor || dpi != _dpi) RenderForForeground();
            // 每轮都重新置顶：小黄鸭输出层是全屏置顶窗口，否则会把提示挡在下面。
            Raise();
        }
        catch (Exception ex)
        {
            DebugLog.Write("悬浮提示置顶失败: " + ex.Message);
        }
    }

    private static void OnShutdownStarting(DispatcherQueue sender, DispatcherQueueShutdownStartingEventArgs args)
    {
        foreach (var timer in new[] { _hideTimer, _followTimer })
        {
            if (timer is null) continue;
            timer.Stop();
            timer.Tick -= OnHideTick;
            timer.Tick -= OnFollowTick;
        }
        _hideTimer = null;
        _followTimer = null;
        DestroyToast();
        sender.ShutdownStarting -= OnShutdownStarting;
        _queue = null;
    }

    private static void DestroyToast()
    {
        if (_hwnd == IntPtr.Zero) return;
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

    private static void Render(string text, bool warning, RECT work, double scale)
    {
        int Scaled(int value) => Math.Max(1, (int)Math.Round(value * scale));
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception();
        var memory = IntPtr.Zero;
        var font = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var oldFont = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new Win32Exception();
            font = CreateFontW(-Scaled(17), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0,
                4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
            if (font == IntPtr.Zero) throw new Win32Exception();
            oldFont = SelectObject(memory, font);
            if (!GetTextExtentPoint32W(memory, text, text.Length, out var extent))
                throw new Win32Exception();

            var workWidth = work.Right - work.Left;
            var workHeight = work.Bottom - work.Top;
            var margin = Math.Min(Scaled(16), Math.Max(0, (workWidth - 1) / 2));
            var width = (int)Math.Min((long)extent.CX + Scaled(36), workWidth - margin * 2L);
            var height = Math.Min(Math.Max(Scaled(46), extent.CY + Scaled(20)), workHeight);
            if (width <= 0 || height <= 0) return;
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32
                }
            };
            bitmap = CreateDIBSection(memory, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception();
            oldBitmap = SelectObject(memory, bitmap);
            const int background = 0x001A1D23;
            var pixels = new int[checked(width * height)];
            Array.Fill(pixels, background);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            SetBkMode(memory, 1 /* TRANSPARENT */);
            SetTextColor(memory, warning ? 0x007ACFFFu : 0x00FFFFFFu);
            var padding = Math.Min(Scaled(18), (width - 1) / 2);
            var rect = new RECT { Left = padding, Top = 0, Right = width - padding, Bottom = height };
            DrawTextW(memory, text, text.Length, ref rect,
                0x0001 | 0x0004 | 0x0020 | 0x0800 | 0x8000 /* CENTER|VCENTER|SINGLELINE|NOPREFIX|END_ELLIPSIS */);
            GdiFlush();
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // GDI 不写 alpha：背景预乘为半透明，文字保持不透明，与监控浮条相同。
            for (var i = 0; i < pixels.Length; i++)
            {
                var color = pixels[i] & 0xFFFFFF;
                if (color != background)
                {
                    pixels[i] = unchecked((int)(0xFF000000u | (uint)color));
                    continue;
                }
                const int alpha = 210;
                var r = ((color >> 16) & 255) * alpha / 255;
                var g = ((color >> 8) & 255) * alpha / 255;
                var b = (color & 255) * alpha / 255;
                pixels[i] = (alpha << 24) | (r << 16) | (g << 8) | b;
            }
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var destination = new POINT
            {
                X = work.Left + (workWidth - width) / 2,
                Y = work.Top + Math.Min(Scaled(24), Math.Max(0, workHeight - height))
            };
            var source = new POINT();
            var size = new SIZE { CX = width, CY = height };
            var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(_hwnd, screen, ref destination, ref size, memory, ref source,
                    0, ref blend, 2 /* ULW_ALPHA */))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memory, oldBitmap);
            if (oldFont != IntPtr.Zero) SelectObject(memory, oldFont);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (font != IntPtr.Zero) DeleteObject(font);
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
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size,
        IntPtr srcDc, ref POINT src, uint colorKey, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr dc, string text, int count, ref RECT rect, uint format);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int count, out SIZE size);
    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr dc, uint color);
}
