using System.Runtime.InteropServices;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>
/// 性能监控悬浮条：独立 Win32 窗口（置顶 / 点击穿透 / 不抢焦点 / 圆角 / 背景全透明），
/// 按用户在监控磁贴编辑页选的位置（六选一）、大小、配色、常开与"显示项目"摆放，
/// 一排按勾选结果显示 FPS、帧延迟、帧波动线、CPU、GPU（含温度）、电池功耗、电量、内存。
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
    private const int SW_HIDE = 0;
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
    /// <summary>电池电量（百分比）块：只放"电量"标题 + 百分比</summary>
    private const int ChipSoc = 68;
    private const int ChipMemory = 58;
    /// <summary>当前时间块（时:分:秒）</summary>
    private const int ChipClock = 78;
    private const int PadH = 7;
    private const int BarHeight = 36;

    private IntPtr _hwnd;
    private IntPtr _instance;
    private double _scale = 1;
    private int _width;
    private int _batteryWidth;
    private int _fpsWidth;
    private int _cpuWidth;
    private int _gpuWidth;
    private int _socWidth;
    private int _memoryWidth;
    private int _clockWidth;
    private RECT _work;
    private int _x, _y;

    // 用户在监控磁贴编辑页选定的设置
    private MonitorPosition _position;
    private double _userScale;
    private MonitorMetric _metrics;
    private MonitorColorTheme _color = MonitorColorTheme.Default;
    private bool _alwaysOn;

    private readonly object _renderGate = new();

    private IntPtr _fontLabel;
    private IntPtr _fontValue;
    private IntPtr _fontTip;
    private IntPtr _fontFps;

    private MonitorSnapshot? _snapshot;
    private bool _visible = true;

    private static WndProcDelegate? _wndProc; // 防止回调被 GC
    private static bool _registered;

    public MonitorOverlayWindow(MonitorPosition position, double userScale, MonitorMetric metrics, MonitorColorTheme color, bool alwaysOn)
    {
        _position = position;
        _userScale = Math.Clamp(userScale, 0.6, 1.8);
        _metrics = metrics == MonitorMetric.None ? MonitorMetric.All : metrics;
        _color = color;
        _alwaysOn = alwaysOn;
    }

    /// <summary>该指标（组）是否勾选显示</summary>
    private bool Has(MonitorMetric m) => (_metrics & m) != 0;

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

            // 逐像素透明（背景全透明，文字/折线始终不透明）由 UpdateLayeredWindow 完成
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

    /// <summary>是否常开：常开时切出本软件也一直显示</summary>
    public bool AlwaysOn => _alwaysOn;

    /// <summary>应用新的位置 / 大小 / 配色 / 常开（编辑页保存后实时生效）</summary>
    public void ApplySettings(MonitorPosition position, double userScale, MonitorMetric metrics, MonitorColorTheme color, bool alwaysOn)
    {
        _position = position;
        _userScale = Math.Clamp(userScale, 0.6, 1.8);
        _metrics = metrics == MonitorMetric.None ? MonitorMetric.All : metrics;
        _color = color;
        _alwaysOn = alwaysOn;
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            UpdateMetrics();
            CreateFonts();
            lock (_renderGate) { AdjustDynamicWidths(); FitToWorkArea(); }
            ApplyRoundedRegion();
            PlaceWindow();
            if (_alwaysOn && !_visible)
            {
                _visible = true;
                ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                ReassertTopmost();
            }
        }
        catch { }
    }

    /// <summary>背景透明度：半透明深色底片，保证在浅色 / 同色画面下文字依然清晰；圆角外仍完全透明</summary>
    private const byte BackgroundAlpha = 190;

    /// <summary>推送新数据并重绘（可从任意线程调用）</summary>
    public void Update(MonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (_hwnd == IntPtr.Zero || !_visible) return;
        try
        {
            lock (_renderGate)
            {
                if (AdjustDynamicWidths())
                {
                    FitToWorkArea();
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

    /// <summary>
    /// 主窗口切换前台/后台：后台且当前没有全屏游戏时隐藏悬浮条，
    /// 节省 GPU 更新开销；全屏游戏里即使主窗口在后台也保持显示。
    /// 勾选"常开"时无论前台后台一律显示。
    /// </summary>
    public void SetForeground(bool appForeground, bool fullscreenVisible)
    {
        var shouldShow = _alwaysOn || appForeground || fullscreenVisible;
        if (_hwnd == IntPtr.Zero || shouldShow == _visible) return;
        _visible = shouldShow;
        try
        {
            ShowWindow(_hwnd, shouldShow ? SW_SHOWNOACTIVATE : SW_HIDE);
            if (shouldShow)
            {
                PlaceWindow(); // 重新定位（主窗口可能被拖过）
                ReassertTopmost();
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

    /// <summary>固定宽度的块（间距 + 帧波动线）之和，其余块宽度按文字动态测量</summary>
    private int FixedChipsWidth() =>
        PadH * 2
        + (Has(MonitorMetric.FrameGraph) ? ChipGraph : 0);

    /// <summary>全部块的当前总宽度（固定块 + 各动态块）</summary>
    private int TotalWidth() =>
        Scaled(FixedChipsWidth()) + _fpsWidth + _cpuWidth + _gpuWidth + _batteryWidth + _socWidth + _memoryWidth + _clockWidth;

    /// <summary>按字体测一段文字的像素宽度</summary>
    private int MeasureText(IntPtr hdc, IntPtr font, string text, out int width)
    {
        var old = SelectObject(hdc, font);
        var ok = GetTextExtentPoint32W(hdc, text, text.Length, out var size);
        SelectObject(hdc, old);
        width = size.CX;
        return ok ? 0 : -1;
    }

    /// <summary>FPS 块按实际帧率文字宽度收紧；未勾选时宽度为 0（整块不占位）</summary>
    private int MeasureFpsWidth()
    {
        if (!Has(MonitorMetric.Fps | MonitorMetric.FrameTime)) return 0;
        if (_fontFps == IntPtr.Zero || _fontLabel == IntPtr.Zero) return Scaled(ChipFps);
        var snap = _snapshot ?? new MonitorSnapshot();
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return Scaled(ChipFps);
        try
        {
            if (!Has(MonitorMetric.Fps))
            {
                // 只勾了"帧延迟"：按普通指标块（标题 + 数值）测宽
                if (MeasureText(hdc, _fontLabel, "帧延迟", out var labelW) != 0) return Scaled(ChipFps);
                var onlyDelay = FormatFrameDelay(snap);
                if (MeasureText(hdc, _fontValue, onlyDelay, out var valueW) != 0) return Scaled(ChipFps);
                return Math.Clamp(Scaled(15) + Math.Max(labelW, valueW) + Scaled(10), Scaled(52), Scaled(ChipFps));
            }

            if (MeasureText(hdc, _fontFps, FormatFps(snap), out var fpsW) != 0) return Scaled(ChipFps);
            if (MeasureText(hdc, _fontLabel, "FPS", out var unitW) != 0) return Scaled(ChipFps);
            var content = fpsW + Scaled(3) + unitW;
            if (Has(MonitorMetric.FrameTime))
            {
                if (MeasureText(hdc, _fontLabel, FormatFrameDelay(snap), out var delayW) != 0) return Scaled(ChipFps);
                content = Math.Max(content, delayW);
            }
            return Math.Max(Scaled(15) + content + Scaled(6), Scaled(52));
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    /// <summary>电池块按实际文字宽度收紧，让后面的内存块贴上来；未勾选时宽度为 0</summary>
    private int MeasureBatteryWidth()
    {
        if (!Has(MonitorMetric.Battery)) return 0;
        if (_fontValue == IntPtr.Zero) return Scaled(ChipBattery);
        var text = FormatBattery(_snapshot ?? new MonitorSnapshot());
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return Scaled(ChipBattery);
        try
        {
            if (MeasureText(hdc, _fontValue, text, out var size) != 0) return Scaled(ChipBattery);
            return Math.Max(Scaled(15) + size + Scaled(12), Scaled(96));
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    /// <summary>动态测宽：标题/数值取较长者；max 传 0 表示不设上限（文字更长时块自动加宽，避免压到下一位）；未勾选时宽度为 0</summary>
    private int MeasureLabelValueWidth(string label, string value, int min, int max)
    {
        if (_fontLabel == IntPtr.Zero || _fontValue == IntPtr.Zero) return Scaled(max > 0 ? max : min);
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return Scaled(max > 0 ? max : min);
        try
        {
            if (MeasureText(hdc, _fontLabel, label, out var labelW) != 0) return Scaled(max > 0 ? max : min);
            if (MeasureText(hdc, _fontValue, value, out var valueW) != 0) return Scaled(max > 0 ? max : min);
            var w = Scaled(15) + Math.Max(labelW, valueW) + Scaled(10);
            return max > 0 ? Math.Clamp(w, Scaled(min), Scaled(max)) : Math.Max(w, Scaled(min));
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    /// <summary>CPU 块按实际标题/数值宽度收紧；未勾选时宽度为 0</summary>
    private int MeasureCpuWidth()
    {
        if (!Has(MonitorMetric.CpuAny)) return 0;
        var snap = _snapshot ?? new MonitorSnapshot();
        return MeasureLabelValueWidth(CpuLabel(), CpuValue(snap), 104, 0);
    }

    /// <summary>GPU 块按实际标题/数值宽度收紧（无功耗时标题更短，后面的电池块贴上来）；未勾选时宽度为 0</summary>
    private int MeasureGpuWidth()
    {
        if (!Has(MonitorMetric.GpuAny)) return 0;
        var snap = _snapshot ?? new MonitorSnapshot();
        return MeasureLabelValueWidth(GpuLabel(snap), FormatGpu(snap), 104, 0);
    }

    /// <summary>电量块按实际文字宽度收紧；未勾选时宽度为 0</summary>
    private int MeasureSocWidth()
    {
        if (!Has(MonitorMetric.BatteryPercent)) return 0;
        var snap = _snapshot ?? new MonitorSnapshot();
        return MeasureLabelValueWidth("电量", FormatSoc(snap), 46, 0);
    }

    /// <summary>内存块（内存占用 · 显存占用）按实际文字宽度收紧；两项都未勾选时宽度为 0</summary>
    private int MeasureMemoryWidth()
    {
        if (!HasMemoryBlock()) return 0;
        var snap = _snapshot ?? new MonitorSnapshot();
        return MeasureLabelValueWidth(MemoryLabel(), MemoryValue(snap), 46, 0);
    }

    /// <summary>内存块是否显示：内存看占用率、显存看已用量，勾了任一项就显示</summary>
    private bool HasMemoryBlock() => Has(MonitorMetric.Memory | MonitorMetric.GpuVram);

    /// <summary>内存块标题：两项都勾时合成为一个块</summary>
    private string MemoryLabel()
    {
        var mem = Has(MonitorMetric.Memory);
        var vram = Has(MonitorMetric.GpuVram);
        if (mem && vram) return "内存 · 显存";
        return mem ? "内存" : "显存";
    }

    /// <summary>内存块数值：内存百分比与显存 GB 用 · 连接，取不到的项单独显示 —</summary>
    private string MemoryValue(MonitorSnapshot s)
    {
        var parts = new List<string>();
        if (Has(MonitorMetric.Memory)) parts.Add(double.IsNaN(s.MemPercent) ? "—" : $"{s.MemPercent:0}%");
        if (Has(MonitorMetric.GpuVram)) parts.Add(double.IsNaN(s.GpuVram) ? "—" : $"{s.GpuVram:0.#}G");
        return string.Join(" · ", parts);
    }

    /// <summary>当前时间块按文字宽度收紧（固定 8 字符）；未勾选时宽度为 0</summary>
    private int MeasureClockWidth()
    {
        if (!Has(MonitorMetric.Clock)) return 0;
        return MeasureLabelValueWidth("时间", ClockText(), 56, 0);
    }

    /// <summary>当前时间文本：24 小时制 时:分:秒</summary>
    private static string ClockText() => DateTime.Now.ToString("HH:mm:ss");

    /// <summary>按实际文字重新计算 FPS / CPU / GPU / 电池 / 电量 / 内存块宽度；变化明显时返回 true（调用方负责重设区域与位置）</summary>
    private bool AdjustDynamicWidths()
    {
        var fps = MeasureFpsWidth();
        var cpu = MeasureCpuWidth();
        var gpu = MeasureGpuWidth();
        var battery = MeasureBatteryWidth();
        var soc = MeasureSocWidth();
        var memory = MeasureMemoryWidth();
        var clock = MeasureClockWidth();
        var changed = false;
        if (Math.Abs(fps - _fpsWidth) >= 4) { _fpsWidth = fps; changed = true; }
        if (Math.Abs(cpu - _cpuWidth) >= 4) { _cpuWidth = cpu; changed = true; }
        if (Math.Abs(gpu - _gpuWidth) >= 4) { _gpuWidth = gpu; changed = true; }
        if (Math.Abs(battery - _batteryWidth) >= 4) { _batteryWidth = battery; changed = true; }
        if (Math.Abs(soc - _socWidth) >= 4) { _socWidth = soc; changed = true; }
        if (Math.Abs(memory - _memoryWidth) >= 4) { _memoryWidth = memory; changed = true; }
        if (Math.Abs(clock - _clockWidth) >= 4) { _clockWidth = clock; changed = true; }
        if (!changed) return false;
        _width = TotalWidth();
        return true;
    }

    /// <summary>
    /// 按工作区宽度兜底：文字很长（例如 CPU/GPU 四项全开）或大字号时整体等比缩小一次，
    /// 保证悬浮条始终完整落在屏幕内，不会被裁掉一截。
    /// </summary>
    private void FitToWorkArea()
    {
        var workW = _work.Right - _work.Left;
        var margin = Math.Max(4, Scaled(10));
        var avail = workW - margin * 2;
        if (avail <= 0 || _width <= avail) return;
        var factor = (double)avail / _width;
        if (factor >= 0.999) return;

        _scale = Math.Max(0.45, _scale * factor);
        CreateFonts();
        _batteryWidth = Has(MonitorMetric.Battery) ? Scaled(ChipBattery) : 0;
        _fpsWidth = Has(MonitorMetric.Fps | MonitorMetric.FrameTime) ? Scaled(ChipFps) : 0;
        _cpuWidth = Has(MonitorMetric.CpuAny) ? Scaled(ChipCpu) : 0;
        _gpuWidth = Has(MonitorMetric.GpuAny) ? Scaled(ChipGpu) : 0;
        _socWidth = Has(MonitorMetric.BatteryPercent) ? Scaled(ChipSoc) : 0;
        _memoryWidth = HasMemoryBlock() ? Scaled(ChipMemory) : 0;
        _clockWidth = Has(MonitorMetric.Clock) ? Scaled(ChipClock) : 0;
        _width = TotalWidth();
        AdjustDynamicWidths();
    }

    private int Scaled(int v) => (int)Math.Round(v * _scale);

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
    /// 只按用户设定的百分比缩放（1.0 = 100%），不随屏幕分辨率 / DPI 自动变化，
    /// 保证同一比例在不同分辨率下显示一致；仅当悬浮条总宽超出工作区时由
    /// <see cref="FitToWorkArea"/> 兜底等比缩小。
    /// </summary>
    private void UpdateMetrics()
    {
        if (!SystemParametersInfoW(SPI_GETWORKAREA, 0, out _work, 0))
            _work = new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
        _scale = Math.Clamp(_userScale, 0.7, 2.0);
        _batteryWidth = Has(MonitorMetric.Battery) ? Scaled(ChipBattery) : 0;
        _fpsWidth = Has(MonitorMetric.Fps | MonitorMetric.FrameTime) ? Scaled(ChipFps) : 0;
        _cpuWidth = Has(MonitorMetric.CpuAny) ? Scaled(ChipCpu) : 0;
        _gpuWidth = Has(MonitorMetric.GpuAny) ? Scaled(ChipGpu) : 0;
        _socWidth = Has(MonitorMetric.BatteryPercent) ? Scaled(ChipSoc) : 0;
        _memoryWidth = HasMemoryBlock() ? Scaled(ChipMemory) : 0;
        _clockWidth = Has(MonitorMetric.Clock) ? Scaled(ChipClock) : 0;
        _width = TotalWidth();
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
    /// 背景固定全透明（只用来区分哪些像素属于前景），文字/折线/彩条保持 100% 不透明，
    /// 因此悬浮条没有外框和底色，只剩清晰文字与配色。
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

            var bgAlpha = BackgroundAlpha;
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

    /// <summary>一组指标配色（组合色）：每种方案给各数据块分配不同颜色</summary>
    private readonly record struct MonitorPalette(
        uint Fps, uint Graph, uint Cpu, uint Gpu, uint Battery, uint Soc, uint Memory, uint Clock);

    private MonitorPalette Palette => _color switch
    {
        MonitorColorTheme.Neon => new(
            Color(0xFF, 0x5C, 0x8A), Color(0xFF, 0xD1, 0x66), Color(0x4C, 0xFF, 0xD5),
            Color(0xB9, 0x83, 0xFF), Color(0xFF, 0x8C, 0x42), Color(0xFF, 0xB8, 0x6B),
            Color(0x4C, 0xC2, 0xFF), Color(0x7C, 0xFF, 0xE8)),
        MonitorColorTheme.Ice => new(
            Color(0x7F, 0xE7, 0xFF), Color(0x4C, 0xC2, 0xFF), Color(0x6B, 0xB6, 0xFF),
            Color(0x8F, 0xB3, 0xFF), Color(0x9B, 0xE0, 0xFF), Color(0xB3, 0xE9, 0xFF),
            Color(0x6F, 0xE0, 0xD8), Color(0xC9, 0xE9, 0xFF)),
        MonitorColorTheme.Sunset => new(
            Color(0xFF, 0xD1, 0x66), Color(0xFF, 0x9F, 0x5A), Color(0xFF, 0xB8, 0x6B),
            Color(0xFF, 0x8C, 0x42), Color(0xFF, 0xE0, 0xA3), Color(0xFF, 0xC4, 0x6B),
            Color(0xFF, 0xA0, 0x7A), Color(0xFF, 0xD9, 0xA0)),
        MonitorColorTheme.Aurora => new(
            Color(0x7C, 0xF2, 0xC6), Color(0x6F, 0xA8, 0xFF), Color(0xA4, 0x8C, 0xFF),
            Color(0xFF, 0x8A, 0xD4), Color(0xFF, 0xD1, 0x66), Color(0x8F, 0xE9, 0xFF),
            Color(0x57, 0xE0, 0xC9), Color(0xD9, 0xE4, 0xFF)),
        MonitorColorTheme.Mono => new(
            Color(0xFF, 0xFF, 0xFF), Color(0xE6, 0xE6, 0xE6), Color(0xFF, 0xFF, 0xFF),
            Color(0xE6, 0xE6, 0xE6), Color(0xFF, 0xFF, 0xFF), Color(0xE6, 0xE6, 0xE6),
            Color(0xFF, 0xFF, 0xFF), Color(0xFF, 0xFF, 0xFF)),
        _ => new(
            Color(0x6F, 0xE3, 0xA0), Color(0x4C, 0xC2, 0xFF), Color(0x6B, 0xB6, 0xFF),
            Color(0xA9, 0x9B, 0xFF), Color(0xFF, 0xC4, 0x6B), Color(0xFF, 0xD9, 0x7A),
            Color(0x6F, 0xE0, 0xD8), Color(0xBB, 0xD8, 0xFF))
    };

    private void DrawForeground(IntPtr hdc, int w, int h)
    {
        SetBkMode(hdc, 1); // TRANSPARENT

        var snap = _snapshot ?? new MonitorSnapshot();
        var p = Palette;
        var x = Scaled(PadH);

        if (Has(MonitorMetric.Fps | MonitorMetric.FrameTime))
            x = DrawFpsChip(hdc, x, _fpsWidth, p.Fps);
        if (Has(MonitorMetric.FrameGraph))
            x = DrawGraph(hdc, x, Scaled(ChipGraph), snap.FrameHistory, p.Graph);
        if (Has(MonitorMetric.CpuAny))
            x = DrawChip(hdc, x, _cpuWidth, CpuLabel(), CpuValue(snap), p.Cpu);
        if (Has(MonitorMetric.GpuAny))
            x = DrawChip(hdc, x, _gpuWidth, GpuLabel(snap), FormatGpu(snap), p.Gpu);
        // 内存 · 显存紧跟 GPU 块（在 GPU 分割线之后），电池类信息排在最后
        if (HasMemoryBlock())
            x = DrawChip(hdc, x, _memoryWidth, MemoryLabel(), MemoryValue(snap), p.Memory);
        if (Has(MonitorMetric.Battery))
            x = DrawChip(hdc, x, _batteryWidth, "电池功耗 · 预计", FormatBattery(snap), p.Battery);
        if (Has(MonitorMetric.BatteryPercent))
            x = DrawChip(hdc, x, _socWidth, "电量", FormatSoc(snap), p.Soc);
        if (Has(MonitorMetric.Clock))
            DrawChip(hdc, x, _clockWidth, "时间", ClockText(), p.Clock);
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

    /// <summary>一块指标：左侧彩色竖条 + 彩色小标题 + 彩色数值（同一指标色，标题略暗保留层次）</summary>
    private int DrawChip(IntPtr hdc, int x, int width, string label, string value, uint accent)
    {
        DrawAccentBar(hdc, x, accent);
        var left = x + Scaled(15);
        SelectObject(hdc, _fontLabel);
        SetTextColor(hdc, DimColor(accent, 0.78));
        TextOutW(hdc, left, Scaled(3), label, label.Length);

        SelectObject(hdc, _fontValue);
        SetTextColor(hdc, accent);
        TextOutW(hdc, left, Scaled(13), value, value.Length);
        return x + width;
    }

    /// <summary>FPS 块：放大的帧率数字 + 脚下小号帧延迟（只勾帧延迟时退化为普通指标块）</summary>
    private int DrawFpsChip(IntPtr hdc, int x, int width, uint accent)
    {
        var snap = _snapshot ?? new MonitorSnapshot();
        if (!Has(MonitorMetric.Fps))
            return DrawChip(hdc, x, width, "帧延迟", FormatFrameDelay(snap), accent);

        DrawAccentBar(hdc, x, accent);
        var left = x + Scaled(15);

        SelectObject(hdc, _fontFps);
        SetTextColor(hdc, accent);
        var fps = FormatFps(snap);
        TextOutW(hdc, left, Scaled(1), fps, fps.Length);

        // 数字后面带个小号单位 FPS
        var extent = new SIZE();
        if (GetTextExtentPoint32W(hdc, fps, fps.Length, out extent))
        {
            SelectObject(hdc, _fontLabel);
            SetTextColor(hdc, DimColor(accent, 0.78));
            TextOutW(hdc, left + extent.CX + Scaled(3), Scaled(7), "FPS", 3);
        }

        if (Has(MonitorMetric.FrameTime))
        {
            SelectObject(hdc, _fontLabel);
            SetTextColor(hdc, DimColor(accent, 0.86));
            var delay = FormatFrameDelay(snap);
            TextOutW(hdc, left, Scaled(23), delay, delay.Length);
        }
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
        RoundRect(hdc, bx, Scaled(7), bx + barW, Scaled(BarHeight - 7), barW, barW);
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
        var gy = Scaled(12);
        var gw = width - Scaled(6);
        var gh = Scaled(18);
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
            SetTextColor(hdc, accent);
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

    /// <summary>CPU 块标题：只保留勾选的项，例如只勾"使用/温度"时显示 "CPU 使用 · 温度"</summary>
    private string CpuLabel()
    {
        var parts = new List<string>();
        if (Has(MonitorMetric.CpuUsage)) parts.Add("使用");
        if (Has(MonitorMetric.CpuFreq)) parts.Add("频率");
        if (Has(MonitorMetric.CpuTemp)) parts.Add("温度");
        if (Has(MonitorMetric.CpuPower)) parts.Add("功耗");
        return "CPU " + string.Join(" · ", parts);
    }

    private string CpuValue(MonitorSnapshot s)
    {
        var parts = new List<string>();
        if (Has(MonitorMetric.CpuUsage)) parts.Add($"{s.CpuUsage:0}%");
        if (Has(MonitorMetric.CpuFreq)) parts.Add(s.CpuMhz > 0 ? $"{s.CpuMhz / 1000.0:0.00}GHz" : "—");
        if (Has(MonitorMetric.CpuTemp)) parts.Add(double.IsNaN(s.CpuTemp) ? "—" : $"{s.CpuTemp:0}°C");
        if (Has(MonitorMetric.CpuPower)) parts.Add(double.IsNaN(s.CpuPower) ? "—" : $"{s.CpuPower:0.0}W");
        return string.Join(" · ", parts);
    }

    /// <summary>GPU 块标题：只保留勾选的项（Intel 核显没有瓦特接口时不显示"功耗"）</summary>
    private string GpuLabel(MonitorSnapshot s)
    {
        var parts = new List<string>();
        if (Has(MonitorMetric.GpuUsage)) parts.Add("使用");
        if (Has(MonitorMetric.GpuFreq)) parts.Add("频率");
        if (Has(MonitorMetric.GpuPower) && !double.IsNaN(s.GpuPower)) parts.Add("功耗");
        if (Has(MonitorMetric.GpuTemp)) parts.Add("温度");
        return parts.Count == 0 ? "GPU" : "GPU " + string.Join(" · ", parts);
    }

    private string FormatGpu(MonitorSnapshot s)
    {
        // 频率读得到就显示，0 也照实显示 0MHz；取不到的项用 — 占位
        var parts = new List<string>();
        if (Has(MonitorMetric.GpuUsage)) parts.Add(double.IsNaN(s.GpuUsage) ? "—" : $"{s.GpuUsage:0}%");
        if (Has(MonitorMetric.GpuFreq)) parts.Add(double.IsNaN(s.GpuMhz) ? "—" : $"{s.GpuMhz:0}MHz");
        if (Has(MonitorMetric.GpuPower)) parts.Add(double.IsNaN(s.GpuPower) ? "—" : $"{s.GpuPower:0.0}W");
        if (Has(MonitorMetric.GpuTemp)) parts.Add(double.IsNaN(s.GpuTemp) ? "—" : $"{s.GpuTemp:0}°C");
        return string.Join(" · ", parts);
    }

    private static string FormatBattery(MonitorSnapshot s)
    {
        if (s.Charging)
            return double.IsNaN(s.BatteryPower) ? "充电中" : $"{s.BatteryPower:0.0}W 充电";
        // 接电源且未充电（电池已满）：Windows 不提供功率
        if (s.OnAc) return "接电源";
        var power = double.IsNaN(s.BatteryPower) ? "" : $"{s.BatteryPower:0.0}W";
        var time = double.IsNaN(s.BatteryMinutes)
            ? ""
            : $"{(int)(s.BatteryMinutes / 60)}h{(int)(s.BatteryMinutes % 60):00}m";
        var parts = new[] { power, time }.Where(p => p.Length > 0).ToArray();
        return parts.Length > 0 ? string.Join(" · ", parts) : "—";
    }

    /// <summary>电池电量（%）：无电池或读取失败时显示"—"</summary>
    private static string FormatSoc(MonitorSnapshot s) =>
        double.IsNaN(s.BatteryPercent) ? "—" : $"{s.BatteryPercent:0}%";

    private static uint Color(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    /// <summary>把配色按比例调暗（小标题 / 单位用同一种指标色，略暗以保留与数值的层次）</summary>
    private static uint DimColor(uint color, double factor)
    {
        var r = (int)((color & 0xFF) * factor);
        var g = (int)(((color >> 8) & 0xFF) * factor);
        var b = (int)(((color >> 16) & 0xFF) * factor);
        return (uint)(r | (g << 8) | (b << 16));
    }

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
                lock (_renderGate) { AdjustDynamicWidths(); FitToWorkArea(); }
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
