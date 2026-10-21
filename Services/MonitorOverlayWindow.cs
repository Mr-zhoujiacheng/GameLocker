using System.Runtime.InteropServices;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>
/// 性能监控悬浮条：独立 Win32 窗口（置顶 / 点击穿透 / 不抢焦点 / 圆角 / 整窗半透明），
/// 按用户在监控磁贴编辑页选的位置（六选一）、大小与透明度摆放，
/// 一排显示 FPS、帧延迟、帧波动线、CPU、GPU、电池、内存。
/// 用 GDI 双缓冲绘制，数据由 MonitorService 每秒推送。
/// </summary>
internal sealed class MonitorOverlayWindow : IDisposable
{
    private const string ClassName = "GameLockerMonitorOverlay";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SPI_GETWORKAREA = 0x0030;
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_DPICHANGED = 0x02E0;
    private const int HTTRANSPARENT = -1;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;

    /// <summary>各指标块宽度（96 DPI 基准）与总内边距（紧凑版）</summary>
    private const int ChipFps = 74;
    private const int ChipGraph = 84;
    private const int ChipCpu = 206;
    private const int ChipGpu = 164;
    private const int ChipBattery = 140;
    private const int ChipMemory = 58;
    private const int PadH = 10;
    private const int BarHeight = 48;

    private IntPtr _hwnd;
    private IntPtr _instance;
    private double _scale = 1;
    private int _width;
    private int _batteryWidth;
    private int _fpsWidth;
    private RECT _work;
    private int _x, _y;

    // 用户在监控磁贴编辑页选定的设置
    private MonitorPosition _position;
    private double _userScale;
    private double _opacity;

    private readonly object _renderGate = new();

    private IntPtr _fontLabel;
    private IntPtr _fontValue;
    private IntPtr _fontTip;
    private IntPtr _fontFps;

    private MonitorSnapshot? _snapshot;

    private static WndProcDelegate? _wndProc; // 防止回调被 GC
    private static bool _registered;

    public MonitorOverlayWindow(MonitorPosition position, double userScale, double opacity)
    {
        _position = position;
        _userScale = Math.Clamp(userScale, 0.6, 1.8);
        _opacity = Math.Clamp(opacity, 0.0, 1.0);
    }

    /// <summary>创建并显示悬浮条；失败返回 false</summary>
    public bool Create()
    {
        try
        {
            _instance = GetModuleHandle(null);
            if (!_registered)
            {
                _wndProc = WndProc;
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = _instance,
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = ClassName
                };
                if (RegisterClassExW(ref wc) == 0) return false;
                _registered = true;
            }

            UpdateMetrics();
            var width = _width;
            var height = Scaled(BarHeight);

            _hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
                ClassName, "GameLocker Monitor", WS_POPUP,
                0, 0, width, height, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;

            // 逐像素透明（背景/外框按透明度，文字始终不透明）由 UpdateLayeredWindow 完成
            ApplyRoundedRegion();
            CreateFonts();
            PlaceWindow();
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>应用新的位置 / 大小 / 透明度（编辑页保存后实时生效）</summary>
    public void ApplySettings(MonitorPosition position, double userScale, double opacity)
    {
        _position = position;
        _userScale = Math.Clamp(userScale, 0.6, 1.8);
        _opacity = Math.Clamp(opacity, 0.0, 1.0);
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            UpdateMetrics();
            CreateFonts();
            lock (_renderGate) { AdjustDynamicWidths(); }
            ApplyRoundedRegion();
            PlaceWindow();
        }
        catch { }
    }

    private byte AlphaValue() => (byte)Math.Round(_opacity * 255);

