using System.Runtime.InteropServices;
using System.Text;
using GameLocker.Models;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GameLocker.Services;

/// <summary>
/// 屏幕翻译：按 A 隐藏启动器 → 右上角面板提示"点击要翻译的窗口" → 用户点击目标窗口锁定 →
/// 点面板「开始翻译」才截图 OCR → 逐行批量翻译 → 把译文覆盖在原文位置（替换原文）。
/// 「停止翻译」只撤掉译文、恢复原状，不退出；再按一次 A 才结束整个流程。窗口移动跟随，最小化/关闭自动结束。
/// </summary>
public static class ScreenTranslateService
{
    private const int PollMs = 150;
    private const int LockDelayMs = 400;
    private const int SelectTimeoutMs = 2500;
    private const int MinWidth = 320;
    private const int MinHeight = 180;

    private const int StageIdle = 0;
    private const int StageSelecting = 1;
    private const int StageShowing = 2;

    private static volatile bool _running;
    private static volatile int _stage = StageIdle;
    private static LaunchItem? _item;
    private static string _selectedEngine = "auto";
    private static string EffectiveEngine => _selectedEngine == "auto"
        ? (_item?.TranslateEngine == "azure" ? "azure" : "free") : _selectedEngine;
    private static Timer? _timer;
    private static long _seq;
    private static CancellationTokenSource? _translationCancellation;
    private static IntPtr _target;
    private static IntPtr _pending;
    private static long _pendingSince;
    private static bool _mouseDown;
    private static int _watchTicks;
    private static readonly uint _selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    /// <summary>当前是否处于翻译流程（等待选窗、已选中窗口等待开始，或正在显示译文）</summary>
    public static bool IsRunning => _running;

    /// <summary>是否正在翻译并显示译文（决定面板按钮是「停止翻译」还是「开始翻译」）</summary>
    private static bool IsTranslating => _stage == StageShowing;

    /// <summary>A 键开关：运行中则关闭，否则按当前设置开启</summary>
    public static bool Toggle(LaunchItem item)
    {
        if (IsRunning)
        {
            Stop();
            return false;
        }
        Start(item);
        return true;
    }

    public static void Start(LaunchItem item)
    {
        if (App.Main?.DispatcherQueue is { HasThreadAccess: false })
        {
            OnUi(() => Start(item));
            return;
        }
        // 已在流程中（编辑页保存了翻译设置）：只更新设置；正在翻译时用新设置原地重译
        if (_running && _target != IntPtr.Zero && IsLiveTarget(_target))
        {
            _item = item;
            if (_stage == StageShowing)
            {
                BeginTranslation();
            }
            return;
        }

        Stop();
        _item = item;
        _selectedEngine = "auto";
        Hud.SetActivity("尚未调用接口");
        _running = true;
        _stage = StageSelecting;
        _target = IntPtr.Zero;
        _pending = IntPtr.Zero;
        _pendingSince = 0;
        _watchTicks = 0;
        _mouseDown = LeftDown();

        CancelTranslation();
        OnUi(() =>
        {
            App.Main?.HideToTray();
            Hud.ShowPrompt("点击要翻译的窗口", "选中后点「开始翻译」· 按 A 关闭");
        });

        var timer = _timer;
        _timer = null;
        timer?.Dispose();
        Timer? pollTimer = null;
        pollTimer = new Timer(_ => OnUi(() =>
        {
            if (_running && ReferenceEquals(_timer, pollTimer)) Tick();
        }), null, PollMs, PollMs);
        _timer = pollTimer;
    }

    public static void Stop()
    {
        if (App.Main?.DispatcherQueue is { HasThreadAccess: false })
        {
            OnUi(Stop);
            return;
        }
        _running = false;
        _stage = StageIdle;
        _target = IntPtr.Zero;
        _pending = IntPtr.Zero;
        _item = null;
        CancelTranslation();
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
        OnUi(Hud.HideAll);
    }

    private static void Tick()
    {
        if (!_running) return;
        try
        {
            Hud.SyncPanelVisibility();
            if (App.Main?.IsShown == true)
            {
                Hud.SuppressOverlay();
                _pending = IntPtr.Zero;
                _pendingSince = 0;
                _mouseDown = LeftDown();
                return;
            }
            if (_stage == StageSelecting)
            {
                PollSelector();
            }
            else if (_stage == StageShowing && ++_watchTicks % 2 == 0) WatchTarget();
        }
        catch { /* 单次轮询失败不影响后续 */ }
    }

    /// <summary>选窗阶段：盯住前台窗口，用户点击某个窗口使其获得焦点后立刻锁定</summary>
    private static void PollSelector()
    {
        var hwnd = GetForegroundWindow();
        var down = LeftDown();
        var clicked = down && !_mouseDown;
        _mouseDown = down;

        if (!IsCandidate(hwnd))
        {
            _pending = IntPtr.Zero;
            _pendingSince = 0;
            return;
        }

        if (_pending == IntPtr.Zero)
        {
            // 隐藏启动器后系统会把前台交还给原来的窗口：先记为候选，等用户点击或超时后锁定
            _pending = hwnd;
            _pendingSince = Environment.TickCount64;
        }
        else if (_pending != hwnd)
        {
            Lock(hwnd); // 前台换成了另一个窗口 = 用户刚点了它
            return;
        }

        // 点控制面板上的「停止翻译 / 重新选取」不算选中窗口
        if ((clicked && !Hud.PanelContainsCursor()) ||
            Environment.TickCount64 - _pendingSince >= SelectTimeoutMs) Lock(hwnd);
    }

    /// <summary>可翻译的候选窗口：排除本进程、桌面/任务栏、最小化与过小的窗口</summary>
    private static bool IsCandidate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == Hud.OverlayHandle || hwnd == Hud.PanelHandle) return false;
        if (IsIconic(hwnd) || !IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _selfPid) return false;
        var cls = ClassNameOf(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return false;
        return rect.Right - rect.Left >= MinWidth && rect.Bottom - rect.Top >= MinHeight;
    }

    /// <summary>锁定目标窗口（只选中、不翻译，等用户点面板的「开始翻译」）</summary>
    private static void Lock(IntPtr hwnd)
    {
        if (!_running) return;
        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return;
        if (rect.Right - rect.Left < MinWidth || rect.Bottom - rect.Top < MinHeight) return;

        var changed = _target != hwnd;
        if (changed && _stage == StageShowing)
        {
            // 翻译中换了目标窗口：取消进行中的识别/翻译，撤掉旧译文，等用户再点开始
            CancelTranslation();
            _stage = StageSelecting;
            OnUi(Hud.HideOverlay);
        }
        _target = hwnd;
        _pending = IntPtr.Zero;
        _pendingSince = 0;
        _watchTicks = 0;
        if (!changed) return; // 同一个窗口：保留当前状态文案（例如"翻译失败"提示）
        OnUi(() =>
        {
            Hud.ShowPanel();
            Hud.SetStatus("已选中窗口", "点「开始翻译」开始翻译");
        });
    }

    private static void CancelTranslation()
    {
        Interlocked.Increment(ref _seq);
        var cancellation = _translationCancellation;
        _translationCancellation = null;
        cancellation?.Cancel();
    }

    private static void BeginTranslation()
    {
        CancelTranslation();
        if (!_running) return;
        var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        var seq = _seq;
        _stage = StageShowing;
        _watchTicks = 0;
        Hud.HideOverlay();
        Hud.SetActivity("仅发送固定测试短句，不发送屏幕内容");
        Hud.SetStatus("正在检查翻译接口…", "请稍候，可停止后重试");
        _ = RunTranslationAsync(seq, cancellation);
    }

    private static async Task RunTranslationAsync(long seq, CancellationTokenSource cancellation)
    {
        try
        {
            await TranslateTargetAsync(seq, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DebugLog.Write($"屏幕翻译: 任务失败 {ex.GetType().Name}");
            Retry(seq, "翻译失败", "识别或翻译异常，可点「开始翻译」重试");
        }
        finally
        {
            if (ReferenceEquals(_translationCancellation, cancellation)) _translationCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>截图 → OCR → 批量翻译 → 在 UI 线程重建覆盖层</summary>
    private static async Task TranslateTargetAsync(long seq, CancellationToken token)
    {
        await Task.Delay(LockDelayMs, token);
        if (seq != _seq || !_running) return;

        var item = _item;
        var hwnd = _target;
        if (item is null || hwnd == IntPtr.Zero) return;
        if (!IsLiveTarget(hwnd))
        {
            OnUi(seq, Stop);
            return;
        }

        var engine = EffectiveEngine;
        var apiKey = item.TranslateApiKey;
        var region = item.TranslateRegion;
        var source = item.TranslateSource;
        var target = item.TranslateTarget;
        var preflight = await TranslateService.PreflightAsync(engine, apiKey, region, token,
            result => OnUi(seq, () => Hud.SetActivity(
                $"检查 {TranslateService.EngineName(result.Engine)}：{result.Reason}")));
        token.ThrowIfCancellationRequested();
        if (seq != _seq || !_running) return;
        if (!preflight.Success)
        {
            var reasons = string.Join("；", preflight.Sources.Select(s =>
                $"{TranslateService.EngineName(s.Engine)}：{s.Reason}"));
            OnUi(seq, () => Hud.SetActivity(reasons));
            Retry(seq, "翻译接口检查失败", preflight.Sources.First().Reason + "，可点「开始翻译」重试");
            return;
        }
        OnUi(seq, () =>
        {
            Hud.SetStatus("正在识别文字…", "接口检查通过");
            Hud.SetActivity("可用：" + string.Join("、", preflight.AvailableSources.Select(TranslateService.EngineName)));
        });

        RECT rect;
        if (!GetWindowRect(hwnd, out rect))
        {
            Retry(seq, "无法读取窗口位置", "可点「开始翻译」重试");
            return;
        }
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w < MinWidth || h < MinHeight)
        {
            Retry(seq, "窗口尺寸过小", "请调整窗口大小后重试");
            return;
        }

        token.ThrowIfCancellationRequested();
        var cap = await Task.Run(() => Capture(hwnd, w, h), token);
        token.ThrowIfCancellationRequested();
        if (seq != _seq || !_running) return;
        if (cap is null)
        {
            DebugLog.Write($"屏幕翻译: 截图失败 {w}x{h}");
            Retry(seq, "窗口捕获失败（部分游戏不支持）", "可重试；不会改为截取桌面");
            return;
        }

        token.ThrowIfCancellationRequested();
        var lines = await OcrAsync(cap, w, h, source, token);
        if (seq != _seq || !_running) return;
        if (lines.Count == 0)
        {
            DebugLog.Write($"屏幕翻译: OCR 未识别到文字 {w}x{h}");
            Retry(seq, "未识别到文字", "部分游戏不支持窗口捕获，可重试或换窗");
            return;
        }

        // 目标为中文时，纯中文/数字行没必要再翻：省免费额度，也避免把已经是中文的内容再盖一遍
        var candidates = new List<TextLine>(lines.Count);
        var payload = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            if (target == "zh" && !NeedsTranslation(l.Text)) continue;
            candidates.Add(l);
            payload.Add(l.Text);
        }
        if (payload.Count == 0)
        {
            Retry(seq, "没有可翻译的文字", "该窗口的文字已经是目标语言");
            return;
        }

        OnUi(seq, () => Hud.SetStatus($"正在翻译 {payload.Count} 行…", "在线翻译中，请稍候"));
        token.ThrowIfCancellationRequested();
        if (seq != _seq || !_running) return;

        var translated = await TranslateService.TranslateLinesAsync(
            payload, engine, apiKey, region,
            source, target, token,
            activity => OnUi(seq, () => Hud.SetActivity(activity)), preflight.AvailableSources);
        if (seq != _seq || !_running) return;

        var ok = translated.Count(t => !string.IsNullOrWhiteSpace(t));
        DebugLog.Write($"屏幕翻译: OCR {lines.Count} 行, 待译 {payload.Count} 行, 成功 {ok} 行, 引擎={engine}");
        if (ok == 0)
        {
            Retry(seq, "翻译失败", "在线翻译不可用，可在编辑页填入 Azure 密钥");
            return;
        }

        var drawn = new List<OverlayLine>(candidates.Count);
        for (var i = 0; i < candidates.Count && i < translated.Count; i++)
        {
            var text = translated[i]?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var line = candidates[i];
            var x = Math.Max(0, line.X - 5);
            var y = Math.Max(0, line.Y - 2);
            var right = Math.Min(w, line.X + line.W + 5);
            var bottom = Math.Min(h, line.Y + line.H + 2);
            var bw = right - x;
            var bh = bottom - y;
            if (bw <= 2 || bh <= 2) continue;

            var bg = SampleBackground(cap.Pixels, w, h, x, y, bw, bh);
            var fg = ContrastText(bg);
            drawn.Add(new OverlayLine(text, x, y, bw, bh, bg, fg));
        }

        if (drawn.Count == 0)
        {
            Retry(seq, "翻译失败", "检查翻译引擎设置后重试");
            return;
        }

        var rectCopy = rect;
        OnUi(seq, () =>
        {
            Hud.ShowOverlay(drawn, rectCopy);
            Hud.SetStatus($"已翻译 {drawn.Count} 行", "点「停止翻译」恢复原状");
        });
    }

    /// <summary>识别/翻译失败：撤掉译文、停留在原窗口，用户可再点「开始翻译」重试</summary>
    private static void Retry(long seq, string title, string sub)
    {
        if (App.Main?.DispatcherQueue is { HasThreadAccess: false })
        {
            OnUi(seq, () => Retry(seq, title, sub));
            return;
        }
        if (!_running || seq != _seq) return;
        CancelTranslation();
        _stage = StageSelecting;
        _pending = _target;
        _pendingSince = Environment.TickCount64;
        _mouseDown = LeftDown();
        OnUi(() =>
        {
            Hud.HideOverlay();
            Hud.ShowPanel();
            Hud.SetStatus(title, sub);
        });
    }

    /// <summary>面板「开始翻译」：截图 → OCR → 翻译，并把译文覆盖在原文上</summary>
    private static void StartTranslating()
    {
        if (!_running) return;
        var hwnd = _target;
        if (hwnd == IntPtr.Zero || !IsLiveTarget(hwnd))
        {
            _target = IntPtr.Zero;
            OnUi(() =>
            {
                Hud.ShowPanel();
                Hud.SetStatus("请先点击要翻译的窗口", "选中窗口后再点「开始翻译」");
            });
            return;
        }

        BeginTranslation();
    }

    /// <summary>面板「停止翻译」：只撤掉译文恢复原状，流程与面板保持，可再次开始</summary>
    private static void StopTranslating()
    {
        if (!_running) return;
        CancelTranslation();
        _stage = StageSelecting;
        _pending = _target;
        _pendingSince = Environment.TickCount64;
        _mouseDown = LeftDown();
        OnUi(() =>
        {
            Hud.HideOverlay();
            Hud.ShowPanel();
            Hud.SetStatus("已停止翻译", "点「开始翻译」可再次翻译");
        });
    }

    /// <summary>目标窗口看门狗：关闭/最小化则停止；被启动器遮住时临时隐藏；移动时跟随</summary>
    private static void WatchTarget()
    {
        if (!_running || _stage != StageShowing) return;
        var hwnd = _target;
        if (!IsLiveTarget(hwnd))
        {
            OnUi(Stop);
            return;
        }

        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return;
        if (rect.Right - rect.Left < MinWidth || rect.Bottom - rect.Top < MinHeight)
        {
            OnUi(Stop);
            return;
        }

        var suppressed = !CanShowOverlay();
        var copy = rect;
        OnUi(() => Hud.Follow(copy, suppressed));
    }

    private static bool CanShowOverlay()
    {
        if (!_running || _stage != StageShowing || App.Main?.IsShown == true) return false;
        var foreground = GetForegroundWindow();
        return IsLiveTarget(_target) &&
            (foreground == _target || (Hud.PanelHandle != IntPtr.Zero && foreground == Hud.PanelHandle));
    }

    private static bool IsLiveTarget(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && !IsIconic(hwnd);

    /// <summary>悬浮窗的创建与绘制必须回到有消息循环的 UI 线程，否则点击/重绘消息不会被处理</summary>
    private static void OnUi(Action action)
    {
        var queue = App.Main?.DispatcherQueue;
        if (queue is null) return;
        if (queue.HasThreadAccess) action();
        else
        {
            var seq = _seq;
            var running = _running;
            queue.TryEnqueue(() =>
            {
                if (seq == _seq && running == _running) action();
            });
        }
    }

    private static void OnUi(long seq, Action action) => OnUi(() =>
    {
        if (_running && seq == _seq) action();
    });

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool LeftDown()
    {
        try { return (GetAsyncKeyState(0x01) & 0x8000) != 0; }
        catch { return false; }
    }

    private sealed class Captured
    {
        public required byte[] Bmp;      // 预处理后的 32 位 BMP（供 OCR）
        public required byte[] Pixels;   // 原始 BGRA 像素（供按行采样背景色，自下而上）
    }

    private sealed record TextLine(string Text, int X, int Y, int W, int H);

    private sealed record OverlayLine(string Text, int X, int Y, int W, int H, uint Bg, uint Fg);

    private static Captured? Capture(IntPtr hwnd, int w, int h)
    {
        IntPtr windowDc = IntPtr.Zero, memDc = IntPtr.Zero, hbmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            windowDc = GetDC(hwnd);
            if (windowDc == IntPtr.Zero) return null;
            memDc = CreateCompatibleDC(windowDc);
            if (memDc == IntPtr.Zero) return null;
            hbmp = CreateCompatibleBitmap(windowDc, w, h);
            if (hbmp == IntPtr.Zero) return null;
            old = SelectObject(memDc, hbmp);
            if (old == IntPtr.Zero || old == new IntPtr(-1))
            {
                old = IntPtr.Zero;
                return null;
            }
            // 只要求目标窗口自行绘制，绝不从桌面复制，避免采集遮挡窗口内容。
            const uint PW_RENDERFULLCONTENT = 2;
            if (!PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT)) return null;
            var selected = SelectObject(memDc, old);
            if (selected == IntPtr.Zero || selected == new IntPtr(-1)) return null;
            old = IntPtr.Zero;

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = h, // 正数 = 自下而上，正好是 BMP 的存储顺序
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };
            var pixels = new byte[w * h * 4];
            if (GetDIBits(memDc, hbmp, 0, (uint)h, pixels, ref info, 0) != h) return null;

            // OCR 用的副本做暗底反相/灰度拉伸，保留原始像素用于覆盖层取色
            var processed = (byte[])pixels.Clone();
            Preprocess(processed);

            // 包成 32 位 BMP，交给 BitmapDecoder 解码
            var bmp = new byte[54 + processed.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            WriteInt(bmp, 2, bmp.Length);
            WriteInt(bmp, 10, 54);
            WriteInt(bmp, 14, 40);
            WriteInt(bmp, 18, w);
            WriteInt(bmp, 22, h);
            bmp[26] = 1;
            bmp[28] = 32;
            WriteInt(bmp, 34, processed.Length);
            System.Buffer.BlockCopy(processed, 0, bmp, 54, processed.Length);
            return new Captured { Bmp = bmp, Pixels = pixels };
        }
        catch { return null; }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (windowDc != IntPtr.Zero) ReleaseDC(hwnd, windowDc);
        }
    }

    /// <summary>
    /// OCR 预处理（就地修改 BGRA 像素）：系统 OCR 对"深底浅字"识别率很低，先取出现次数最多的亮度作为背景色，
    /// 背景偏暗时反相成"浅底深字"；再把背景固定到白端、文字压到黑端，并转灰度去掉彩色干扰。
    /// </summary>
    private static void Preprocess(byte[] pixels)
    {
        var total = pixels.Length / 4;
        if (total <= 0) return;

        var hist = new int[256];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luma = (pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000;
            hist[luma]++;
        }

        var bg = 0;
        for (var v = 1; v < 256; v++)
            if (hist[v] > hist[bg]) bg = v;

        var lo = 0;
        var hi = 255;
        for (var v = 0; v < 256; v++) if (hist[v] > 0) { lo = v; break; }
        for (var v = 255; v >= 0; v--) if (hist[v] > 0) { hi = v; break; }

        var dark = bg < 110;                                  // 深底浅字
        var span = dark ? hi - bg : bg - lo;                   // 背景到文字的亮度距离
        if (span < 30) return;                                 // 接近纯色：拉伸只会放大噪点

        var lut = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            var mapped = dark ? 255 - (v - bg) * 255 / span : (v - lo) * 255 / span;
            lut[v] = (byte)(mapped < 0 ? 0 : mapped > 255 ? 255 : mapped);
        }

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luma = (pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000;
            var g = lut[luma];
            pixels[i] = g;
            pixels[i + 1] = g;
            pixels[i + 2] = g;
        }
    }

    private static void WriteInt(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static async Task<List<TextLine>> OcrAsync(Captured cap, int w, int h, string source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var engines = GetEngines(source);
        if (engines.Count == 0) return [];

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(cap.Bmp);
            await writer.StoreAsync().AsTask(token);
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);

        // 小窗口按 2 倍放大再识别：系统 OCR 对 12px 以下的文字几乎识别不出来；
        // 4K 这类大窗口本身字够大，放大反而拖慢
        var scale = w * h <= 3_000_000 ? 2u : 1u;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)w * scale,
            ScaledHeight = (uint)h * scale,
            InterpolationMode = BitmapInterpolationMode.Linear
        };
        // 屏幕截图的 alpha 通道无意义，必须用 Ignore：按预乘 alpha 解码会把像素当成全透明
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token);

        var best = new List<TextLine>();
        var bestScore = -1;
        var bestText = "";
        foreach (var (engine, chinese) in engines)
        {
            token.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(bitmap).AsTask(token);
            // 按"该引擎对应文字的数量"打分：中文引擎读英文界面会夹带大量空格和误识别汉字，
            // 直接比长度会让它永远胜出，必须按中文字数 / 英文字母数分别计分
            var score = Score(result.Text, chinese);
            if (score > bestScore || (score == bestScore && result.Text.Length > bestText.Length))
            {
                bestScore = score;
                bestText = result.Text;
                best = BuildLines(result, scale, w, h);
            }
        }
        return best;
    }

    /// <summary>把 OCR 的每行文字与其包围盒换算回截图原始像素坐标（放大过的要除以 scale）</summary>
    private static List<TextLine> BuildLines(OcrResult result, uint scale, int w, int h)
    {
        var list = new List<TextLine>();
        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0) continue;

            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (var word in line.Words)
            {
                var br = word.BoundingRect;
                if (br.Width <= 0 || br.Height <= 0) continue;
                l = Math.Min(l, (float)br.X);
                t = Math.Min(t, (float)br.Y);
                r = Math.Max(r, (float)(br.X + br.Width));
                b = Math.Max(b, (float)(br.Y + br.Height));
            }
            if (l == float.MaxValue) continue;

            var x = (int)Math.Floor(l / scale);
            var y = (int)Math.Floor(t / scale);
            var right = (int)Math.Ceiling(r / scale);
            var bottom = (int)Math.Ceiling(b / scale);
            x = Math.Max(0, x); y = Math.Max(0, y);
            right = Math.Min(w, right); bottom = Math.Min(h, bottom);
            if (right - x <= 4 || bottom - y <= 4) continue;

            list.Add(new TextLine(text, x, y, right - x, bottom - y));
        }
        return list;
    }

    /// <summary>目标为中文时粗判一行是否含英文：至少两个拉丁字母才值得送去翻译，纯中文/数字行直接跳过</summary>
    private static bool NeedsTranslation(string text)
    {
        var letters = 0;
        foreach (var c in text)
            if (char.IsAsciiLetter(c) && ++letters >= 2) return true;
        return false;
    }

    /// <summary>统计识别结果里属于该引擎语言的字符数（中文数汉字，英文数拉丁字母）</summary>
    private static int Score(string text, bool chinese)
    {
        var n = 0;
        foreach (var c in text)
        {
            if (chinese ? c is >= '\u4E00' and <= '\u9FFF' : char.IsAsciiLetter(c)) n++;
        }
        return n;
    }

    private static OcrEngine? _zhEngine, _enEngine, _jaEngine;

    /// <summary>
    /// 按源语言取 OCR 引擎（第二个值表示是否中文引擎）。"自动"时中英文引擎各识别一遍、取分数更高的一份：
    /// 只选一个时，中文引擎读英文界面、或英文引擎读中文界面都会大量漏字。
    /// </summary>
    private static List<(OcrEngine Engine, bool Chinese)> GetEngines(string source)
    {
        var list = new List<(OcrEngine, bool)>(2);
        try
        {
            switch (source)
            {
                case "zh":
                    if (Engine("zh-Hans-CN") is { } zh) list.Add((zh, true));
                    break;
                case "en":
                    if (Engine("en-US") is { } en) list.Add((en, false));
                    break;
                case "ja":
                    if (Engine("ja") is { } ja) list.Add((ja, false));
                    break;
                default:
                    if (Engine("zh-Hans-CN") is { } z) list.Add((z, true));
                    if (Engine("en-US") is { } e) list.Add((e, false));
                    break;
            }
            if (list.Count == 0 && OcrEngine.TryCreateFromUserProfileLanguages() is { } fallback)
                list.Add((fallback, false));
        }
        catch { }
        return list;
    }

    private static OcrEngine? Engine(string tag)
    {
        try
        {
            return tag switch
            {
                "en-US" => _enEngine ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")),
                "ja" => _jaEngine ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja")),
                _ => _zhEngine ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"))
            };
        }
        catch { return null; }
    }

    /// <summary>在原文行框（略微外扩）里取出现次数最多的颜色作为背景色，字形像素只占少数不会夺魁</summary>
    private static uint SampleBackground(byte[] pixels, int w, int h, int x, int y, int bw, int bh)
    {
        const int pad = 6;
        var x0 = Math.Max(0, x - pad);
        var y0 = Math.Max(0, y - pad);
        var x1 = Math.Min(w, x + bw + pad);
        var y1 = Math.Min(h, y + bh + pad);
        if (x1 <= x0 || y1 <= y0) return 0x00202020;

        var counts = new int[64];
        var sumR = new int[64];
        var sumG = new int[64];
        var sumB = new int[64];
        for (var yy = y0; yy < y1; yy++)
        {
            var row = (h - 1 - yy) * w; // 像素自下而上
            for (var xx = x0; xx < x1; xx++)
            {
                var i = (row + xx) * 4;
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var idx = ((r >> 6) << 4) | ((g >> 6) << 2) | (b >> 6);
                counts[idx]++;
                sumR[idx] += r; sumG[idx] += g; sumB[idx] += b;
            }
        }

        var best = 0;
        for (var i = 1; i < 64; i++) if (counts[i] > counts[best]) best = i;
        if (counts[best] == 0) return 0x00202020;

        var rr = sumR[best] / counts[best];
        var gg = sumG[best] / counts[best];
        var bb = sumB[best] / counts[best];
        return (uint)(rr | (gg << 8) | (bb << 16)); // COLORREF: 0x00BBGGRR
    }

    /// <summary>按背景亮度选黑/白文字，保证覆盖层文字始终清晰</summary>
    private static uint ContrastText(uint colorref)
    {
        var r = (int)(colorref & 0xFF);
        var g = (int)((colorref >> 8) & 0xFF);
        var b = (int)((colorref >> 16) & 0xFF);
        var luma = (r * 299 + g * 587 + b * 114) / 1000;
        return luma < 140 ? 0x00FFFFFFu : 0x00000000u;
    }

    /// <summary>译文覆盖层与右上角控制面板（同一窗口类，按 hwnd 区分绘制内容）</summary>
    private static class Hud
    {
        private const string ClassName = "GameLockerTranslateHud";
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_HIDE = 0;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int LWA_COLORKEY = 0x1;
        private const int LWA_ALPHA = 0x2;
        private const uint ColorKey = 0x00FF00FF; // 品红做透明色键
        private const int DT_SINGLELINE = 0x00000020;
        private const int DT_CENTER = 0x00000001;
        private const int DT_VCENTER = 0x00000004;
        private const int DT_NOPREFIX = 0x00000800;
        private const int DT_END_ELLIPSIS = 0x00008000;
        private const int TRANSPARENT_BK = 1;

        // 右上角常驻控制面板
        private const int PanelW = 356;
        private const int PanelH = 196;
        private const int DropdownExpand = 106;
        private const int DT_RIGHT = 0x00000002;
        private static bool _engineDropdownOpen;
        private static string _engineStatusSnapshot = "";
        private static int PanelHeight => PanelH + (_engineDropdownOpen ? DropdownExpand : 0);
        private static readonly string[] EngineChoices = ["auto", "youdao", "tencent", "bing", "mymemory", "azure"];
        private static string _activity = "尚未调用接口";
        private const int PanelMargin = 16;

        // 配色（COLORREF = 0x00BBGGRR，注意红蓝通道顺序相反）
        private const uint ColOutline = 0x00302D38;
        private const uint ColBackground = 0x00141319;
        private const uint ColDivider = 0x0028252F;
        private const uint ColText = 0x00F2F2F6;
        private const uint ColTextDim = 0x009696A1;
        private const uint ColAccent = 0x00E0A25A;
        private const uint ColAccentLine = 0x005A4A38;
        private const uint ColGreen = 0x0090D67C;
        private const uint ColAmber = 0x005CB2E0;
        private const uint ColRed = 0x006C6CE0;
        private const uint ColGrey = 0x00827A7A;
        private const uint ColStart = 0x00F08B4C;
        private const uint ColStartHot = 0x00FF9C5E;
        private const uint ColStop = 0x005C5CD7;
        private const uint ColStopHot = 0x006E6EE5;

        private static IntPtr _overlay, _panel;
        private static bool _registered;
        private static WndProcDelegate? _wndProc;
        private static string _status = "", _statusSub = "";
        private static List<OverlayLine> _lines = [];
        private static readonly object LineLock = new();
        private static bool _overlayVisible;
        private static bool _panelVisible;
        private static RECT _rect;
        private static int _pressed; // 1=底部按钮被按下 0=无
        private static int _hover;   // 与 _pressed 取值一致，表示鼠标悬停在哪个控件
        private static bool _trackingLeave;
        private static RECT _actionRect;
        private static double _scale = 1;

        /// <summary>按面板 DPI 缩放像素值（掌机 125%/150% 缩放下字体与按钮才不会被裁切）</summary>
        private static int Scaled(int value) => (int)Math.Round(value * _scale);

        public static IntPtr OverlayHandle => _overlay;
        public static IntPtr PanelHandle => _panel;

        private static void Create()
        {
            if (_overlay != IntPtr.Zero) return;
            var instance = GetModuleHandle(null);
            if (!_registered)
            {
                _wndProc = WndProc;
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = instance,
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = ClassName
                };
                RegisterClassExW(ref wc);
                _registered = true;
            }

            var ex = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            _overlay = CreateWindowExW(ex, ClassName, "GameLocker Translate Overlay", WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            // 控制面板要能接鼠标点击：去掉 WS_EX_TRANSPARENT，改用整窗透明度
            _panel = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
                ClassName, "GameLocker Translate Panel", WS_POPUP,
                0, 0, PanelW, PanelH, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            if (_overlay != IntPtr.Zero)
                SetLayeredWindowAttributes(_overlay, ColorKey, 255, LWA_COLORKEY);
            if (_panel != IntPtr.Zero)
            {
                var dpi = GetDpiForWindow(_panel);
                _scale = dpi > 0 ? dpi / 96.0 : 1;
                SetLayeredWindowAttributes(_panel, 0, 205, LWA_ALPHA);
            }
        }

        public static void ShowPrompt(string title, string sub)
        {
            Create();
            ShowPanel();
            SetStatus(title, sub);
        }

        /// <summary>覆盖层也是置顶窗口，重新显示会盖住面板：把它再提到最前，保证按钮始终可点</summary>
        public static void RaisePanel()
        {
            if (_panel != IntPtr.Zero && _panelVisible)
                SetWindowPos(_panel, TopMost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>右上角常驻面板：显示当前阶段提示与「停止 / 重新选取」两个按钮</summary>
        public static void ShowPanel()
        {
            Create();
            if (_panel == IntPtr.Zero || !_running || App.Main?.IsShown == true) return;
            if (_panelVisible) return;
            _panelVisible = true;
            ResizePanel();
            InvalidateRect(_panel, IntPtr.Zero, false);
        }

        private static void ResizePanel()
        {
            var pw = Scaled(PanelW);
            var ph = Scaled(PanelHeight);
            var margin = Scaled(PanelMargin);
            var x = Math.Max(margin, GetSystemMetrics(0) - pw - margin);
            SetWindowPos(_panel, TopMost, x, margin, pw, ph, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            // 裁成圆角，避免面板四角出现生硬的直角方块
            var radius = Scaled(12);
            var rgn = CreateRoundRectRgn(0, 0, pw + 1, ph + 1, radius, radius);
            if (rgn != IntPtr.Zero && SetWindowRgn(_panel, rgn, true) == 0) DeleteObject(rgn);
            InvalidateRect(_panel, IntPtr.Zero, false);
            UpdateWindow(_panel);
        }

        public static void SyncPanelVisibility()
        {
            if (!_running || App.Main?.IsShown == true)
            {
                if (_panelVisible) HidePanel();
            }
            else
            {
                if (!_panelVisible) ShowPanel();
                var snapshot = string.Join("|", EngineChoices.Select(EngineLabel));
                if (_engineStatusSnapshot != snapshot)
                {
                    _engineStatusSnapshot = snapshot;
                    InvalidateRect(_panel, IntPtr.Zero, false);
                }
            }
        }

        public static void SuppressOverlay()
        {
            if (_overlay != IntPtr.Zero) ShowWindow(_overlay, SW_HIDE);
            _overlayVisible = false;
        }

        public static void SetActivity(string text)
        {
            if (_activity == text) return;
            _activity = text;
            if (_panelVisible) InvalidateRect(_panel, IntPtr.Zero, false);
        }

        public static void HidePanel()
        {
            _panelVisible = false;
            _engineDropdownOpen = false;
            _hover = 0;
            if (_pressed != 0) ReleaseCapture();
            _pressed = 0;
            if (_panel != IntPtr.Zero)
            {
                ShowWindow(_panel, SW_HIDE);
            }
        }

        /// <summary>更新面板上的状态文字（主状态 + 次说明）</summary>
        public static void SetStatus(string main, string sub = "")
        {
            if (_status == main && _statusSub == sub) return;
            _status = main;
            _statusSub = sub;
            if (_panel != IntPtr.Zero && _panelVisible)
            {
                InvalidateRect(_panel, IntPtr.Zero, false);
                UpdateWindow(_panel);
            }
        }

        /// <summary>鼠标是否落在控制面板上：选窗阶段用它排除点面板造成的误锁定</summary>
        public static bool PanelContainsCursor()
        {
            if (_panel == IntPtr.Zero || !_panelVisible) return false;
            POINT pt;
            if (!GetCursorPos(out pt)) return false;
            RECT r;
            if (!GetWindowRect(_panel, out r)) return false;
            return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        }

        public static void ShowOverlay(List<OverlayLine> lines, RECT rect)
        {
            Create();
            if (_overlay == IntPtr.Zero) return;
            lock (LineLock) _lines = lines;
            _rect = rect;
            // 首次显示也遵守前台限制；保留译文供目标重新获得前台时恢复。
            if (!CanShowOverlay())
            {
                ShowWindow(_overlay, SW_HIDE);
                _overlayVisible = false;
                return;
            }
            _overlayVisible = true;
            SetWindowPos(_overlay, TopMost, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            InvalidateRect(_overlay, IntPtr.Zero, true);
            UpdateWindow(_overlay);
            RaisePanel();
        }

        public static void HideOverlay()
        {
            lock (LineLock) _lines = [];
            _overlayVisible = false;
            if (_overlay != IntPtr.Zero) ShowWindow(_overlay, SW_HIDE);
        }

        public static void HideAll()
        {
            HideOverlay();
            HidePanel();
        }

        /// <summary>看门狗：跟随窗口移动；启动器呼出时把覆盖层藏起来（否则会盖在启动器上）</summary>
        public static void Follow(RECT rect, bool suppressed)
        {
            if (_overlay == IntPtr.Zero) return;
            if (suppressed || !CanShowOverlay() || _lines.Count == 0)
            {
                if (_overlayVisible)
                {
                    ShowWindow(_overlay, SW_HIDE);
                    _overlayVisible = false;
                }
                RaisePanel();
                return;
            }
            if (!_panelVisible) ShowPanel();
            if (!_overlayVisible)
            {
                _rect = rect;
                _overlayVisible = true;
                SetWindowPos(_overlay, TopMost, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
                InvalidateRect(_overlay, IntPtr.Zero, true);
                UpdateWindow(_overlay);
                RaisePanel();
                return;
            }
            if (rect.Left == _rect.Left && rect.Top == _rect.Top &&
                rect.Right == _rect.Right && rect.Bottom == _rect.Bottom) return;
            _rect = rect;
            SetWindowPos(_overlay, TopMost, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                SWP_NOACTIVATE);
            RaisePanel();
        }

        public static void Paint(IntPtr hwnd)
        {
            PAINTSTRUCT ps;
            var dc = BeginPaint(hwnd, out ps);
            RECT client;
            GetClientRect(hwnd, out client);

            if (hwnd == _panel)
            {
                var buffer = CreateCompatibleDC(dc);
                var bitmap = CreateCompatibleBitmap(dc, client.Right, client.Bottom);
                if (buffer != IntPtr.Zero && bitmap != IntPtr.Zero)
                {
                    var previous = SelectObject(buffer, bitmap);
                    PaintPanel(buffer, client);
                    BitBlt(dc, 0, 0, client.Right, client.Bottom, buffer, 0, 0, 0x00CC0020);
                    SelectObject(buffer, previous);
                }
                else PaintPanel(dc, client);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (buffer != IntPtr.Zero) DeleteDC(buffer);
                EndPaint(hwnd, ref ps);
                return;
            }

            // 整窗先铺品红（色键透明），再在需要的区域画内容
            var brush = CreateSolidBrush(ColorKey);
            FillRect(dc, ref client, brush);
            DeleteObject(brush);

            if (hwnd == _overlay) PaintOverlay(dc, client);
            EndPaint(hwnd, ref ps);
        }

        /// <summary>右上角控制面板：标题 + 当前状态/进度 + 接口下拉 + 「开始翻译 / 停止翻译」按钮</summary>
        private static void PaintPanel(IntPtr dc, RECT client)
        {
            var w = client.Right;
            var h = client.Bottom;

            // 圆角外框 + 深色底：两层圆角填充模拟 1px 描边
            FillRound(dc, 0, 0, w, h, 12, ColOutline);
            FillRound(dc, 1, 1, w - 1, h - 1, 11, ColBackground);

            SetBkMode(dc, TRANSPARENT_BK);

            // 标题行：左侧标题，右侧接口下拉（左右排列）
            FillRound(dc, Scaled(14), Scaled(12), Scaled(18), Scaled(31), 2, ColAccent);
            var titleRect = new RECT { Left = Scaled(26), Top = Scaled(9), Right = w - Scaled(192), Bottom = Scaled(34) };
            SelectObject(dc, GetFont(Scaled(16), 600));
            SetTextColor(dc, ColText);
            DrawTextW(dc, "屏幕翻译", -1, ref titleRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);

            PaintDropdown(dc, w);
            FillBar(dc, Scaled(12), Scaled(44), w - Scaled(12), Scaled(45), ColDivider);

            if (_engineDropdownOpen)
            {
                // 展开时下拉列表接管状态区：统一卡片底 + 行内高亮，避免每行独立方块显得碎
                var first = EngineRect(0, w);
                var last = EngineRect(EngineChoices.Length - 1, w);
                FillRound(dc, first.Left, first.Top - Scaled(3), last.Right, last.Bottom + Scaled(5), 8, 0x0034303D);
                FillRound(dc, first.Left + 1, first.Top - Scaled(2), last.Right - 1, last.Bottom + Scaled(4), 7, 0x001A1721);
                for (var i = 0; i < EngineChoices.Length; i++)
                    PaintEngineRow(dc, EngineRect(i, w), EngineChoices[i]);
            }
            else
            {
                var mainRect = new RECT { Left = Scaled(16), Top = Scaled(52), Right = w - Scaled(16), Bottom = Scaled(74) };
                SelectObject(dc, GetFont(Scaled(13)));
                SetTextColor(dc, ColText);
                DrawTextW(dc, _status, -1, ref mainRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);

                if (_statusSub.Length > 0)
                {
                    var subRect = new RECT { Left = Scaled(16), Top = Scaled(74), Right = w - Scaled(16), Bottom = Scaled(92) };
                    SelectObject(dc, GetFont(Scaled(11)));
                    SetTextColor(dc, ColTextDim);
                    DrawTextW(dc, _statusSub, -1, ref subRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
                }

                var activityRect = new RECT { Left = Scaled(16), Top = Scaled(94), Right = w - Scaled(16), Bottom = Scaled(114) };
                SelectObject(dc, GetFont(Scaled(12)));
                SetTextColor(dc, ColGreen);
                DrawTextW(dc, _activity, -1, ref activityRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
            }

            var hintTop = Scaled(_engineDropdownOpen ? 232 : 124);
            var hint = new RECT { Left = Scaled(16), Top = hintTop, Right = w - Scaled(16), Bottom = hintTop + Scaled(18) };
            SelectObject(dc, GetFont(Scaled(10)));
            SetTextColor(dc, ColTextDim);
            DrawTextW(dc, "状态依据实际请求 · 切换后重新翻译", -1, ref hint, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);

            LayoutAction(w, h, out _actionRect);
            PaintPrimaryButton(dc, _actionRect, IsTranslating ? "停止翻译" : "开始翻译");
        }

        /// <summary>接口下拉：无填充的细描边样式（与面板同底，只留 1px 边框），展开/悬停时描边提亮</summary>
        private static void PaintDropdown(IntPtr dc, int w)
        {
            var rect = DropdownRect(w);
            var active = _engineDropdownOpen || _hover == 2 || _pressed == 2;
            FillRound(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, 6, active ? ColAccentLine : 0x00373341);
            FillRound(dc, rect.Left + 1, rect.Top + 1, rect.Right - 1, rect.Bottom - 1, 5,
                active ? 0x00201C28u : ColBackground);

            var labelRect = new RECT { Left = rect.Left + Scaled(11), Top = rect.Top, Right = rect.Left + Scaled(42), Bottom = rect.Bottom };
            SelectObject(dc, GetFont(Scaled(9)));
            SetTextColor(dc, ColTextDim);
            DrawTextW(dc, "接口", -1, ref labelRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);

            var valueRect = new RECT { Left = rect.Left + Scaled(44), Top = rect.Top, Right = rect.Right - Scaled(26), Bottom = rect.Bottom };
            SelectObject(dc, GetFont(Scaled(12)));
            SetTextColor(dc, ColText);
            DrawTextW(dc, EngineTitle(_selectedEngine), -1, ref valueRect,
                DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);

            // 用两条细斜线拼成 chevron，比实心三角更轻
            var cx = rect.Right - Scaled(14);
            var cy = (rect.Top + rect.Bottom) / 2;
            var color = active ? ColAccent : 0x00A5A5B0;
            if (_engineDropdownOpen)
            {
                StrokeLine(dc, cx - Scaled(4), cy + Scaled(2), cx, cy - Scaled(2), color);
                StrokeLine(dc, cx, cy - Scaled(2), cx + Scaled(4), cy + Scaled(2), color);
            }
            else
            {
                StrokeLine(dc, cx - Scaled(4), cy - Scaled(2), cx, cy + Scaled(2), color);
                StrokeLine(dc, cx, cy + Scaled(2), cx + Scaled(4), cy - Scaled(2), color);
            }
        }

        /// <summary>下拉列表项：状态圆点 + 接口名 + 右侧状态文字，选中项带强调竖线</summary>
        private static void PaintEngineRow(IntPtr dc, RECT rect, string name)
        {
            var selected = _selectedEngine == name;
            var index = Array.IndexOf(EngineChoices, name);
            var hot = _hover == index + 3 || _pressed == index + 3;
            if (selected || hot)
                FillRound(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, 6, selected ? 0x002E2A3Au : 0x00231F2Cu);
            if (selected)
                FillRound(dc, rect.Left + Scaled(4), rect.Top + Scaled(8), rect.Left + Scaled(7), rect.Bottom - Scaled(8), 1, ColAccent);

            var color = StatusColor(name == "auto" ? "auto" : EngineState(name));
            var dotY = (rect.Top + rect.Bottom) / 2;
            FillRound(dc, rect.Left + Scaled(11), dotY - Scaled(3), rect.Left + Scaled(17), dotY + Scaled(3), 3, color);

            var nameRect = new RECT { Left = rect.Left + Scaled(24), Top = rect.Top, Right = rect.Left + Scaled(102), Bottom = rect.Bottom };
            SelectObject(dc, GetFont(Scaled(12), selected ? 600 : 500));
            SetTextColor(dc, selected ? ColText : 0x00D2D2DA);
            DrawTextW(dc, EngineTitle(name), -1, ref nameRect,
                DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);

            var stateRect = new RECT { Left = rect.Left + Scaled(102), Top = rect.Top, Right = rect.Right - Scaled(12), Bottom = rect.Bottom };
            SelectObject(dc, GetFont(Scaled(10)));
            SetTextColor(dc, color);
            DrawTextW(dc, name == "auto" ? (_item?.TranslateEngine == "azure" ? "Azure" : "择优") : EngineStateShort(name),
                -1, ref stateRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_RIGHT | DT_END_ELLIPSIS);
        }

        /// <summary>底部主按钮：开始翻译用蓝色强调，翻译中用红色停止色，悬停与按下都提亮</summary>
        private static void PaintPrimaryButton(IntPtr dc, RECT rect, string text)
        {
            var hot = _hover == 1 || _pressed == 1;
            var color = IsTranslating
                ? (hot ? ColStopHot : ColStop)
                : (hot ? ColStartHot : ColStart);
            FillRound(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, 9, color);
            // 顶部高光，避免大色块显得扁平
            FillRound(dc, rect.Left + Scaled(10), rect.Top + 1, rect.Right - Scaled(10), rect.Top + 2, 1, 0x00C0D7FF);

            SelectObject(dc, GetFont(Scaled(13), 600));
            SetTextColor(dc, 0x00FFFFFF);
            DrawTextW(dc, text, -1, ref rect, DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
        }

        /// <summary>接口状态对应圆点颜色：绿=可用、灰=未检测、橙=待重试/冷却、红=最近失败</summary>
        private static uint StatusColor(string state) => state switch
        {
            "最近可用" => ColGreen,
            "未检测" => ColGrey,
            "待重试" or "冷却中" => ColAmber,
            "最近失败" => ColRed,
            "未配置" => ColGrey,
            _ => ColAccent
        };

        private static void FillRound(IntPtr dc, int left, int top, int right, int bottom, int radius, uint color)
        {
            if (right - left <= 0 || bottom - top <= 0) return;
            var rgn = CreateRoundRectRgn(left, top, right, bottom, radius * 2, radius * 2);
            if (rgn == IntPtr.Zero) return;
            var brush = CreateSolidBrush(color);
            FillRgn(dc, rgn, brush);
            DeleteObject(brush);
            DeleteObject(rgn);
        }

        private static void FillBar(IntPtr dc, int left, int top, int right, int bottom, uint color)
        {
            var rect = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
            var brush = CreateSolidBrush(color);
            FillRect(dc, ref rect, brush);
            DeleteObject(brush);
        }

        private static void StrokeLine(IntPtr dc, int x1, int y1, int x2, int y2, uint color)
        {
            var pen = CreatePen(0, 1, color); // PS_SOLID
            var old = SelectObject(dc, pen);
            MoveToEx(dc, x1, y1, IntPtr.Zero);
            LineTo(dc, x2, y2);
            SelectObject(dc, old);
            DeleteObject(pen);
        }

        /// <summary>按钮命中区域：整条底部按钮，按窗口实际像素（含 DPI 缩放）计算</summary>
        private static void LayoutAction(int w, int h, out RECT action)
        {
            var y = h - Scaled(46);
            var bh = Scaled(36);
            action = new RECT { Left = Scaled(12), Top = y, Right = w - Scaled(12), Bottom = y + bh };
        }

        private static string EngineTitle(string name) =>
            name == "auto" ? "自动" : TranslateService.EngineName(name);

        private static string EngineLabel(string name) => EngineTitle(name) + " · " + EngineStateShort(name);

        private static string EngineState(string name) =>
            TranslateService.GetEngineStatus(name, !string.IsNullOrWhiteSpace(_item?.TranslateApiKey));

        /// <summary>下拉列表用的短状态标签（窄列放不下完整描述）</summary>
        private static string EngineStateShort(string name)
        {
            var state = EngineState(name);
            return state switch
            {
                "最近可用" => "可用",
                "最近失败" => "失败",
                "冷却中" => "冷却",
                "待重试" => "重试",
                _ => state
            };
        }

        private static RECT DropdownRect(int width) => new()
        {
            Left = width - Scaled(180), Top = Scaled(10),
            Right = width - Scaled(12), Bottom = Scaled(40)
        };

        private static RECT EngineRect(int index, int width) => new()
        {
            Left = width - Scaled(180), Top = Scaled(46 + index * 30),
            Right = width - Scaled(12), Bottom = Scaled(74 + index * 30)
        };

        private static int HitTest(int x, int y)
        {
            LayoutAction(Scaled(PanelW), Scaled(PanelHeight), out var action);
            if (Inside(action, x, y)) return 1;
            if (Inside(DropdownRect(Scaled(PanelW)), x, y)) return 2;
            if (_engineDropdownOpen)
                for (var i = 0; i < EngineChoices.Length; i++)
                    if (Inside(EngineRect(i, Scaled(PanelW)), x, y)) return i + 3;
            return 0;
        }

        /// <summary>面板鼠标按下：记录按下的按钮并重绘出按压效果</summary>
        public static void OnPanelMouseDown(IntPtr lParam)
        {
            var x = (short)(lParam & 0xFFFF);
            var y = (short)((lParam >> 16) & 0xFFFF);
            var hit = HitTest(x, y);
            if (hit == _pressed) return;
            _pressed = hit;
            // 捕获鼠标：按下后移到面板外再松开也能收到抬起消息，避免按钮卡在按下的样子
            if (hit != 0) SetCapture(_panel);
            InvalidateRect(_panel, IntPtr.Zero, false);
        }

        /// <summary>面板鼠标抬起：在按钮内抬起才触发动作</summary>
        public static void OnPanelMouseUp(IntPtr lParam)
        {
            var x = (short)(lParam & 0xFFFF);
            var y = (short)((lParam >> 16) & 0xFFFF);
            var hit = HitTest(x, y);
            var armed = _pressed;
            _pressed = 0;
            if (armed != 0) ReleaseCapture();
            if (_panel != IntPtr.Zero) InvalidateRect(_panel, IntPtr.Zero, false);
            if (hit == 0 || hit != armed) return;

            if (hit == 2)
            {
                _engineDropdownOpen = !_engineDropdownOpen;
                ResizePanel();
                return;
            }
            if (hit >= 3)
            {
                var engine = EngineChoices[hit - 3];
                _engineDropdownOpen = false;
                ResizePanel();
                if (engine == _selectedEngine) return;
                if (engine == "azure" && string.IsNullOrWhiteSpace(_item?.TranslateApiKey))
                {
                    SetStatus("Azure 未配置", "请先在编辑页填写密钥");
                    return;
                }
                _selectedEngine = engine;
                if (IsTranslating) BeginTranslation();
                else SetActivity("已选择：" + (engine == "auto" ? "自动" : TranslateService.EngineName(engine)) + "（尚未请求）");
                return;
            }
            // 按钮在两种状态间切换：未翻译→开始翻译；翻译中→停止并恢复原状
            if (IsTranslating) StopTranslating();
            else StartTranslating();
        }

        /// <summary>面板鼠标移动：更新悬停高亮（变化时才重绘，避免每次移动都刷新）</summary>
        public static void OnPanelMouseMove(IntPtr lParam)
        {
            var x = (short)(lParam & 0xFFFF);
            var y = (short)((lParam >> 16) & 0xFFFF);
            var hit = HitTest(x, y);
            if (hit != _hover)
            {
                _hover = hit;
                if (_panel != IntPtr.Zero) InvalidateRect(_panel, IntPtr.Zero, false);
            }
            if (_trackingLeave) return;
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = 0x00000002, // TME_LEAVE
                hwndTrack = _panel
            };
            _trackingLeave = TrackMouseEvent(ref tme);
        }

        /// <summary>鼠标离开面板：清掉悬停态</summary>
        public static void OnPanelMouseLeave()
        {
            _trackingLeave = false;
            if (_hover == 0) return;
            _hover = 0;
            if (_panel != IntPtr.Zero) InvalidateRect(_panel, IntPtr.Zero, false);
        }

        private static bool Inside(RECT r, int x, int y) =>
            x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

        private static void PaintOverlay(IntPtr dc, RECT client)
        {
            List<OverlayLine> lines;
            lock (LineLock) lines = _lines;
            if (lines.Count == 0) return;

            SetBkMode(dc, TRANSPARENT_BK);
            foreach (var line in lines)
            {
                // 先用采样到的背景色盖住原文，再把译文写在原位
                var box = new RECT
                {
                    Left = line.X,
                    Top = line.Y,
                    Right = line.X + line.W,
                    Bottom = line.Y + line.H
                };
                var brush = CreateSolidBrush(line.Bg);
                FillRect(dc, ref box, brush);
                DeleteObject(brush);

                var size = Math.Max(10, Math.Min(72, (int)(line.H * 0.82)));
                var font = GetFont(size);
                var old = SelectObject(dc, font);
                var avail = Math.Max(8, line.W - 4);
                while (size > 10)
                {
                    SIZE extent;
                    GetTextExtentPoint32W(dc, line.Text, line.Text.Length, out extent);
                    if (extent.cx <= avail) break;
                    size -= 2;
                    font = GetFont(size);
                    SelectObject(dc, font);
                }

                SetTextColor(dc, line.Fg);
                var textRect = box;
                textRect.Left += 2;
                textRect.Right -= 2;
                DrawTextW(dc, line.Text, -1, ref textRect,
                    DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
                SelectObject(dc, old);
            }
        }
    }

    private static readonly Dictionary<(int Height, int Weight), IntPtr> FontCache = [];

    /// <summary>按像素高度与字重缓存字体；charset 用 DEFAULT_CHARSET(1)，中/日/英都由系统字体回退处理</summary>
    private static IntPtr GetFont(int height, int weight = 500)
    {
        height = Math.Max(8, Math.Min(96, height));
        if (FontCache.TryGetValue((height, weight), out var cached)) return cached;
        var font = CreateFontW(-height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 0, 0, "Microsoft YaHei UI");
        FontCache[(height, weight)] = font;
        return font;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x000F: // WM_PAINT
                Hud.Paint(hwnd);
                return IntPtr.Zero;
            case 0x0014: // WM_ERASEBKGND
                return (IntPtr)1;
            case 0x0084: // WM_NCHITTEST
                // 控制面板必须返回 HTCLIENT(1) 才会收到鼠标消息；返回 HTNOWHERE(0) 会被当成点在空白处
                return hwnd == Hud.PanelHandle ? (IntPtr)1 : (IntPtr)(-1);
            case 0x0201: // WM_LBUTTONDOWN
                if (hwnd == Hud.PanelHandle) Hud.OnPanelMouseDown(lParam);
                return IntPtr.Zero;
            case 0x0200: // WM_MOUSEMOVE
                if (hwnd == Hud.PanelHandle) Hud.OnPanelMouseMove(lParam);
                return IntPtr.Zero;
            case 0x02A3: // WM_MOUSELEAVE
                if (hwnd == Hud.PanelHandle) Hud.OnPanelMouseLeave();
                return IntPtr.Zero;
            case 0x0202: // WM_LBUTTONUP
                if (hwnd == Hud.PanelHandle) Hud.OnPanelMouseUp(lParam);
                return IntPtr.Zero;
            case 0x0021: // WM_MOUSEACTIVATE
                return (IntPtr)3; // MA_NOACTIVATE：永不抢前台
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseW, int ellipseH);
    [DllImport("gdi32.dll")] private static extern bool FillRgn(IntPtr dc, IntPtr region, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] private static extern bool LineTo(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    private static IntPtr TopMost => new(-1); // HWND_TOPMOST

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public RECT rcPaint;
        public int fRestore; public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    // 注意：SetBkMode / SetTextColor / CreateSolidBrush / CreateFontW / GetTextExtentPoint32W 都由 gdi32.dll 导出，
    // 声明成 user32.dll 会在调用时抛 EntryPointNotFoundException（0x80131523），在窗口过程里直接崩进程。
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr dc, string text, int count, ref RECT rect, int format);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int esc, int orient, int weight,
        int italic, int underline, int strike, int charset, int outPrecision, int clipPrecision,
        int quality, int pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out SIZE size);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines,
        [Out] byte[] bits, ref BITMAPINFO info, uint usage);
}