    /// <summary>推送新数据并重绘（可从任意线程调用）</summary>
    public void Update(MonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            lock (_renderGate)
            {
                if (AdjustDynamicWidths())
                {
                    ApplyRoundedRegion();
                    PlaceWindow();
                }
                else
                {
                    RenderLayeredCore();
                }
            }
        }
        catch { }
    }

    /// <summary>重新置顶并保持位置（全屏游戏切前后窗口可能被 DWM 降级到普通层）</summary>
    public void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE|NOMOVE|NOACTIVATE
        }
        catch { }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            try { DestroyWindow(_hwnd); } catch { }
            _hwnd = IntPtr.Zero;
        }
        DeleteFonts();
    }

    // ---- 布局 / 窗口 ----

    private static int TotalWidth() =>
        ChipGraph + ChipCpu + ChipGpu + ChipMemory + PadH * 2;

    /// <summary>FPS 块按实际帧率文字宽度收紧，让帧波动折线紧贴“帧生成”读数</summary>
    private int MeasureFpsWidth()
    {
        if (_fontFps == IntPtr.Zero || _fontLabel == IntPtr.Zero) return Scaled(ChipFps);
        var snap = _snapshot ?? new MonitorSnapshot();
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return Scaled(ChipFps);
        try
        {
            var old = SelectObject(hdc, _fontFps);
            var fps = FormatFps(snap);
            var ok = GetTextExtentPoint32W(hdc, fps, fps.Length, out var fpsSize);
            SelectObject(hdc, _fontLabel);
            var delay = FormatFrameDelay(snap);
            ok &= GetTextExtentPoint32W(hdc, delay, delay.Length, out var delaySize);
            ok &= GetTextExtentPoint32W(hdc, "FPS", 3, out var unitSize);
            SelectObject(hdc, old);
            if (!ok) return Scaled(ChipFps);
            var content = Math.Max(fpsSize.CX + Scaled(3) + unitSize.CX, delaySize.CX);
            return Math.Clamp(Scaled(15) + content + Scaled(6), Scaled(52), Scaled(ChipFps));
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    /// <summary>电池块按实际文字宽度收紧，让后面的内存块贴上来</summary>
    private int MeasureBatteryWidth()
    {
        if (_fontValue == IntPtr.Zero) return Scaled(ChipBattery);
        var text = FormatBattery(_snapshot ?? new MonitorSnapshot());
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return Scaled(ChipBattery);
        try
        {
            var old = SelectObject(hdc, _fontValue);
            var ok = GetTextExtentPoint32W(hdc, text, text.Length, out var size);
            SelectObject(hdc, old);
            if (!ok) return Scaled(ChipBattery);
            return Math.Clamp(Scaled(15) + size.CX + Scaled(12), Scaled(96), Scaled(ChipBattery));
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    /// <summary>按实际文字重新计算 FPS / 电池块宽度；变化明显时返回 true（调用方负责重设区域与位置）</summary>
    private bool AdjustDynamicWidths()
    {
        var fps = MeasureFpsWidth();
        var battery = MeasureBatteryWidth();
        var changed = false;
        if (Math.Abs(fps - _fpsWidth) >= 4) { _fpsWidth = fps; changed = true; }
        if (Math.Abs(battery - _batteryWidth) >= 4) { _batteryWidth = battery; changed = true; }
        if (!changed) return false;
        _width = Scaled(TotalWidth()) + _fpsWidth + _batteryWidth;
        return true;
    }

    private int Scaled(int v) => (int)Math.Round(v * _scale);

    private static double DpiScale()
    {
        try
        {
            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return 1;
            var dpi = GetDeviceCaps(hdc, 88); // LOGPIXELSX
            ReleaseDC(IntPtr.Zero, hdc);
            return dpi > 0 ? dpi / 96.0 : 1;
        }
        catch { return 1; }
    }

    private void ApplyRoundedRegion()
    {
        var r = Math.Max(6, Scaled(12));
        var region = CreateRoundRectRgn(0, 0, _width + 1, Scaled(BarHeight) + 1, r, r);
        if (region != IntPtr.Zero) SetWindowRgn(_hwnd, region, false);
    }

    /// <summary>按用户选择的位置摆放在屏幕工作区内：左/中/右 × 上/下</summary>
    private void PlaceWindow()
    {
        if (!SystemParametersInfoW(SPI_GETWORKAREA, 0, out _work, 0))
            _work = new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
        var workW = _work.Right - _work.Left;
        var margin = Math.Max(4, Scaled(10));
        var vMargin = Math.Max(2, Scaled(2)); // 上下只留一点点缝

        var x = _position switch
        {
            MonitorPosition.TopLeft or MonitorPosition.BottomLeft => _work.Left + margin,
            MonitorPosition.TopRight or MonitorPosition.BottomRight => _work.Right - _width - margin,
            _ => _work.Left + (workW - _width) / 2
        };
        var y = _position is MonitorPosition.BottomLeft or MonitorPosition.BottomCenter or MonitorPosition.BottomRight
            ? _work.Bottom - Scaled(BarHeight) - vMargin
            : _work.Top + vMargin;
        _x = x;
        _y = y;
        SetWindowPos(_hwnd, new IntPtr(-1), x, y, _width, Scaled(BarHeight), 0x0010 | 0x0040); // NOACTIVATE|SHOWWINDOW
        RenderLayered();
    }

    /// <summary>
    /// 按屏幕工作区与 DPI 计算缩放：超宽屏按 DPI 放大，窄屏（如掌机 1280 宽）自动缩小，
    /// 再叠加用户选的大小档位，最终不超过屏幕可用宽度。
    /// </summary>
    private void UpdateMetrics()
    {
        if (!SystemParametersInfoW(SPI_GETWORKAREA, 0, out _work, 0))
            _work = new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
        var workW = Math.Max(320, _work.Right - _work.Left);
        var byDpi = DpiScale();
        var byWidth = (workW - 24.0) / (TotalWidth() + ChipFps + ChipBattery);
        var fit = Math.Min(byDpi, byWidth);
        _scale = Math.Clamp(fit * _userScale, 0.55, Math.Max(0.55, byWidth));
        _batteryWidth = Scaled(ChipBattery);
        _fpsWidth = Scaled(ChipFps);
        _width = Scaled(TotalWidth()) + _fpsWidth + _batteryWidth;
    }

    private void CreateFonts()
    {
        lock (_renderGate)
        {
            DeleteFonts();
            _fontLabel = CreateFontW(-Scaled(9), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _fontValue = CreateFontW(-Scaled(13), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _fontTip = CreateFontW(-Scaled(9), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _fontFps = CreateFontW(-Scaled(21), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        }
    }

    private void DeleteFonts()
    {
        if (_fontLabel != IntPtr.Zero) { DeleteObject(_fontLabel); _fontLabel = IntPtr.Zero; }
        if (_fontValue != IntPtr.Zero) { DeleteObject(_fontValue); _fontValue = IntPtr.Zero; }
        if (_fontTip != IntPtr.Zero) { DeleteObject(_fontTip); _fontTip = IntPtr.Zero; }
        if (_fontFps != IntPtr.Zero) { DeleteObject(_fontFps); _fontFps = IntPtr.Zero; }
    }

    // ---- 绘制 ----

    private void Paint()
    {
        if (_hwnd == IntPtr.Zero) return;
        // 内容由 Update / PlaceWindow 直接写入分层窗口，这里只负责消费重绘消息
        var hdc = BeginPaint(_hwnd, out var ps);
        if (hdc != IntPtr.Zero) EndPaint(_hwnd, ref ps);
    }

    /// <summary>
    /// 32 位 DIB + UpdateLayeredWindow 逐像素透明：
    /// 背景与外框按用户设定的不透明度淡出，文字/折线/彩条保持 100% 不透明，
    /// 因此透明度拉到 0% 时外框完全消失、只剩清晰文字。
    /// </summary>
    private void RenderLayered()
    {
        lock (_renderGate)
        {
            RenderLayeredCore();
        }
    }

    private void RenderLayeredCore()
    {
        var w = _width;
        var h = Scaled(BarHeight);
        if (w <= 0 || h <= 0) return;

        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // 负高度 = 自上而下，与 GDI 绘制顺序一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
                biSizeImage = (uint)(w * h * 4)
            }
        };
        var bits = IntPtr.Zero;
        var dib = IntPtr.Zero;
        if (mem != IntPtr.Zero) dib = CreateDIBSection(mem, ref bmi, 0, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            return;
        }

        try
        {
            var oldBmp = SelectObject(mem, dib);
            var r = Math.Max(6, Scaled(12));
            var clip = CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r);
            var oldClip = SelectClipRgn(mem, clip);

            var count = w * h;
            var bg = new int[count];
            var buf = new int[count];

            DrawBackground(mem, w, h);
            Marshal.Copy(bits, bg, 0, count);

            DrawForeground(mem, w, h);
            Marshal.Copy(bits, buf, 0, count);

            SelectClipRgn(mem, oldClip);
            if (clip != IntPtr.Zero) DeleteObject(clip);

            var bgAlpha = AlphaValue();
            for (var i = 0; i < count; i++)
            {
                var bgColor = bg[i];
                if (bgColor == 0) { buf[i] = 0; continue; } // 圆角外：完全透明
                var c = buf[i];
                if (c != bgColor)
                {
                    // 前景覆盖处：不透明度固定 255，不受透明度设置影响
                    buf[i] = unchecked((int)(0xFF000000u | (uint)(c & 0xFFFFFF)));
                    continue;
                }
                if (bgAlpha == 255) continue;
                if (bgAlpha == 0) { buf[i] = 0; continue; }
                var b = (c & 0xFF) * bgAlpha / 255;
                var g = ((c >> 8) & 0xFF) * bgAlpha / 255;
                var rr = ((c >> 16) & 0xFF) * bgAlpha / 255;
                buf[i] = (bgAlpha << 24) | (rr << 16) | (g << 8) | b;
            }
            Marshal.Copy(buf, 0, bits, count);

            var dst = new POINT { X = _x, Y = _y };
            var src = new POINT();
            var size = new SIZE { CX = w, CY = h };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(_hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);

            SelectObject(mem, oldBmp);
        }
        finally
        {
            DeleteObject(dib);
            DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void DrawForeground(IntPtr hdc, int w, int h)
    {
        SetBkMode(hdc, 1); // TRANSPARENT

        var snap = _snapshot ?? new MonitorSnapshot();
        var x = Scaled(PadH);

        x = DrawFpsChip(hdc, x, _fpsWidth, Color(0x6F, 0xE3, 0xA0));
        x = DrawGraph(hdc, x, Scaled(ChipGraph), snap.FrameHistory, Color(0x4C, 0xC2, 0xFF));
        x = DrawChip(hdc, x, Scaled(ChipCpu), "CPU 使用 · 频率 · 温度 · 功耗", FormatCpu(snap), Color(0x6B, 0xB6, 0xFF));
        x = DrawChip(hdc, x, Scaled(ChipGpu), "GPU 使用 · 频率 · 功耗", FormatGpu(snap), Color(0xA9, 0x9B, 0xFF));
        x = DrawChip(hdc, x, _batteryWidth, "电池功耗 · 预计", FormatBattery(snap), Color(0xFF, 0xC4, 0x6B));
        DrawChip(hdc, x, Scaled(ChipMemory), "内存", FormatMemory(snap), Color(0x6F, 0xE0, 0xD8));
    }

    /// <summary>竖渐变深色底 + 细描边，比纯色块更有层次</summary>
    private void DrawBackground(IntPtr hdc, int w, int h)
    {
        var v = new TRIVERTEX[2];
        v[0].X = 0; v[0].Y = 0;
        v[0].Red = 0x2B00; v[0].Green = 0x3100; v[0].Blue = 0x3A00;
        v[1].X = w; v[1].Y = h;
        v[1].Red = 0x1700; v[1].Green = 0x1A00; v[1].Blue = 0x2100;
        var mesh = new[] { new GRADIENT_RECT { UpperLeft = 0, LowerRight = 1 } };
        if (!GradientFill(hdc, v, 2, mesh, 1, 1 /*GRADIENT_FILL_RECT_V*/))
        {
            var bg = CreateSolidBrush(Color(0x1A, 0x1D, 0x23));
            var old = SelectObject(hdc, bg);
            var r0 = Math.Max(6, Scaled(12));
            RoundRect(hdc, 0, 0, w, h, r0, r0);
            SelectObject(hdc, old);
            DeleteObject(bg);
            return;
        }

        // 只描边不填充（窗口区域已经把四角裁圆）
        var border = CreatePen(0, Math.Max(1, Scaled(1)), Color(0x3E, 0x46, 0x52));
        var hollow = GetStockObject(5); // NULL_BRUSH
        var oldBrush = SelectObject(hdc, hollow);
        var oldPen = SelectObject(hdc, border);
        var r = Math.Max(6, Scaled(12));
        RoundRect(hdc, 0, 0, w, h, r, r);
        SelectObject(hdc, oldBrush);
        SelectObject(hdc, oldPen);
        DeleteObject(border);
    }

    /// <summary>一块指标：左侧彩色竖条 + 小标题 + 亮色数值</summary>
    private int DrawChip(IntPtr hdc, int x, int width, string label, string value, uint accent)
    {
        DrawAccentBar(hdc, x, accent);
        var left = x + Scaled(15);
        SelectObject(hdc, _fontLabel);
        SetTextColor(hdc, Color(0x86, 0x93, 0xA6));
        TextOutW(hdc, left, Scaled(5), label, label.Length);

        SelectObject(hdc, _fontValue);
        SetTextColor(hdc, Color(0xF4, 0xF7, 0xFB));
        TextOutW(hdc, left, Scaled(17), value, value.Length);
        return x + width;
    }

    /// <summary>FPS 块：放大的帧率数字 + 脚下小号帧延迟</summary>
    private int DrawFpsChip(IntPtr hdc, int x, int width, uint accent)
    {
        DrawAccentBar(hdc, x, accent);
        var left = x + Scaled(15);
        var snap = _snapshot ?? new MonitorSnapshot();

        SelectObject(hdc, _fontFps);
        SetTextColor(hdc, Color(0xF4, 0xF7, 0xFB));
        var fps = FormatFps(snap);
        TextOutW(hdc, left, Scaled(3), fps, fps.Length);

        // 数字后面带个小号单位 FPS
        var extent = new SIZE();
        if (GetTextExtentPoint32W(hdc, fps, fps.Length, out extent))
        {
            SelectObject(hdc, _fontLabel);
            SetTextColor(hdc, Color(0x9A, 0xA6, 0xB6));
            TextOutW(hdc, left + extent.CX + Scaled(3), Scaled(10), "FPS", 3);
        }

        SelectObject(hdc, _fontLabel);
        SetTextColor(hdc, Color(0x9A, 0xA6, 0xB6));
        var delay = FormatFrameDelay(snap);
        TextOutW(hdc, left, Scaled(30), delay, delay.Length);
        return x + width;
    }

    /// <summary>指标块左侧的彩色圆角竖条（颜色区分指标，替代生硬的分隔线）</summary>
    private void DrawAccentBar(IntPtr hdc, int x, uint accent)
    {
        var barW = Math.Max(2, Scaled(3));
        var bx = x + Scaled(6);
        var brush = CreateSolidBrush(accent);
        var pen = CreatePen(0, Math.Max(1, Scaled(1)), accent);
        var oldBrush = SelectObject(hdc, brush);
        var oldPen = SelectObject(hdc, pen);
        RoundRect(hdc, bx, Scaled(11), bx + barW, Scaled(BarHeight - 11), barW, barW);
        SelectObject(hdc, oldBrush);
        SelectObject(hdc, oldPen);
        DeleteObject(pen);
        DeleteObject(brush);
    }

    /// <summary>帧波动折线：无标题，块内拉伸帧时间序列，淡基线 + 青色折线 + 末端圆点</summary>
    private int DrawGraph(IntPtr hdc, int x, int width, double[] history, uint accent)
    {
        var left = x + Scaled(2);
        var gx = left;
        var gy = Scaled(18);
        var gw = width - Scaled(6);
        var gh = Scaled(22);
        if (gw <= 4 || gh <= 4) return x + width;

        // 基线：给折线一个视觉落脚点
        var basePen = CreatePen(0, 1, Color(0x35, 0x3E, 0x49));
        var oldBase = SelectObject(hdc, basePen);
        MoveToEx(hdc, gx, gy + gh - 1, IntPtr.Zero);
        LineTo(hdc, gx + gw, gy + gh - 1);
        SelectObject(hdc, oldBase);
        DeleteObject(basePen);

        if (history.Length >= 2)
        {
            var max = 0.0;
            foreach (var v in history) if (v > max) max = v;
            var scaleMax = Math.Max(max, 33.3); // 至少按 30fps 量程，波动更有可读性
            var count = Math.Min(history.Length, 90);
            var start = history.Length - count;
            var pts = new POINT[count];
            for (var i = 0; i < count; i++)
            {
                var v = Math.Clamp(history[start + i], 0, scaleMax);
                var px = gx + gw * i / Math.Max(1, count - 1);
                var py = gy + gh - (int)(gh * v / scaleMax);
                pts[i] = new POINT { X = px, Y = py };
            }
            var pen = CreatePen(0, Math.Max(1, Scaled(2)), accent);
            var old = SelectObject(hdc, pen);
            Polyline(hdc, pts, pts.Length);
            SelectObject(hdc, old);
            DeleteObject(pen);

            // 末端圆点：标出最新一帧
            var last = pts[^1];
            var dot = Math.Max(2, Scaled(2));
            var brush = CreateSolidBrush(accent);
            var dotPen = CreatePen(0, Math.Max(1, Scaled(1)), accent);
            var oldBrush = SelectObject(hdc, brush);
            var oldPen = SelectObject(hdc, dotPen);
            RoundRect(hdc, last.X - dot, last.Y - dot, last.X + dot, last.Y + dot, dot * 2, dot * 2);
            SelectObject(hdc, oldBrush);
            SelectObject(hdc, oldPen);
            DeleteObject(dotPen);
            DeleteObject(brush);
        }
        else
        {
            SelectObject(hdc, _fontValue);
            SetTextColor(hdc, Color(0xF4, 0xF7, 0xFB));
            TextOutW(hdc, gx, Scaled(17), "—", 1);
        }
        return x + width;
    }

    // ---- 数值格式 ----

    private static string FormatFps(MonitorSnapshot s) =>
        s.Fps > 0 ? s.Fps.ToString("0") : "—";

    /// <summary>帧延迟：放在 FPS 数字脚下的小字；FPS 取不到时说明权限原因</summary>
    private static string FormatFrameDelay(MonitorSnapshot s)
    {
        if (s.FrameMs > 0) return $"{s.FrameMs:0.0}ms";
        return MonitorService.FpsAvailable ? "—" : "需管理员";
    }

    private static string FormatCpu(MonitorSnapshot s)
    {
        var freq = s.CpuMhz > 0 ? $"{s.CpuMhz / 1000.0:0.00}GHz" : "—";
        var temp = double.IsNaN(s.CpuTemp) ? "—" : $"{s.CpuTemp:0}°C";
        var power = double.IsNaN(s.CpuPower) ? "—" : $"{s.CpuPower:0.0}W";
        return $"{s.CpuUsage:0}% · {freq} · {temp} · {power}";
    }

    private static string FormatGpu(MonitorSnapshot s)
    {
        var usage = double.IsNaN(s.GpuUsage) ? "—" : $"{s.GpuUsage:0}%";
        var freq = double.IsNaN(s.GpuMhz) ? "—" : $"{s.GpuMhz:0}MHz";
        var power = double.IsNaN(s.GpuPower) ? "—" : $"{s.GpuPower:0.0}W";
        return $"{usage} · {freq} · {power}";
    }

    private static string FormatBattery(MonitorSnapshot s)
    {
        var pct = double.IsNaN(s.BatteryPercent) ? "" : $"{s.BatteryPercent:0}%";
        if (s.Charging)
        {
            var charging = double.IsNaN(s.BatteryPower) ? "充电中" : $"{s.BatteryPower:0.0}W 充电";
            return pct.Length > 0 ? $"{charging} · {pct}" : charging;
        }
        // 接电源且未充电（电池已满）：Windows 不提供功率，显示电量
        if (s.OnAc) return pct.Length > 0 ? $"接电源 · {pct}" : "接电源";
        var power = double.IsNaN(s.BatteryPower) ? "" : $"{s.BatteryPower:0.0}W";
        var time = double.IsNaN(s.BatteryMinutes)
            ? ""
            : $"{(int)(s.BatteryMinutes / 60)}h{(int)(s.BatteryMinutes % 60):00}m";
        var parts = new[] { power, time, pct }.Where(p => p.Length > 0).ToArray();
        return parts.Length > 0 ? string.Join(" · ", parts) : "—";
    }

    private static string FormatMemory(MonitorSnapshot s) => $"{s.MemPercent:0}%";

    private static uint Color(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    // ---- 窗口过程 ----

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint();
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_NCHITTEST:
                return new IntPtr(HTTRANSPARENT); // 鼠标穿透到下层游戏
            case WM_DPICHANGED:
                UpdateMetrics();
                CreateFonts();
                lock (_renderGate) { AdjustDynamicWidths(); }
                ApplyRoundedRegion();
                PlaceWindow();
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---- P/Invoke ----

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRIVERTEX
    {
        public int X;
        public int Y;
        public ushort Red;
        public ushort Green;
        public ushort Blue;
        public ushort Alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GRADIENT_RECT
    {
        public uint UpperLeft;
        public uint LowerRight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int CX;
        public int CY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size,
        IntPtr srcDc, ref POINT src, uint colorKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(int action, int param, out RECT rect, int winIni);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRgn, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectClipRgn(IntPtr hdc, IntPtr region);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("msimg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GradientFill(IntPtr hdc, [In] TRIVERTEX[] vertex, uint numVertex,
        [In] GRADIENT_RECT[] mesh, uint numMesh, uint mode);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int style, int width, uint color);

    [DllImport("gdi32.dll")]
    private static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int length);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out SIZE size);

    [DllImport("gdi32.dll")]
    private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    private static extern bool LineTo(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern bool Polyline(IntPtr hdc, POINT[] points, int count);
}
